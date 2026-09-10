using ExpenseTracker.Services;

namespace ExpenseTracker.Tests;

public class PeriodTests
{
    [Fact]
    public void A_start_day_of_one_is_the_calendar_month()
    {
        var p = Period.Containing(new DateOnly(2026, 8, 3), 1);
        Assert.Equal(new DateOnly(2026, 8, 1), p.Start);
        Assert.Equal(new DateOnly(2026, 8, 31), p.End);
        Assert.Equal(31, p.DayCount);
    }

    [Fact]
    public void A_date_before_the_start_day_belongs_to_the_previous_period()
    {
        var p = Period.Containing(new DateOnly(2026, 8, 3), 15);
        Assert.Equal(new DateOnly(2026, 7, 15), p.Start);
        Assert.Equal(new DateOnly(2026, 8, 14), p.End);
    }

    [Fact]
    public void A_date_on_the_start_day_opens_the_new_period()
    {
        var p = Period.Containing(new DateOnly(2026, 8, 15), 15);
        Assert.Equal(new DateOnly(2026, 8, 15), p.Start);
        Assert.Equal(new DateOnly(2026, 9, 14), p.End);
    }

    [Fact]
    public void February_survives_a_late_start_day()
    {
        var p = Period.Containing(new DateOnly(2026, 3, 1), 28);
        Assert.Equal(new DateOnly(2026, 2, 28), p.Start);
        Assert.Equal(new DateOnly(2026, 3, 27), p.End);
    }

    [Fact]
    public void Start_days_past_the_28th_are_capped()
    {
        Assert.Equal(28, Period.NormalizeStartDay(31));
        Assert.Equal(1, Period.NormalizeStartDay(0));
    }

    [Fact]
    public void Shifting_stays_on_the_same_start_day()
    {
        var p = Period.Containing(new DateOnly(2026, 1, 31), 15);
        Assert.Equal(new DateOnly(2026, 1, 15), p.Start);
        Assert.Equal(new DateOnly(2026, 2, 15), p.Next().Start);
        Assert.Equal(new DateOnly(2025, 12, 15), p.Previous().Start);
    }

    [Fact]
    public void Elapsed_days_are_clamped_to_the_period()
    {
        var p = Period.Containing(new DateOnly(2026, 8, 3), 1);
        Assert.Equal(0, p.ElapsedDays(new DateOnly(2026, 7, 20)));
        Assert.Equal(3, p.ElapsedDays(new DateOnly(2026, 8, 3)));
        Assert.Equal(31, p.ElapsedDays(new DateOnly(2026, 9, 20)));
    }
}
