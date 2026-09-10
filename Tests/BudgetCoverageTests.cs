using ExpenseTracker.Data;
using ExpenseTracker.Services;

namespace ExpenseTracker.Tests;

/// <summary>
/// Coverage is the number that tells the user their budgets are not watching
/// their money. If it reads high while most spending is unbudgeted, the page is
/// worse than having no page at all.
/// </summary>
public class BudgetCoverageTests
{
    private static CategorySpend Spend(string name, long average, long? limit = null) =>
        new(new Category { Id = name.GetHashCode(), Name = name }, average, limit);

    private static BudgetCoverage Coverage(params CategorySpend[] categories) => new(categories, Periods: 5);

    [Fact]
    public void Coverage_is_the_budgeted_share_of_typical_spending()
    {
        var cov = Coverage(
            Spend("Market", 15_000_00),
            Spend("Abonelikler", 500_00, limit: 2_500_00));

        Assert.Equal(500_00, cov.BudgetedAverage);
        Assert.Equal(15_500_00, cov.TotalAverage);
        Assert.Equal(15_000_00, cov.UncoveredAverage);

        // The limit is five times the spend, but coverage counts what is watched,
        // not how generously — otherwise a big enough limit would read as 100%.
        Assert.InRange(cov.Ratio, 0.03, 0.033);
    }

    [Fact]
    public void A_limit_on_a_category_with_no_spending_is_dormant()
    {
        var cov = Coverage(
            Spend("Market", 15_000_00),
            Spend("Mecburi", 0, limit: 51_500_00));

        var dormant = Assert.Single(cov.DormantBudgets);
        Assert.Equal("Mecburi", dormant.Category.Name);

        // A dormant budget must not flatter the ratio: it contributes a limit but
        // no watched spending.
        Assert.Equal(0, cov.BudgetedAverage);
    }

    [Fact]
    public void An_unbudgeted_category_with_no_spending_is_not_reported_as_uncovered()
    {
        // Nagging about an empty category the user never uses is noise.
        var cov = Coverage(Spend("Market", 15_000_00), Spend("Kullanılmayan", 0));

        var only = Assert.Single(cov.Uncovered);
        Assert.Equal("Market", only.Category.Name);
    }

    [Fact]
    public void Uncovered_categories_come_back_dearest_first()
    {
        var cov = Coverage(
            Spend("Ulaşım", 838_00),
            Spend("Market", 14_226_00),
            Spend("Yeme-İçme", 3_876_00));

        Assert.Equal(
            ["Market", "Yeme-İçme", "Ulaşım"],
            cov.Uncovered.Select(c => c.Category.Name));
    }

    [Fact]
    public void Full_coverage_reads_as_one()
    {
        var cov = Coverage(
            Spend("Market", 15_000_00, limit: 16_000_00),
            Spend("Ulaşım", 800_00, limit: 1_000_00));

        Assert.Equal(1.0, cov.Ratio, 0.001);
        Assert.Empty(cov.Uncovered);
        Assert.Equal(0, cov.UncoveredAverage);
    }

    [Fact]
    public void An_empty_ledger_is_zero_rather_than_a_divide_by_zero()
    {
        var cov = Coverage(Spend("Market", 0), Spend("Ulaşım", 0));

        Assert.Equal(0, cov.Ratio);
        Assert.Equal(0, cov.TotalAverage);
    }

    [Fact]
    public void A_zero_limit_does_not_count_as_budgeted()
    {
        // SetAsync deletes on a zero limit, but a stale row must not be read as
        // a budget that covers anything.
        var cov = Coverage(Spend("Market", 15_000_00, limit: 0));

        Assert.Equal(0, cov.BudgetedAverage);
        Assert.Single(cov.Uncovered);
        Assert.Empty(cov.DormantBudgets);
    }
}
