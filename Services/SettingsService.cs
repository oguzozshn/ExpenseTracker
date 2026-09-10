using Microsoft.EntityFrameworkCore;
using ExpenseTracker.Data;

namespace ExpenseTracker.Services;

/// <summary>User preferences. Nothing here is secret, so it is stored as plain key/value rows.</summary>
public class SettingsService(IDbContextFactory<AppDbContext> dbf)
{
    public const string CurrencyKey = "app.currency";
    public const string PeriodStartDayKey = "app.periodStartDay";

    // Every page reads these on first render, so the values are cached and
    // invalidated on write rather than hitting SQLite on each navigation.
    private UserSettings? _cache;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<UserSettings> GetAsync()
    {
        if (_cache is not null) return _cache;

        await _gate.WaitAsync();
        try
        {
            if (_cache is not null) return _cache;

            await using var db = await dbf.CreateDbContextAsync();
            var rows = await db.Settings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value);

            rows.TryGetValue(CurrencyKey, out var currency);
            rows.TryGetValue(PeriodStartDayKey, out var startDayRaw);

            return _cache = new UserSettings(
                Currency: string.IsNullOrWhiteSpace(currency) ? "₺" : currency,
                PeriodStartDay: Period.NormalizeStartDay(int.TryParse(startDayRaw, out var d) ? d : 1));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(UserSettings settings)
    {
        await using var db = await dbf.CreateDbContextAsync();
        await SetAsync(db, CurrencyKey, settings.Currency);
        await SetAsync(db, PeriodStartDayKey, Period.NormalizeStartDay(settings.PeriodStartDay).ToString());
        await db.SaveChangesAsync();

        _cache = settings with { PeriodStartDay = Period.NormalizeStartDay(settings.PeriodStartDay) };
    }

    private static async Task SetAsync(AppDbContext db, string key, string value)
    {
        var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == key);
        if (row is null)
            db.Settings.Add(new AppSetting { Key = key, Value = value });
        else
            row.Value = value;
    }

    /// <summary>Current period, i.e. the one containing today.</summary>
    public async Task<Period> CurrentPeriodAsync() =>
        Period.Containing(DateOnly.FromDateTime(DateTime.Now), (await GetAsync()).PeriodStartDay);
}

public record UserSettings(string Currency, int PeriodStartDay)
{
    public string Format(long minor) => Money.Format(minor, Currency);

    public string FormatSigned(long minor) => Money.FormatSigned(minor, Currency);
}
