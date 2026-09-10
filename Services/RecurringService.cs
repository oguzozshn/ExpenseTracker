using Microsoft.EntityFrameworkCore;
using ExpenseTracker.Data;

namespace ExpenseTracker.Services;

public class RecurringService(IDbContextFactory<AppDbContext> dbf, ILogger<RecurringService> log)
{
    /// <summary>
    /// A single pass never writes more than this many occurrences per rule. It
    /// only bites when someone back-dates a rule by years, and it keeps that
    /// typo from silently filling the ledger with a decade of rent.
    /// </summary>
    private const int MaxOccurrencesPerPass = 120;

    public async Task<List<RecurringRule>> ListAsync()
    {
        await using var db = await dbf.CreateDbContextAsync();
        return await db.RecurringRules.AsNoTracking().Include(r => r.Category).Include(r => r.Account)
            .OrderBy(r => r.Paused).ThenBy(r => r.DayOfMonth).ThenBy(r => r.Name)
            .ToListAsync();
    }

    public async Task<RecurringRule?> GetAsync(int id)
    {
        await using var db = await dbf.CreateDbContextAsync();
        return await db.RecurringRules.AsNoTracking().Include(r => r.Category).Include(r => r.Account)
            .FirstOrDefaultAsync(r => r.Id == id);
    }

    public async Task<int> SaveAsync(RecurringRule input)
    {
        await using var db = await dbf.CreateDbContextAsync();

        var category = await db.Categories.FirstOrDefaultAsync(c => c.Id == input.CategoryId)
            ?? throw new InvalidOperationException("Kategori seçilmedi.");

        RecurringRule row;
        if (input.Id == 0)
        {
            row = new RecurringRule();
            db.RecurringRules.Add(row);
        }
        else
        {
            row = await db.RecurringRules.FirstOrDefaultAsync(r => r.Id == input.Id)
                  ?? throw new InvalidOperationException($"Kayıt {input.Id} bulunamadı.");
        }

        row.Name = input.Name.Trim();
        row.CategoryId = category.Id;
        row.Kind = category.Kind;
        row.AmountMinor = Math.Abs(input.AmountMinor);
        row.DayOfMonth = Math.Clamp(input.DayOfMonth, 1, 31);
        row.Payee = input.Payee.Trim();
        row.Method = input.Method;
        row.AccountId = input.AccountId;
        row.StartDate = input.StartDate;
        row.EndDate = input.EndDate;
        row.Paused = input.Paused;

        await db.SaveChangesAsync();
        return row.Id;
    }

    public async Task SetPausedAsync(int id, bool paused)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var row = await db.RecurringRules.FirstOrDefaultAsync(r => r.Id == id);
        if (row is null) return;
        row.Paused = paused;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Removes the rule. Transactions it already produced stay: they are real
    /// money that was really spent, and deleting them would rewrite history.
    /// </summary>
    public async Task DeleteAsync(int id)
    {
        await using var db = await dbf.CreateDbContextAsync();
        await db.Transactions.Where(t => t.RecurringRuleId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RecurringRuleId, (int?)null));
        await db.RecurringRules.Where(r => r.Id == id).ExecuteDeleteAsync();
    }

    /// <summary>Next date the rule fires on or after <paramref name="from"/>, or null when it is finished.</summary>
    public static DateOnly? NextOccurrence(RecurringRule rule, DateOnly from)
    {
        if (rule.Paused) return null;

        var anchor = from < rule.StartDate ? rule.StartDate : from;

        var candidate = DayInMonth(anchor.Year, anchor.Month, rule.DayOfMonth);
        if (candidate < anchor)
        {
            var m = anchor.AddMonths(1);
            candidate = DayInMonth(m.Year, m.Month, rule.DayOfMonth);
        }
        if (candidate < rule.StartDate)
        {
            var m = candidate.AddMonths(1);
            candidate = DayInMonth(m.Year, m.Month, rule.DayOfMonth);
        }

        return rule.EndDate is { } end && candidate > end ? null : candidate;
    }

    /// <summary>
    /// The given day of the month, clamped to that month's real length. Computed
    /// fresh for every month rather than carried forward through AddMonths, so a
    /// rule set to 31 lands on the last day of every month — including rolling
    /// straight from a 30-day month into a 31-day one — instead of getting stuck
    /// at whatever a previous short month clamped it to.
    /// </summary>
    private static DateOnly DayInMonth(int year, int month, int day) =>
        new(year, month, Math.Min(day, DateTime.DaysInMonth(year, month)));

    /// <summary>True when the end date cuts the rule off before a second occurrence could ever fire.</summary>
    public static bool IsOneTime(RecurringRule rule) =>
        rule.EndDate is { } end
        && NextOccurrence(rule, rule.StartDate) is { } first
        && end < first.AddMonths(1);

    /// <summary>
    /// Writes every occurrence that is due on or before <paramref name="today"/>
    /// and not already present. Safe to call repeatedly: the unique index on
    /// (RecurringRuleId, OccurredOn) is what makes a replay a no-op, so a machine
    /// that was off for a week catches up on the next start without duplicating.
    /// </summary>
    public async Task<int> GenerateDueAsync(DateOnly today)
    {
        await using var db = await dbf.CreateDbContextAsync();

        var rules = await db.RecurringRules.Where(r => !r.Paused).ToListAsync();
        if (rules.Count == 0) return 0;

        var written = 0;

        foreach (var rule in rules)
        {
            var existing = (await db.Transactions
                    .Where(t => t.RecurringRuleId == rule.Id)
                    .Select(t => t.OccurredOn)
                    .ToListAsync())
                .ToHashSet();

            var date = NextOccurrence(rule, rule.StartDate);
            var produced = 0;

            while (date is { } due && due <= today && produced < MaxOccurrencesPerPass)
            {
                if (!existing.Contains(due))
                {
                    db.Transactions.Add(new Transaction
                    {
                        CategoryId = rule.CategoryId,
                        Kind = rule.Kind,
                        AmountMinor = rule.AmountMinor,
                        OccurredOn = due,
                        Note = rule.Name,
                        Payee = rule.Payee,
                        Method = rule.Method,
                        AccountId = rule.AccountId,
                        CreatedAtUtc = DateTime.UtcNow,
                        RecurringRuleId = rule.Id
                    });
                    written++;
                    produced++;
                }

                if (rule.LastGeneratedOn is null || due > rule.LastGeneratedOn) rule.LastGeneratedOn = due;
                date = NextOccurrence(rule, due.AddDays(1));
            }

            if (produced >= MaxOccurrencesPerPass)
                log.LogWarning("Düzenli kayıt {Rule} tek geçişte {Max} işlem üretti, kalanlar sonraki geçişe bırakıldı.",
                    rule.Name, MaxOccurrencesPerPass);
        }

        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync();

        if (written > 0) log.LogInformation("{Count} düzenli işlem oluşturuldu.", written);
        return written;
    }
}
