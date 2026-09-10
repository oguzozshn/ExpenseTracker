using ExpenseTracker.Data;

namespace ExpenseTracker.Services;

/// <summary>
/// What the entry form binds to. The amount stays a string all the way to
/// validation so that a half-typed "12," is a message under the field rather
/// than a binding error that swallows the keystroke.
/// </summary>
public class TransactionEntry
{
    public long Id { get; set; }
    public int CategoryId { get; set; }
    public EntryKind Kind { get; set; }
    public string Amount { get; set; } = "";
    public DateOnly Date { get; set; } = DateOnly.FromDateTime(DateTime.Now);
    public string Payee { get; set; } = "";
    public string Note { get; set; } = "";
    public PaymentMethod Method { get; set; }
    public int? AccountId { get; set; }
    public bool IsTransfer { get; set; }

    public bool IsNew => Id == 0;

    public static TransactionEntry New(EntryKind kind, DateOnly date, int? accountId = null) => new()
    {
        Kind = kind,
        Date = date,
        AccountId = accountId
    };

    public static TransactionEntry From(Transaction t) => new()
    {
        Id = t.Id,
        CategoryId = t.CategoryId,
        Kind = t.Kind,
        Amount = Money.ToInput(t.AmountMinor),
        Date = t.OccurredOn,
        Payee = t.Payee,
        Note = t.Note,
        Method = t.Method,
        AccountId = t.AccountId,
        IsTransfer = t.IsTransfer
    };

    /// <summary>Returns the error to show, or null and the parsed row through <paramref name="result"/>.</summary>
    public string? Validate(out Transaction result)
    {
        result = new Transaction();

        if (CategoryId == 0) return "Kategori seçilmeli.";
        if (!Money.TryParse(Amount, out var minor)) return "Tutar sayı olmalı.";
        if (minor <= 0) return "Tutar sıfırdan büyük olmalı.";

        result = new Transaction
        {
            Id = Id,
            CategoryId = CategoryId,
            Kind = Kind,
            AmountMinor = minor,
            OccurredOn = Date,
            Payee = Payee,
            Note = Note,
            Method = Method,
            AccountId = AccountId,
            IsTransfer = IsTransfer
        };
        return null;
    }
}
