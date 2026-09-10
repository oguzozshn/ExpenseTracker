namespace ExpenseTracker.Data;

/// <summary>Simple key/value bag for user preferences.</summary>
public class AppSetting
{
    public int Id { get; set; }
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

public enum EntryKind
{
    Expense = 0,
    Income = 1
}

public enum PaymentMethod
{
    Cash = 0,
    Card = 1,
    Transfer = 2,
    Other = 3
}

public class Category
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public EntryKind Kind { get; set; }

    /// <summary>Hex colour used by the charts and the category pill.</summary>
    public string Color { get; set; } = "#5b8def";

    /// <summary>A single emoji. Purely decorative, may be empty.</summary>
    public string Icon { get; set; } = "";

    /// <summary>Hidden from the pickers but its history stays intact.</summary>
    public bool Archived { get; set; }

    public int SortOrder { get; set; }
}

/// <summary>
/// One booked amount. Money is stored in minor units (kuruş) as an integer:
/// SQLite has no decimal type and EF maps <c>decimal</c> to TEXT, which makes
/// SUM and ORDER BY misbehave. Integers keep every total exact and server-side.
/// </summary>
public class Transaction
{
    public long Id { get; set; }

    public int CategoryId { get; set; }
    public Category? Category { get; set; }

    /// <summary>
    /// Copied from the category when the row is written. Kept on the row so that
    /// re-typing a category later cannot silently rewrite past months.
    /// </summary>
    public EntryKind Kind { get; set; }

    /// <summary>Always positive; <see cref="Kind"/> carries the direction.</summary>
    public long AmountMinor { get; set; }

    public DateOnly OccurredOn { get; set; }

    public string Note { get; set; } = "";

    /// <summary>Shop, person or employer. Free text, used by the search box.</summary>
    public string Payee { get; set; } = "";

    public PaymentMethod Method { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Set when this row was materialised from a <see cref="RecurringRule"/>.</summary>
    public int? RecurringRuleId { get; set; }

    /// <summary>
    /// Which account the money moved through. Nullable because rows entered before
    /// accounts existed have no answer, and guessing one would invent a balance.
    /// </summary>
    public int? AccountId { get; set; }
    public Account? Account { get; set; }

    /// <summary>
    /// Money moved between the user's own accounts — a credit card payment, a
    /// withdrawal. Kept out of every income/expense total: importing both the bank
    /// statement and the card statement would otherwise count the same spending twice.
    /// </summary>
    public bool IsTransfer { get; set; }

    /// <summary>
    /// Fingerprint of the statement line this row came from; null when entered by
    /// hand. Unique per account, so re-importing an overlapping statement is a no-op.
    /// </summary>
    public string? ImportHash { get; set; }

    public int? ImportBatchId { get; set; }
}

public enum AccountType
{
    Cash = 0,
    Bank = 1,
    CreditCard = 2
}

/// <summary>
/// Where the money sits. Statements are imported into one of these, and the balance
/// is the opening balance plus everything booked against it.
/// </summary>
public class Account
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public AccountType Type { get; set; }
    public string Color { get; set; } = "#5b8def";
    public string Icon { get; set; } = "";

    /// <summary>Balance before the first recorded transaction. Negative on a card with existing debt.</summary>
    public long OpeningBalanceMinor { get; set; }

    /// <summary>Last four digits of the IBAN or the card. Free text, only to tell two accounts apart.</summary>
    public string Reference { get; set; } = "";

    public bool Archived { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>
/// Maps a piece of statement text to a category, so the second import of the same
/// bank does not need the categories picking again.
/// </summary>
public class CategoryRule
{
    public int Id { get; set; }

    /// <summary>Case-insensitive substring searched in the statement description.</summary>
    public string Contains { get; set; } = "";

    public int CategoryId { get; set; }
    public Category? Category { get; set; }

    /// <summary>Matches are booked as transfers rather than spending.</summary>
    public bool MarksTransfer { get; set; }

    /// <summary>Longer patterns win; ties are broken by the more specific text.</summary>
    public int TimesApplied { get; set; }
}

/// <summary>One import run, kept so the whole batch can be undone in a single click.</summary>
public class ImportBatch
{
    public int Id { get; set; }

    public int AccountId { get; set; }
    public Account? Account { get; set; }

    public string FileName { get; set; } = "";
    public DateTime ImportedAtUtc { get; set; }

    public int Imported { get; set; }
    public int Skipped { get; set; }

    public DateOnly? FirstDate { get; set; }
    public DateOnly? LastDate { get; set; }
}

/// <summary>One spending limit per category, applied to every period.</summary>
public class Budget
{
    public int Id { get; set; }
    public int CategoryId { get; set; }
    public Category? Category { get; set; }
    public long MonthlyLimitMinor { get; set; }
}

/// <summary>
/// A fixed amount that repeats every period — rent, subscriptions, salary.
/// The scheduler turns due occurrences into real transactions; nothing here is
/// summed directly, so the reports have a single source of truth.
/// </summary>
public class RecurringRule
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    public int CategoryId { get; set; }
    public Category? Category { get; set; }

    public EntryKind Kind { get; set; }
    public long AmountMinor { get; set; }

    /// <summary>
    /// 1-31. Clamped to the real length of each month when an occurrence is
    /// computed, so 31 means "last day of the month" every time and 30 lands
    /// on 28 (or 29) in February rather than failing to exist.
    /// </summary>
    public int DayOfMonth { get; set; } = 1;

    public string Payee { get; set; } = "";
    public PaymentMethod Method { get; set; }

    /// <summary>
    /// Account the occurrences are booked against. Nullable because rules written
    /// before this existed never picked one, and guessing would move money through
    /// an account the user never named. A rule with no account still produces real
    /// transactions; they just sit outside every balance — and outside the card
    /// forecast, which is the reason to set it.
    /// </summary>
    public int? AccountId { get; set; }
    public Account? Account { get; set; }

    /// <summary>No occurrence is generated before this date.</summary>
    public DateOnly StartDate { get; set; }

    /// <summary>Inclusive. Null means open ended.</summary>
    public DateOnly? EndDate { get; set; }

    public bool Paused { get; set; }

    /// <summary>Date of the newest occurrence already written. Advisory only — the unique index is what actually prevents duplicates.</summary>
    public DateOnly? LastGeneratedOn { get; set; }
}
