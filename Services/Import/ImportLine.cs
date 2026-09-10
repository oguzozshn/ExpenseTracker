using ExpenseTracker.Data;

namespace ExpenseTracker.Services.Import;

/// <summary>One statement line, from raw text through to the decisions made in the preview.</summary>
public class ImportLine
{
    public int RowNumber { get; init; }

    public string RawDate { get; init; } = "";
    public string RawDescription { get; init; } = "";
    public string RawAmount { get; init; } = "";

    public DateOnly Date { get; set; }
    public string Description { get; set; } = "";

    /// <summary>Negative means money left the account.</summary>
    public long SignedMinor { get; set; }

    /// <summary>Set when the line could not be parsed; such lines are never written.</summary>
    public string? Error { get; set; }

    public string Hash { get; set; } = "";

    /// <summary>True when this exact line is already in the ledger from an earlier import.</summary>
    public bool AlreadyImported { get; set; }

    public int CategoryId { get; set; }
    public bool IsTransfer { get; set; }

    /// <summary>Unticked lines are left out of the batch.</summary>
    public bool Include { get; set; } = true;

    /// <summary>The rule that suggested the category, so its use can be counted.</summary>
    public int? RuleId { get; set; }

    public bool IsValid => Error is null;

    /// <summary>Passed every gate except the category, which the user may still owe us.</summary>
    public bool Writable => IsValid && Include && !AlreadyImported;

    /// <summary>Will actually be written. A line without a category never is.</summary>
    public bool Ready => Writable && CategoryId != 0;

    public EntryKind Kind => SignedMinor >= 0 ? EntryKind.Income : EntryKind.Expense;

    public long AmountMinor => Math.Abs(SignedMinor);
}

public class ImportPreview
{
    public List<ImportLine> Lines { get; init; } = [];

    public int Valid => Lines.Count(l => l.IsValid);
    public int Failed => Lines.Count(l => !l.IsValid);
    public int Duplicates => Lines.Count(l => l.IsValid && l.AlreadyImported);
    public int Ready => Lines.Count(l => l.Ready);
    public int Uncategorised => Lines.Count(l => l.Writable && l.CategoryId == 0);
    public int Excluded => Lines.Count(l => l.IsValid && !l.AlreadyImported && !l.Include);

    public long ExpenseTotal => Lines.Where(l => l.Ready && !l.IsTransfer && l.SignedMinor < 0)
        .Sum(l => l.AmountMinor);

    public long IncomeTotal => Lines.Where(l => l.Ready && !l.IsTransfer && l.SignedMinor > 0)
        .Sum(l => l.AmountMinor);

    public DateOnly? First => Lines.Where(l => l.IsValid).Select(l => (DateOnly?)l.Date).Min();
    public DateOnly? Last => Lines.Where(l => l.IsValid).Select(l => (DateOnly?)l.Date).Max();
}

public record ImportResult(int BatchId, int Imported, int Skipped);
