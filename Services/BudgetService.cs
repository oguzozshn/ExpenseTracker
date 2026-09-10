using Microsoft.EntityFrameworkCore;
using ExpenseTracker.Data;

namespace ExpenseTracker.Services;

public record BudgetStatus(Category Category, long Limit, long Spent)
{
    public long Remaining => Limit - Spent;

    /// <summary>0-1 for the bar width; the raw ratio is exposed separately for the label.</summary>
    public double Fill => Limit <= 0 ? 0 : Math.Clamp(Spent / (double)Limit, 0, 1);

    public double Ratio => Limit <= 0 ? 0 : Spent / (double)Limit;

    public bool Over => Limit > 0 && Spent > Limit;

    /// <summary>True once more than 85% is used but the limit is not yet broken.</summary>
    public bool Near => !Over && Ratio >= 0.85;

    /// <summary>
    /// Where this category lands at the current pace. Compared with the limit it
    /// answers "am I going to blow this budget", which the plain bar cannot.
    /// </summary>
    public long Projected(Period period, DateOnly today)
    {
        var elapsed = period.ElapsedDays(today);
        return elapsed <= 0 ? 0 : Spent / elapsed * period.DayCount;
    }
}

/// <summary>A category's typical monthly spend, and the limit standing against it.</summary>
public record CategorySpend(Category Category, long MonthlyAverage, long? Limit)
{
    public bool Budgeted => Limit is > 0;

    /// <summary>A limit on a category nothing has been spent in. It inflates the totals and tracks nothing.</summary>
    public bool Dormant => Budgeted && MonthlyAverage == 0;
}

/// <summary>
/// How much of the spending is actually under a budget. Without this the page can
/// show a healthy set of bars while most of the money leaves through categories
/// that were never given a limit — which is the failure mode budgets are for.
/// </summary>
public record BudgetCoverage(IReadOnlyList<CategorySpend> Categories, int Periods)
{
    public long BudgetedAverage => Categories.Where(c => c.Budgeted).Sum(c => c.MonthlyAverage);

    public long TotalAverage => Categories.Sum(c => c.MonthlyAverage);

    public long UncoveredAverage => TotalAverage - BudgetedAverage;

    /// <summary>0-1. Share of typical monthly spending that sits inside a budget.</summary>
    public double Ratio => TotalAverage <= 0 ? 0 : BudgetedAverage / (double)TotalAverage;

    /// <summary>Unbudgeted categories that actually cost something, dearest first.</summary>
    public IEnumerable<CategorySpend> Uncovered =>
        Categories.Where(c => !c.Budgeted && c.MonthlyAverage > 0).OrderByDescending(c => c.MonthlyAverage);

    public IEnumerable<CategorySpend> DormantBudgets => Categories.Where(c => c.Dormant);
}

public class BudgetService(IDbContextFactory<AppDbContext> dbf, TransactionService transactions)
{
    /// <summary>Complete periods averaged over when suggesting a limit.</summary>
    private const int LookBack = 6;

    public async Task<Dictionary<int, long>> LimitsAsync()
    {
        await using var db = await dbf.CreateDbContextAsync();
        return await db.Budgets.AsNoTracking().ToDictionaryAsync(b => b.CategoryId, b => b.MonthlyLimitMinor);
    }

    /// <summary>A limit of zero or less removes the budget.</summary>
    public async Task SetAsync(int categoryId, long limitMinor)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var row = await db.Budgets.FirstOrDefaultAsync(b => b.CategoryId == categoryId);

        if (limitMinor <= 0)
        {
            if (row is not null) db.Budgets.Remove(row);
        }
        else if (row is null)
        {
            db.Budgets.Add(new Budget { CategoryId = categoryId, MonthlyLimitMinor = limitMinor });
        }
        else
        {
            row.MonthlyLimitMinor = limitMinor;
        }

        await db.SaveChangesAsync();
    }

    /// <summary>Budgeted categories with their spend in the period, worst offender first.</summary>
    public async Task<List<BudgetStatus>> StatusAsync(Period period)
    {
        await using var db = await dbf.CreateDbContextAsync();

        var budgets = await db.Budgets.AsNoTracking().Include(b => b.Category).ToListAsync();
        if (budgets.Count == 0) return [];

        var spend = await transactions.SpendByCategoryAsync(period);

        return budgets
            .Where(b => b.Category is not null)
            .Select(b => new BudgetStatus(b.Category!, b.MonthlyLimitMinor, spend.GetValueOrDefault(b.CategoryId)))
            .OrderByDescending(s => s.Ratio)
            .ToList();
    }

    /// <summary>
    /// Typical monthly spend per expense category over the last complete periods,
    /// paired with whatever limit is set. Drives both the coverage panel and the
    /// suggested limits, so the user is never asked to guess a number the ledger
    /// already knows.
    /// </summary>
    public async Task<BudgetCoverage> CoverageAsync(Period current)
    {
        // The current period is part-elapsed; averaging it in would understate
        // every category and suggest limits that are too tight to survive a month.
        var periods = new List<Period>();
        for (var i = LookBack; i >= 1; i--) periods.Add(current.Shift(-i));

        var window = new Period(periods[0].Start, periods[^1].End);

        await using var db = await dbf.CreateDbContextAsync();

        var categories = await db.Categories.AsNoTracking()
            .Where(c => c.Kind == EntryKind.Expense && !c.Archived)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .ToListAsync();

        var rows = await db.Transactions.AsNoTracking()
            .Where(t => t.Kind == EntryKind.Expense
                        && !t.IsTransfer
                        && t.OccurredOn >= window.Start
                        && t.OccurredOn <= window.End)
            .Select(t => new { t.CategoryId, t.OccurredOn, t.AmountMinor })
            .ToListAsync();

        var limits = await db.Budgets.AsNoTracking()
            .ToDictionaryAsync(b => b.CategoryId, b => b.MonthlyLimitMinor);

        var totals = rows.GroupBy(r => r.CategoryId)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.AmountMinor));

        // Divided by the periods the ledger was actually running, not by the
        // periods each category happened to appear in: a budget is a monthly
        // allowance, so a category bought once in five months averages a fifth
        // of it, not the whole thing.
        var active = periods.Count(p => rows.Any(r => r.OccurredOn >= p.Start && r.OccurredOn <= p.End));

        var spends = categories
            .Select(c => new CategorySpend(
                Category: c,
                MonthlyAverage: active == 0 ? 0 : totals.GetValueOrDefault(c.Id) / active,
                Limit: limits.TryGetValue(c.Id, out var l) ? l : null))
            .ToList();

        return new BudgetCoverage(spends, active);
    }
}
