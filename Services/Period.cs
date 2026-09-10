namespace ExpenseTracker.Services;

/// <summary>
/// A budgeting period: normally a calendar month, but it can start on any day
/// up to the 28th so that someone paid on the 15th sees a period that matches
/// their salary cycle. Every report and budget in the app goes through this type
/// instead of computing month boundaries by hand.
/// </summary>
public readonly record struct Period(DateOnly Start, DateOnly End)
{
    /// <summary>Days after the 28th do not exist in February, so the start day is capped there.</summary>
    public static int NormalizeStartDay(int day) => Math.Clamp(day, 1, 28);

    public static Period Containing(DateOnly date, int startDay)
    {
        startDay = NormalizeStartDay(startDay);
        var start = new DateOnly(date.Year, date.Month, startDay);
        if (date.Day < startDay) start = start.AddMonths(-1);
        return new Period(start, start.AddMonths(1).AddDays(-1));
    }

    public Period Shift(int months)
    {
        var start = Start.AddMonths(months);
        return new Period(start, start.AddMonths(1).AddDays(-1));
    }

    public Period Next() => Shift(1);

    public Period Previous() => Shift(-1);

    public bool Contains(DateOnly date) => date >= Start && date <= End;

    public int DayCount => End.DayNumber - Start.DayNumber + 1;

    /// <summary>Days already elapsed, clamped to the period. Used for pace-vs-budget.</summary>
    public int ElapsedDays(DateOnly today) =>
        today < Start ? 0 : today > End ? DayCount : today.DayNumber - Start.DayNumber + 1;

    /// <summary>"Ağustos 2026" for a calendar month, otherwise the explicit range.</summary>
    public string Label => Start.Day == 1
        ? Start.ToString("MMMM yyyy", Money.Culture)
        : $"{Start.ToString("d MMM", Money.Culture)} – {End.ToString("d MMM yyyy", Money.Culture)}";

    public string ShortLabel => Start.ToString("MMM yy", Money.Culture);
}
