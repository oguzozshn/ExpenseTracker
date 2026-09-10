using ExpenseTracker.Data;
using ExpenseTracker.Services;

namespace ExpenseTracker.Tests;

/// <summary>
/// The two estimators are the whole forecast. If they stop bracketing the truth
/// the dashboard prints a confident number that is quietly wrong, which is worse
/// than printing nothing.
/// </summary>
public class ForecastTests
{
    private const double Tolerance = 0.5;

    [Fact]
    public void A_flat_series_makes_both_estimators_agree()
    {
        double[] flat = [1000, 1000, 1000, 1000];

        Assert.Equal(1000, ForecastService.WeightedAverage(flat), Tolerance);
        Assert.Equal(1000, ForecastService.LinearProjection(flat), Tolerance);
    }

    [Fact]
    public void The_weighted_average_leans_on_the_newest_period()
    {
        double[] rising = [100, 200, 300];

        // A plain mean would say 200; recency weighting has to land above it and
        // still below the newest value, or it is not an average at all.
        var weighted = ForecastService.WeightedAverage(rising);
        Assert.True(weighted > 200, $"beklenen > 200, gelen {weighted}");
        Assert.True(weighted < 300, $"beklenen < 300, gelen {weighted}");
    }

    [Fact]
    public void The_line_continues_a_steady_trend_one_period_past_the_end()
    {
        double[] rising = [100, 200, 300, 400];
        Assert.Equal(500, ForecastService.LinearProjection(rising), Tolerance);
    }

    [Fact]
    public void On_a_rising_series_the_line_sits_above_the_weighted_average()
    {
        // This ordering is what makes Low/High a real interval rather than two
        // numbers that occasionally swap places.
        double[] rising = [16593, 21743, 25600, 29001, 30792];

        Assert.True(
            ForecastService.LinearProjection(rising) > ForecastService.WeightedAverage(rising),
            "yükselen seride doğrusal projeksiyon ağırlıklı ortalamanın üstünde olmalı");
    }

    [Fact]
    public void A_falling_series_never_forecasts_a_negative_debt()
    {
        // Steep enough and the fitted line goes under zero; a card cannot owe the
        // user money, so the projection has to floor instead.
        double[] collapsing = [10000, 6000, 2000, 100];
        Assert.True(ForecastService.LinearProjection(collapsing) >= 0);
    }

    [Fact]
    public void A_single_period_is_carried_through_unchanged()
    {
        double[] one = [4200];

        Assert.Equal(4200, ForecastService.WeightedAverage(one), Tolerance);
        Assert.Equal(4200, ForecastService.LinearProjection(one), Tolerance);
    }

    [Fact]
    public void An_empty_series_is_zero_rather_than_a_divide_by_zero()
    {
        Assert.Equal(0, ForecastService.WeightedAverage([]), Tolerance);
        Assert.Equal(0, ForecastService.LinearProjection([]), Tolerance);
    }

    [Fact]
    public void A_monthly_rule_is_counted_once_in_a_calendar_month()
    {
        var rule = new RecurringRule { DayOfMonth = 15, StartDate = new DateOnly(2026, 1, 1) };
        var august = new Period(new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31));

        Assert.Equal(1, ForecastService.OccurrencesIn(rule, august));
    }

    [Fact]
    public void A_rule_whose_day_falls_outside_a_shifted_period_is_not_counted_twice()
    {
        // A period running the 15th to the 14th contains exactly one 20th.
        var rule = new RecurringRule { DayOfMonth = 20, StartDate = new DateOnly(2026, 1, 1) };
        var shifted = new Period(new DateOnly(2026, 8, 15), new DateOnly(2026, 9, 14));

        Assert.Equal(1, ForecastService.OccurrencesIn(rule, shifted));
    }

    [Fact]
    public void A_rule_that_has_ended_contributes_nothing()
    {
        var rule = new RecurringRule
        {
            DayOfMonth = 10,
            StartDate = new DateOnly(2026, 1, 1),
            EndDate = new DateOnly(2026, 6, 30)
        };
        var august = new Period(new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31));

        Assert.Equal(0, ForecastService.OccurrencesIn(rule, august));
    }

    [Fact]
    public void A_paused_rule_contributes_nothing()
    {
        var rule = new RecurringRule { DayOfMonth = 10, StartDate = new DateOnly(2026, 1, 1), Paused = true };
        var august = new Period(new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31));

        Assert.Equal(0, ForecastService.OccurrencesIn(rule, august));
    }
}
