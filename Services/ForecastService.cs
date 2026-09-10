using Microsoft.EntityFrameworkCore;
using ExpenseTracker.Data;

namespace ExpenseTracker.Services;

/// <summary>
/// What a card is likely to owe for a period that has not started yet.
///
/// Deliberately a range rather than a single figure. A forecast built on a
/// handful of months of a rising trend genuinely is uncertain, and the two
/// estimators disagree in a useful way: the recency-weighted average lags a
/// trend, the least-squares line overshoots a decelerating one. The truth has
/// been sitting between them, so showing both is more honest than picking one
/// and printing it to the kuruş.
/// </summary>
public record CardForecast(
    Account Account,
    Period Period,
    long CurrentDebt,
    long Recurring,
    long VariableLow,
    long VariableHigh,
    IReadOnlyList<(Period Period, long Expense)> History)
{
    public long Low => Recurring + VariableLow;
    public long High => Recurring + VariableHigh;

    /// <summary>Headline figure. The midpoint is a presentation choice, not a claim of precision.</summary>
    public long Mid => (Low + High) / 2;

    public int BasisPeriods => History.Count;
}

public class ForecastService(IDbContextFactory<AppDbContext> dbf, AccountService accounts)
{
    /// <summary>Complete periods of history read at most. Six keeps a seasonal swing visible without letting last winter drive next month.</summary>
    private const int LookBack = 6;

    /// <summary>With one period there is no trend to measure and the two estimators collapse onto the same number, which would look like confidence the data has not earned.</summary>
    private const int MinBasisPeriods = 2;

    /// <summary>
    /// A forecast per credit card that has enough history. Cards are the only
    /// account type here: a bank balance is not a debt that accrues over a period,
    /// so projecting one would be answering a question nobody asked.
    /// </summary>
    public async Task<List<CardForecast>> CardsAsync(DateOnly today, int periodStartDay)
    {
        var current = Period.Containing(today, periodStartDay);
        var target = current.Next();

        var balances = await accounts.BalancesAsync();
        var cards = balances.Where(b => b.Account.Type == AccountType.CreditCard).ToList();
        if (cards.Count == 0) return [];

        // The current period is excluded on purpose: it is only part-elapsed, and
        // averaging a half-finished month in drags every estimate down.
        var periods = new List<Period>();
        for (var i = LookBack; i >= 1; i--) periods.Add(current.Shift(-i));

        var window = new Period(periods[0].Start, periods[^1].End);
        var cardIds = cards.Select(c => c.Account.Id).ToList();

        await using var db = await dbf.CreateDbContextAsync();

        // Recurring rows are excluded here and added back from the rules below.
        // Counting them in the historical average as well would bill the user
        // twice for every subscription they bother to declare.
        var rows = await db.Transactions.AsNoTracking()
            .Where(t => t.AccountId != null
                        && cardIds.Contains(t.AccountId.Value)
                        && t.Kind == EntryKind.Expense
                        && !t.IsTransfer
                        && t.RecurringRuleId == null
                        && t.OccurredOn >= window.Start
                        && t.OccurredOn <= window.End)
            .Select(t => new { AccountId = t.AccountId!.Value, t.OccurredOn, t.AmountMinor })
            .ToListAsync();

        var rules = await db.RecurringRules.AsNoTracking()
            .Where(r => !r.Paused
                        && r.AccountId != null
                        && cardIds.Contains(r.AccountId.Value)
                        && r.Kind == EntryKind.Expense)
            .ToListAsync();

        var result = new List<CardForecast>();

        foreach (var card in cards)
        {
            var mine = rows.Where(r => r.AccountId == card.Account.Id).ToList();

            // A period with no rows is not evidence of a month without spending —
            // it is a month before this card existed in the ledger, or one whose
            // statement was never imported. Averaging those zeros in would halve
            // the forecast, so they are dropped rather than counted.
            var history = periods
                .Select(p => (Period: p, Expense: mine
                    .Where(r => r.OccurredOn >= p.Start && r.OccurredOn <= p.End)
                    .Sum(r => r.AmountMinor)))
                .Where(x => x.Expense > 0)
                .ToList();

            if (history.Count < MinBasisPeriods) continue;

            var series = history.Select(h => (double)h.Expense).ToList();
            var weighted = WeightedAverage(series);
            var linear = LinearProjection(series);

            var recurring = rules
                .Where(r => r.AccountId == card.Account.Id)
                .Sum(r => OccurrencesIn(r, target) * r.AmountMinor);

            result.Add(new CardForecast(
                Account: card.Account,
                Period: target,
                CurrentDebt: card.CardDebt,
                Recurring: recurring,
                VariableLow: (long)Math.Round(Math.Min(weighted, linear)),
                VariableHigh: (long)Math.Round(Math.Max(weighted, linear)),
                History: history));
        }

        return result;
    }

    /// <summary>
    /// Mean weighted 1..n by recency. Tracks a trend without extrapolating past
    /// the newest observation, so it is the conservative end of the range.
    /// </summary>
    internal static double WeightedAverage(IReadOnlyList<double> values)
    {
        double sum = 0, weights = 0;
        for (var i = 0; i < values.Count; i++)
        {
            var w = i + 1;
            sum += values[i] * w;
            weights += w;
        }
        return weights == 0 ? 0 : sum / weights;
    }

    /// <summary>
    /// Least-squares line through the series, read one step past the end. Follows
    /// a trend rather than lagging it, and overshoots when the trend is flattening
    /// — which is exactly why it is paired with the weighted average instead of
    /// being used alone. Never returns less than zero: a steep enough downward
    /// slope would otherwise forecast a card that pays the user.
    /// </summary>
    internal static double LinearProjection(IReadOnlyList<double> values)
    {
        var n = values.Count;
        if (n == 0) return 0;
        if (n == 1) return values[0];

        var meanX = (n + 1) / 2.0;
        var meanY = values.Average();

        double sxy = 0, sxx = 0;
        for (var i = 0; i < n; i++)
        {
            var dx = i + 1 - meanX;
            sxy += dx * (values[i] - meanY);
            sxx += dx * dx;
        }

        if (sxx == 0) return meanY;

        var slope = sxy / sxx;
        return Math.Max(0, meanY + slope * (n + 1 - meanX));
    }

    /// <summary>
    /// How many times a rule fires inside a period. Normally one — a monthly rule
    /// in a month-long period — but a period that straddles a month boundary can
    /// catch a day-of-month twice, and dropping the second would under-bill it.
    /// </summary>
    internal static int OccurrencesIn(RecurringRule rule, Period period)
    {
        var count = 0;
        var date = RecurringService.NextOccurrence(rule, period.Start);
        while (date is { } d && d <= period.End)
        {
            count++;
            date = RecurringService.NextOccurrence(rule, d.AddDays(1));
        }
        return count;
    }
}
