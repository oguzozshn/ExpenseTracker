using System.Text;

namespace ExpenseTracker.Services.Import;

/// <summary>
/// Statement descriptions arrive in every shape a bank feels like: "MİGROS",
/// "MIGROS TICARET AS", "migros  kadikoy". Both rule matching and the duplicate
/// fingerprint compare the normalised form so those are one and the same merchant.
/// </summary>
public static class StatementText
{
    /// <summary>Upper case, Turkish letters folded to ASCII, runs of whitespace collapsed.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        var sb = new StringBuilder(text.Length);
        var lastWasSpace = false;

        foreach (var ch in text.Trim())
        {
            var c = Fold(ch);

            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace) sb.Append(' ');
                lastWasSpace = true;
                continue;
            }

            lastWasSpace = false;
            sb.Append(char.ToUpperInvariant(c));
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    /// Folds the Turkish letters by hand. char.ToUpperInvariant would turn 'ı' into
    /// 'ı' and leave 'İ' as 'İ', so "MİGROS" and "MIGROS" would stay different strings.
    /// </summary>
    private static char Fold(char c) => c switch
    {
        'ı' or 'İ' or 'i' or 'I' => 'I',
        'ş' or 'Ş' => 'S',
        'ğ' or 'Ğ' => 'G',
        'ü' or 'Ü' => 'U',
        'ö' or 'Ö' => 'O',
        'ç' or 'Ç' => 'C',
        'â' or 'Â' => 'A',
        'î' or 'Î' => 'I',
        'û' or 'Û' => 'U',
        _ => c
    };

    public static bool Contains(string? haystack, string? needle)
    {
        var n = Normalize(needle);
        return n.Length != 0 && Normalize(haystack).Contains(n, StringComparison.Ordinal);
    }
}
