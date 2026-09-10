namespace ExpenseTracker.Services.Import;

/// <summary>
/// Which column holds what. Guessed from the header text and then shown to the user,
/// because every bank names these differently and a wrong guess must be visible
/// before anything is written.
/// </summary>
public class ColumnMap
{
    public int Date { get; set; } = -1;
    public int Description { get; set; } = -1;

    /// <summary>One signed column. Mutually exclusive with <see cref="Debit"/>/<see cref="Credit"/>.</summary>
    public int Amount { get; set; } = -1;

    /// <summary>Money out.</summary>
    public int Debit { get; set; } = -1;

    /// <summary>Money in.</summary>
    public int Credit { get; set; } = -1;

    /// <summary>
    /// Credit card exports usually write purchases as positive numbers, the opposite
    /// of a bank account. Flipped by the user while watching the preview.
    /// </summary>
    public bool InvertSign { get; set; }

    public bool UsesDebitCredit => Debit >= 0 || Credit >= 0;

    public bool IsComplete => Date >= 0 && (Amount >= 0 || UsesDebitCredit);

    private static readonly string[] BalanceWords = ["BAKIYE", "BALANCE", "KALAN", "LIMIT"];

    public static ColumnMap Guess(IReadOnlyList<string> columns)
    {
        var map = new ColumnMap();
        var normalized = columns.Select(StatementText.Normalize).ToList();

        map.Date = FindBest(normalized, ["ISLEM TARIHI", "TARIH", "DATE"], ["VALOR", "VADE", "SON ODEME"]);
        map.Description = FindBest(normalized,
            ["ACIKLAMA", "ISLEM ACIKLAMASI", "DESCRIPTION", "ISYERI", "ODEME YERI", "MERCHANT", "DETAY", "ISLEM"],
            []);

        map.Debit = FindBest(normalized, ["BORC", "DEBIT", "CIKAN", "HARCAMA TUTARI"], BalanceWords);
        map.Credit = FindBest(normalized, ["ALACAK", "CREDIT", "GIREN", "ODEME TUTARI"], BalanceWords);

        // A single signed column is only used when the bank did not split the two.
        if (!map.UsesDebitCredit)
            map.Amount = FindBest(normalized, ["ISLEM TUTARI", "TUTAR", "AMOUNT", "MIKTAR"], BalanceWords);

        return map;
    }

    /// <summary>
    /// Earlier keywords win, so "İşlem Tarihi" beats a bare "Tarih" when both exist.
    /// Columns carrying an excluded word are never picked — a balance column full of
    /// plausible numbers is the easiest thing to mistake for the amount.
    /// </summary>
    private static int FindBest(List<string> columns, string[] keywords, string[] excluded)
    {
        foreach (var keyword in keywords)
        {
            for (var i = 0; i < columns.Count; i++)
            {
                if (excluded.Any(x => columns[i].Contains(x))) continue;
                if (columns[i].Contains(keyword)) return i;
            }
        }
        return -1;
    }
}
