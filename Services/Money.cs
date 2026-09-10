using System.Globalization;

namespace ExpenseTracker.Services;

/// <summary>
/// Conversion between the integer minor units stored in the database and the
/// text the user types and reads. Everything is formatted with tr-TR regardless
/// of the request culture, so a total never changes shape between two browsers.
/// </summary>
public static class Money
{
    public const long Scale = 100;

    public static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("tr-TR");

    public static decimal ToDecimal(long minor) => minor / (decimal)Scale;

    public static long FromDecimal(decimal value) =>
        (long)Math.Round(value * Scale, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Accepts the shapes people actually type: "1234", "1234,56", "1234.56",
    /// "1.234,56" and "1,234.56". When both separators appear the rightmost one
    /// is the decimal point.
    /// <para>
    /// A lone dot is the ambiguous case. It is read as a thousands separator when
    /// it occurs more than once ("1.234.567") or when exactly three digits follow
    /// it ("1.500"), and as a decimal point otherwise ("12.5", "12.50"). Kuruş is
    /// two digits, so a three-digit group is the Turkish thousands separator —
    /// reading it as decimals would turn 1.500 ₺ into 1,50 ₺, a silent 1000× error.
    /// </para>
    /// </summary>
    public static bool TryParse(string? text, out long minor)
    {
        minor = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var s = new string(text.Where(c => char.IsDigit(c) || c is '.' or ',' or '-' or '+').ToArray());
        if (s.Length == 0) return false;

        var lastDot = s.LastIndexOf('.');
        var lastComma = s.LastIndexOf(',');

        int decimalAt;
        if (lastDot >= 0 && lastComma >= 0)
            decimalAt = Math.Max(lastDot, lastComma);
        else if (lastComma >= 0)
            decimalAt = lastComma;
        else if (lastDot > 0 && s.IndexOf('.') == lastDot && !IsThousandsGroup(s, lastDot))
            decimalAt = lastDot;
        else
            decimalAt = -1;

        string whole, fraction;
        if (decimalAt >= 0)
        {
            whole = s[..decimalAt];
            fraction = s[(decimalAt + 1)..];
        }
        else
        {
            whole = s;
            fraction = "";
        }

        var negative = whole.StartsWith('-');
        whole = new string(whole.Where(char.IsDigit).ToArray());
        fraction = new string(fraction.Where(char.IsDigit).ToArray());

        if (whole.Length == 0 && fraction.Length == 0) return false;
        if (whole.Length > 15) return false;

        // Round rather than truncate when someone pastes three or more decimals.
        var fractionMinor = 0L;
        if (fraction.Length > 0)
        {
            var padded = fraction.PadRight(3, '0')[..3];
            if (!long.TryParse(padded, out var thousandths)) return false;
            fractionMinor = (thousandths + 5) / 10;
        }

        var wholeValue = 0L;
        if (whole.Length > 0 && !long.TryParse(whole, out wholeValue)) return false;

        minor = wholeValue * Scale + fractionMinor;
        if (negative) minor = -minor;
        return true;
    }

    /// <summary>True when the separator at <paramref name="at"/> is followed by exactly three digits.</summary>
    private static bool IsThousandsGroup(string s, int at)
    {
        var tail = s[(at + 1)..];
        return tail.Length == 3 && tail.All(char.IsDigit);
    }

    /// <summary>"1.234,56 ₺"</summary>
    public static string Format(long minor, string symbol) =>
        $"{ToDecimal(minor).ToString("N2", Culture)} {symbol}".TrimEnd();

    /// <summary>Same as <see cref="Format"/> but with an explicit + or − in front.</summary>
    public static string FormatSigned(long minor, string symbol)
    {
        var sign = minor < 0 ? "−" : "+";
        return $"{sign}{Format(Math.Abs(minor), symbol)}";
    }

    /// <summary>The form the number takes inside an editable text box.</summary>
    public static string ToInput(long minor) => ToDecimal(minor).ToString("0.00", Culture);
}
