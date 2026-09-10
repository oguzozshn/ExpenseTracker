using Microsoft.EntityFrameworkCore;
using ExpenseTracker.Data;

namespace ExpenseTracker.Services;

public record AccountBalance(Account Account, long Balance, int Count, DateOnly? LastMovement)
{
    /// <summary>A credit card sits at zero when nothing is owed, so its debt is the negative side.</summary>
    public long CardDebt => Account.Type == AccountType.CreditCard && Balance < 0 ? -Balance : 0;
}

public class AccountService(IDbContextFactory<AppDbContext> dbf)
{
    public async Task<List<Account>> ListAsync(bool includeArchived = false)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var q = db.Accounts.AsNoTracking();
        if (!includeArchived) q = q.Where(a => !a.Archived);
        return await q.OrderBy(a => a.SortOrder).ThenBy(a => a.Name).ToListAsync();
    }

    public async Task<Account?> GetAsync(int id)
    {
        await using var db = await dbf.CreateDbContextAsync();
        return await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id);
    }

    /// <summary>Returns the saved row, or null when the name is already taken.</summary>
    public async Task<Account?> SaveAsync(Account input)
    {
        await using var db = await dbf.CreateDbContextAsync();

        var name = input.Name.Trim();
        if (name.Length == 0) return null;

        if (await db.Accounts.AnyAsync(a => a.Id != input.Id && a.Name == name)) return null;

        Account row;
        if (input.Id == 0)
        {
            var maxOrder = await db.Accounts.Select(a => (int?)a.SortOrder).MaxAsync() ?? 0;
            row = new Account { SortOrder = maxOrder + 1 };
            db.Accounts.Add(row);
        }
        else
        {
            row = await db.Accounts.FirstOrDefaultAsync(a => a.Id == input.Id)
                  ?? throw new InvalidOperationException($"Hesap {input.Id} bulunamadı.");
        }

        row.Name = name;
        row.Type = input.Type;
        row.Color = input.Color;
        row.Icon = input.Icon.Trim();
        row.OpeningBalanceMinor = input.OpeningBalanceMinor;
        row.Reference = input.Reference.Trim();
        row.Archived = input.Archived;

        await db.SaveChangesAsync();
        return row;
    }

    public async Task SetArchivedAsync(int id, bool archived)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var row = await db.Accounts.FirstOrDefaultAsync(a => a.Id == id);
        if (row is null) return;
        row.Archived = archived;
        await db.SaveChangesAsync();
    }

    public async Task<int> UsageAsync(int id)
    {
        await using var db = await dbf.CreateDbContextAsync();
        return await db.Transactions.CountAsync(t => t.AccountId == id);
    }

    public async Task<bool> DeleteAsync(int id)
    {
        await using var db = await dbf.CreateDbContextAsync();
        if (await db.Transactions.AnyAsync(t => t.AccountId == id)) return false;
        if (await db.ImportBatches.AnyAsync(i => i.AccountId == id)) return false;

        var row = await db.Accounts.FirstOrDefaultAsync(a => a.Id == id);
        if (row is null) return false;

        db.Accounts.Remove(row);
        await db.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Opening balance plus every booking against the account. Transfers are included
    /// here even though the reports drop them — moving money really does change what
    /// is in the account, it just is not spending.
    /// </summary>
    public async Task<List<AccountBalance>> BalancesAsync(bool includeArchived = false)
    {
        await using var db = await dbf.CreateDbContextAsync();

        var accounts = await db.Accounts.AsNoTracking()
            .Where(a => includeArchived || !a.Archived)
            .OrderBy(a => a.SortOrder).ThenBy(a => a.Name)
            .ToListAsync();

        if (accounts.Count == 0) return [];

        var movements = await db.Transactions.AsNoTracking()
            .Where(t => t.AccountId != null)
            .GroupBy(t => t.AccountId!.Value)
            .Select(g => new
            {
                AccountId = g.Key,
                In = g.Where(t => t.Kind == EntryKind.Income).Sum(t => (long?)t.AmountMinor) ?? 0,
                Out = g.Where(t => t.Kind == EntryKind.Expense).Sum(t => (long?)t.AmountMinor) ?? 0,
                Count = g.Count(),
                Last = g.Max(t => (DateOnly?)t.OccurredOn)
            })
            .ToDictionaryAsync(x => x.AccountId);

        return accounts.Select(a =>
        {
            movements.TryGetValue(a.Id, out var m);
            var balance = a.OpeningBalanceMinor + (m?.In ?? 0) - (m?.Out ?? 0);
            return new AccountBalance(a, balance, m?.Count ?? 0, m?.Last);
        }).ToList();
    }

    public async Task MoveAsync(int id, int direction)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var rows = await db.Accounts.OrderBy(a => a.SortOrder).ThenBy(a => a.Name).ToListAsync();

        var index = rows.FindIndex(a => a.Id == id);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= rows.Count) return;

        (rows[index], rows[target]) = (rows[target], rows[index]);
        for (var i = 0; i < rows.Count; i++) rows[i].SortOrder = i + 1;

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// A single cash account on first run, so the entry form has something selected
    /// and the account column is never a required decision before the first import.
    /// </summary>
    public static async Task SeedDefaultsAsync(AppDbContext db)
    {
        if (await db.Accounts.AnyAsync()) return;

        db.Accounts.Add(new Account
        {
            Name = "Nakit",
            Type = AccountType.Cash,
            Icon = "💵",
            Color = "#3fb950",
            SortOrder = 1
        });

        await db.SaveChangesAsync();
    }

    public static string TypeLabel(AccountType type) => type switch
    {
        AccountType.Cash => "Nakit",
        AccountType.Bank => "Banka",
        _ => "Kredi kartı"
    };
}
