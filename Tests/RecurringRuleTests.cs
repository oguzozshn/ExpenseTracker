using ExpenseTracker.Data;
using ExpenseTracker.Services;

namespace ExpenseTracker.Tests;

/// <summary>
/// The occurrence walk drives the catch-up loop, so an off-by-one here either
/// skips a month of rent or spins forever.
/// </summary>
public class RecurringRuleTests
{
    private static RecurringRule Rule(int day, DateOnly start, DateOnly? end = null, bool paused = false) =>
        new() { DayOfMonth = day, StartDate = start, EndDate = end, Paused = paused };

    [Fact]
    public void The_first_occurrence_is_not_before_the_start_date()
    {
        var rule = Rule(1, new DateOnly(2026, 6, 15));
        Assert.Equal(new DateOnly(2026, 7, 1), RecurringService.NextOccurrence(rule, rule.StartDate));
    }

    [Fact]
    public void A_start_date_on_the_day_itself_fires_that_day()
    {
        var rule = Rule(1, new DateOnly(2026, 6, 1));
        Assert.Equal(new DateOnly(2026, 6, 1), RecurringService.NextOccurrence(rule, rule.StartDate));
    }

    [Fact]
    public void Each_step_moves_strictly_forward()
    {
        var rule = Rule(28, new DateOnly(2026, 1, 28));

        var date = RecurringService.NextOccurrence(rule, rule.StartDate);
        var seen = new List<DateOnly>();
        while (date is { } d && d <= new DateOnly(2026, 4, 30))
        {
            seen.Add(d);
            date = RecurringService.NextOccurrence(rule, d.AddDays(1));
        }

        Assert.Equal(
            [
                new DateOnly(2026, 1, 28),
                new DateOnly(2026, 2, 28),
                new DateOnly(2026, 3, 28),
                new DateOnly(2026, 4, 28)
            ],
            seen);
    }

    [Fact]
    public void Nothing_fires_after_the_end_date()
    {
        var rule = Rule(1, new DateOnly(2026, 1, 1), end: new DateOnly(2026, 3, 15));
        Assert.Equal(new DateOnly(2026, 3, 1), RecurringService.NextOccurrence(rule, new DateOnly(2026, 2, 2)));
        Assert.Null(RecurringService.NextOccurrence(rule, new DateOnly(2026, 3, 2)));
    }

    [Fact]
    public void A_paused_rule_never_fires()
    {
        var rule = Rule(1, new DateOnly(2026, 1, 1), paused: true);
        Assert.Null(RecurringService.NextOccurrence(rule, rule.StartDate));
    }
}
