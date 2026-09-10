using System.Globalization;

namespace ExpenseTracker.Services.Import;

/// <summary>Date and amount parsing for the shapes Turkish bank exports actually use.</summary>
public static class StatementValues
{
    private static readonly string[] DateFormats =
    [
        "dd.MM.yyyy", "d.M.yyyy", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy",
        "yyyy-MM-dd", "yyyy/MM/dd", "dd.MM.yy", "d.M.yy", "dd/MM/yy",
        "d MMMM yyyy", "dd MMMM yyyy", "d MMM yyyy", "dd MMM yyyy"
    ];

    public static bool TryDate(string? text, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        // "03.08.2026 14:22:10" and "03.08.2026T00:00:00" both reduce to the date part.
        var value = text.Trim();
        var cut = value.IndexOfAny([' ', 'T', 't']);
        var head = cut > 0 ? value[..cut] : value;

        foreach (var candidate in new[] { head, value })
        {
            if (DateOnly.TryParseExact(candidate, DateFormats, Money.Culture,
                    DateTimeStyles.None, out date)) return true;

            if (DateOnly.TryParseExact(candidate, DateFormats, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out date)) return true;
        }

        if (DateTime.TryParse(value, Money.Culture, DateTimeStyles.None, out var parsed))
        {
            date = DateOnly.FromDateTime(parsed);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Signed amount in kuruş. Negative means money left the account. Handles the
    /// three ways a statement writes a negative number: a leading minus, a trailing
    /// minus ("1.234,56-") and accounting parentheses ("(1.234,56)").
    /// </summary>
    public static bool TryAmount(string? text, out long minor)
    {
        minor = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var value = text.Trim();

        var negative = false;

        if (value.StartsWith('(') && value.EndsWith(')'))
        {
            negative = true;
            value = value[1..^1];
        }

        // Currency markers are noise; strip them before looking at the signs.
        foreach (var marker in new[] { "TL", "TRY", "₺", "TRL" })
            value = value.Replace(marker, "", StringComparison.OrdinalIgnoreCase);

        value = value.Trim();

        if (value.EndsWith('-'))
        {
            negative = true;
            value = value[..^1].Trim();
        }
        else if (value.StartsWith('-'))
        {
            negative = true;
            value = value[1..].Trim();
        }
        else if (value.StartsWith('+'))
        {
            value = value[1..].Trim();
        }

        if (!Money.TryParse(value, out var magnitude)) return false;

        magnitude = Math.Abs(magnitude);
        minor = negative ? -magnitude : magnitude;
        return true;
    }
}
