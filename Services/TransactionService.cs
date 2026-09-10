using System.Text;
using Microsoft.EntityFrameworkCore;
using ExpenseTracker.Data;

namespace ExpenseTracker.Services;

public record TransactionFilter(
    Period? Period = null,
    int? CategoryId = null,
    EntryKind? Kind = null,
    string? Search = null,
    int? AccountId = null,
    int? ImportBatchId = null,
    int Take = 500);

public record CategoryTotal(
    int CategoryId,
    string Name,
    string Color,
    string Icon,
    EntryKind Kind,
    long Amount,
    int Count);

public record DayTotal(DateOnly Date, long Expense, long Income);

public record LedgerStats(int Transactions, int Categories, int Rules, DateOnly? First, DateOnly? Last);

public record PeriodSummary(
    Period Period,
    long Expense,
    long Income,
    int Count,
    IReadOnlyList<CategoryTotal> ByCategory,
    IReadOnlyList<DayTotal> Daily)
{
    public long Net => Income - Expense;

    /// <summary>Average daily spend over the days that have already happened.</summary>
    public long DailyPace(DateOnly today)
    {
        var days = Period.ElapsedDays(today);
        return days <= 0 ? 0 : Expense / days;
    }

    /// <summary>Where the spend lands if the current pace holds to the end of the period.</summary>
    public long Projected(DateOnly today) => DailyPace(today) * Period.DayCount;

    public static PeriodSummary Empty(Period period) =>
        new(period, 0, 0, 0, [], []);
}

public class TransactionService(IDbContextFactory<AppDbContext> dbf)
{
    public async Task<List<Transaction>> ListAsync(TransactionFilter filter)
    {
        await using var db = await dbf.CreateDbContextAsync();
        return await Apply(db.Transactions.AsNoTracking().Include(t => t.Category).Include(t => t.Account), filter)
            .OrderByDescending(t => t.OccurredOn)
            .ThenByDescending(t => t.Id)
            .Take(filter.Take)
            .ToListAsync();
    }

    public async Task<Transaction?> GetAsync(long id)
    {
        await using var db = await dbf.CreateDbContextAsync();
        return await db.Transactions.AsNoTracking().Include(t => t.Category)
            .FirstOrDefaultAsync(t => t.Id == id);
    }

    public async Task<PeriodSummary> SummaryAsync(Period period)
    {
        await using var db = await dbf.CreateDbContextAsync();

        // Every report below drops transfers: money moved from the bank to the card
        // is not income on one side and spending on the other, it is the same lira.
        var rows = await db.Transactions.AsNoTracking()
            .Where(t => !t.IsTransfer && t.OccurredOn >= period.Start && t.OccurredOn <= period.End)
            .GroupBy(t => new { t.CategoryId, t.Kind })
            .Select(g => new
            {
                g.Key.CategoryId,
                g.Key.Kind,
                Amount = g.Sum(t => t.AmountMinor),
                Count = g.Count()
            })
            .ToListAsync();

        if (rows.Count == 0) return PeriodSummary.Empty(period);

        var ids = rows.Select(r => r.CategoryId).Distinct().ToList();
        var categories = await db.Categories.AsNoTracking()
            .Where(c => ids.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id);

        var byCategory = rows
            .Select(r =>
            {
                categories.TryGetValue(r.CategoryId, out var c);
                return new CategoryTotal(
                    r.CategoryId,
                    c?.Name ?? "Silinmiş kategori",
                    c?.Color ?? "#8b93a3",
                    c?.Icon ?? "",
                    r.Kind,
                    r.Amount,
                    r.Count);
            })
            .OrderByDescending(t => t.Amount)
            .ToList();

        var daily = await db.Transactions.AsNoTracking()
            .Where(t => !t.IsTransfer && t.OccurredOn >= period.Start && t.OccurredOn <= period.End)
            .GroupBy(t => t.OccurredOn)
            .Select(g => new
            {
                Date = g.Key,
                Expense = g.Where(t => t.Kind == EntryKind.Expense).Sum(t => (long?)t.AmountMinor) ?? 0,
                Income = g.Where(t => t.Kind == EntryKind.Income).Sum(t => (long?)t.AmountMinor) ?? 0
            })
            .ToListAsync();

        var dailyByDate = daily.ToDictionary(d => d.Date);

        // Filled in day by day so the chart keeps a flat baseline on days with
        // no spending instead of squeezing them out of the axis.
        var series = new List<DayTotal>(period.DayCount);
        for (var date = period.Start; date <= period.End; date = date.AddDays(1))
        {
            dailyByDate.TryGetValue(date, out var d);
            series.Add(new DayTotal(date, d?.Expense ?? 0, d?.Income ?? 0));
        }

        return new PeriodSummary(
            period,
            byCategory.Where(t => t.Kind == EntryKind.Expense).Sum(t => t.Amount),
            byCategory.Where(t => t.Kind == EntryKind.Income).Sum(t => t.Amount),
            rows.Sum(r => r.Count),
            byCategory,
            series);
    }

    /// <summary>Totals for the <paramref name="count"/> periods ending with <paramref name="last"/>, oldest first.</summary>
    public async Task<List<(Period Period, long Expense, long Income)>> TrendAsync(Period last, int count)
    {
        var first = last.Shift(-(count - 1));

        await using var db = await dbf.CreateDbContextAsync();
        var rows = await db.Transactions.AsNoTracking()
            .Where(t => !t.IsTransfer && t.OccurredOn >= first.Start && t.OccurredOn <= last.End)
            .Select(t => new { t.OccurredOn, t.Kind, t.AmountMinor })
            .ToListAsync();

        var result = new List<(Period, long, long)>(count);
        for (var i = 0; i < count; i++)
        {
            var period = first.Shift(i);
            var inPeriod = rows.Where(r => r.OccurredOn >= period.Start && r.OccurredOn <= period.End).ToList();
            result.Add((
                period,
                inPeriod.Where(r => r.Kind == EntryKind.Expense).Sum(r => r.AmountMinor),
                inPeriod.Where(r => r.Kind == EntryKind.Income).Sum(r => r.AmountMinor)));
        }
        return result;
    }

    /// <summary>Spend per category inside a period, expenses only. Used by the budget page.</summary>
    public async Task<Dictionary<int, long>> SpendByCategoryAsync(Period period)
    {
        await using var db = await dbf.CreateDbContextAsync();
        return await db.Transactions.AsNoTracking()
            .Where(t => !t.IsTransfer && t.Kind == EntryKind.Expense
                        && t.OccurredOn >= period.Start && t.OccurredOn <= period.End)
            .GroupBy(t => t.CategoryId)
            .Select(g => new { g.Key, Amount = g.Sum(t => t.AmountMinor) })
            .ToDictionaryAsync(x => x.Key, x => x.Amount);
    }

    public async Task<long> SaveAsync(Transaction input)
    {
        await using var db = await dbf.CreateDbContextAsync();

        var category = await db.Categories.FirstOrDefaultAsync(c => c.Id == input.CategoryId)
            ?? throw new InvalidOperationException("Kategori seçilmedi.");

        Transaction row;
        if (input.Id == 0)
        {
            row = new Transaction { CreatedAtUtc = DateTime.UtcNow };
            db.Transactions.Add(row);
        }
        else
        {
            row = await db.Transactions.FirstOrDefaultAsync(t => t.Id == input.Id)
                  ?? throw new InvalidOperationException($"İşlem {input.Id} bulunamadı.");
        }

        row.CategoryId = category.Id;
        row.Kind = category.Kind;
        row.AmountMinor = Math.Abs(input.AmountMinor);
        row.OccurredOn = input.OccurredOn;
        row.Note = input.Note.Trim();
        row.Payee = input.Payee.Trim();
        row.Method = input.Method;
        row.AccountId = input.AccountId;
        row.IsTransfer = input.IsTransfer;

        await db.SaveChangesAsync();
        return row.Id;
    }

    public async Task DeleteAsync(long id)
    {
        await using var db = await dbf.CreateDbContextAsync();
        await db.Transactions.Where(t => t.Id == id).ExecuteDeleteAsync();
    }

    /// <summary>Distinct payees seen recently, for the datalist on the entry form.</summary>
    public async Task<List<string>> RecentPayeesAsync(int take = 40)
    {
        await using var db = await dbf.CreateDbContextAsync();
        return await db.Transactions.AsNoTracking()
            .Where(t => t.Payee != "")
            .OrderByDescending(t => t.Id)
            .Select(t => t.Payee)
            .Distinct()
            .Take(take)
            .ToListAsync();
    }

    public async Task<string> ExportCsvAsync(TransactionFilter filter)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var rows = await Apply(
                db.Transactions.AsNoTracking().Include(t => t.Category).Include(t => t.Account),
                filter with { Take = int.MaxValue })
            .OrderBy(t => t.OccurredOn).ThenBy(t => t.Id)
            .ToListAsync();

        // Semicolon separated and with a UTF-8 BOM, because that is what Excel
        // in a Turkish locale opens without an import wizard.
        var sb = new StringBuilder();
        sb.AppendLine("Tarih;Tür;Hesap;Kategori;Tutar;Ödeme;Karşı Taraf;Not");
        foreach (var t in rows)
        {
            sb.Append(t.OccurredOn.ToString("yyyy-MM-dd")).Append(';')
              .Append(t.IsTransfer ? "Transfer" : t.Kind == EntryKind.Income ? "Gelir" : "Gider").Append(';')
              .Append(Csv(t.Account?.Name ?? "")).Append(';')
              .Append(Csv(t.Category?.Name ?? "")).Append(';')
              .Append(Money.ToDecimal(t.AmountMinor).ToString("0.00", Money.Culture)).Append(';')
              .Append(MethodLabel(t.Method)).Append(';')
              .Append(Csv(t.Payee)).Append(';')
              .Append(Csv(t.Note)).AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>Counts for the settings page, so the user can see the file is not empty.</summary>
    public async Task<LedgerStats> StatsAsync()
    {
        await using var db = await dbf.CreateDbContextAsync();
        var count = await db.Transactions.CountAsync();
        return new LedgerStats(
            count,
            await db.Categories.CountAsync(),
            await db.RecurringRules.CountAsync(),
            count == 0 ? null : await db.Transactions.MinAsync(t => t.OccurredOn),
            count == 0 ? null : await db.Transactions.MaxAsync(t => t.OccurredOn));
    }

    public static string MethodLabel(PaymentMethod method) => method switch
    {
        PaymentMethod.Cash => "Nakit",
        PaymentMethod.Card => "Kart",
        PaymentMethod.Transfer => "Havale",
        _ => "Diğer"
    };

    private static string Csv(string value) =>
        value.Contains(';') || value.Contains('"') || value.Contains('\n')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;

    private static IQueryable<Transaction> Apply(IQueryable<Transaction> q, TransactionFilter f)
    {
        if (f.Period is { } p)
            q = q.Where(t => t.OccurredOn >= p.Start && t.OccurredOn <= p.End);

        if (f.CategoryId is { } categoryId)
            q = q.Where(t => t.CategoryId == categoryId);

        if (f.Kind is { } kind)
            q = q.Where(t => t.Kind == kind);

        if (f.AccountId is { } accountId)
            q = q.Where(t => t.AccountId == accountId);

        if (f.ImportBatchId is { } batchId)
            q = q.Where(t => t.ImportBatchId == batchId);

        if (!string.IsNullOrWhiteSpace(f.Search))
        {
            // The user's text is data, not pattern: escape the LIKE wildcards.
            var term = f.Search.Trim()
                .Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            var pattern = $"%{term}%";
            q = q.Where(t =>
                EF.Functions.Like(t.Payee, pattern, "\\") ||
                EF.Functions.Like(t.Note, pattern, "\\"));
        }

        return q;
    }
}
