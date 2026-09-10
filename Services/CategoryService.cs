using Microsoft.EntityFrameworkCore;
using ExpenseTracker.Data;

namespace ExpenseTracker.Services;

public class CategoryService(IDbContextFactory<AppDbContext> dbf)
{
    /// <summary>Exists in both kinds; used for money moving between the user's own accounts.</summary>
    public const string TransferCategoryName = "Hesaplar arası";


    public async Task<List<Category>> ListAsync(bool includeArchived = false)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var q = db.Categories.AsNoTracking();
        if (!includeArchived) q = q.Where(c => !c.Archived);
        return await q.OrderBy(c => c.Kind).ThenBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync();
    }

    public async Task<Category?> GetAsync(int id)
    {
        await using var db = await dbf.CreateDbContextAsync();
        return await db.Categories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
    }

    /// <summary>Returns the saved row, or null when the name is already taken for that kind.</summary>
    public async Task<Category?> SaveAsync(Category input)
    {
        await using var db = await dbf.CreateDbContextAsync();

        var name = input.Name.Trim();
        if (name.Length == 0) return null;

        var clash = await db.Categories
            .AnyAsync(c => c.Id != input.Id && c.Kind == input.Kind && c.Name == name);
        if (clash) return null;

        Category row;
        if (input.Id == 0)
        {
            var maxOrder = await db.Categories.Where(c => c.Kind == input.Kind)
                .Select(c => (int?)c.SortOrder).MaxAsync() ?? 0;
            row = new Category { SortOrder = maxOrder + 1 };
            db.Categories.Add(row);
        }
        else
        {
            row = await db.Categories.FirstOrDefaultAsync(c => c.Id == input.Id)
                  ?? throw new InvalidOperationException($"Kategori {input.Id} bulunamadı.");
        }

        row.Name = name;
        row.Kind = input.Kind;
        row.Color = input.Color;
        row.Icon = input.Icon.Trim();
        row.Archived = input.Archived;

        await db.SaveChangesAsync();
        return row;
    }

    public async Task SetArchivedAsync(int id, bool archived)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var row = await db.Categories.FirstOrDefaultAsync(c => c.Id == id);
        if (row is null) return;
        row.Archived = archived;
        await db.SaveChangesAsync();
    }

    /// <summary>How many rows would break if this category were deleted.</summary>
    public async Task<(int Transactions, int Rules)> UsageAsync(int id)
    {
        await using var db = await dbf.CreateDbContextAsync();
        return (
            await db.Transactions.CountAsync(t => t.CategoryId == id),
            await db.RecurringRules.CountAsync(r => r.CategoryId == id));
    }

    /// <summary>Only allowed while nothing references the category; the UI offers archiving otherwise.</summary>
    public async Task<bool> DeleteAsync(int id)
    {
        var (transactions, rules) = await UsageAsync(id);
        if (transactions > 0 || rules > 0) return false;

        await using var db = await dbf.CreateDbContextAsync();
        var row = await db.Categories.FirstOrDefaultAsync(c => c.Id == id);
        if (row is null) return false;

        var budget = await db.Budgets.FirstOrDefaultAsync(b => b.CategoryId == id);
        if (budget is not null) db.Budgets.Remove(budget);

        db.Categories.Remove(row);
        await db.SaveChangesAsync();
        return true;
    }

    public async Task MoveAsync(int id, int direction)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var row = await db.Categories.FirstOrDefaultAsync(c => c.Id == id);
        if (row is null) return;

        var siblings = await db.Categories.Where(c => c.Kind == row.Kind)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync();

        var index = siblings.FindIndex(c => c.Id == id);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= siblings.Count) return;

        (siblings[index], siblings[target]) = (siblings[target], siblings[index]);

        // Renumber the whole kind: the stored orders may have gaps or ties after
        // archiving and deleting, and a swap of two values would not survive them.
        for (var i = 0; i < siblings.Count; i++) siblings[i].SortOrder = i + 1;

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// First-run content. Without it the app opens on an empty form that cannot
    /// be submitted, since every transaction needs a category.
    /// </summary>
    public static async Task SeedDefaultsAsync(AppDbContext db)
    {
        if (await db.Categories.AnyAsync()) return;

        Category Make(int order, string icon, string name, EntryKind kind, string color) =>
            new() { Name = name, Kind = kind, Color = color, Icon = icon, SortOrder = order };

        db.Categories.AddRange(
            Make(1, "🛒", "Market", EntryKind.Expense, "#5b8def"),
            Make(2, "🏠", "Kira", EntryKind.Expense, "#a371f7"),
            Make(3, "💡", "Faturalar", EntryKind.Expense, "#d29922"),
            Make(4, "🚌", "Ulaşım", EntryKind.Expense, "#3fb950"),
            Make(5, "🍽️", "Yeme-İçme", EntryKind.Expense, "#f85149"),
            Make(6, "💊", "Sağlık", EntryKind.Expense, "#2dd4bf"),
            Make(7, "👕", "Giyim", EntryKind.Expense, "#ec4899"),
            Make(8, "🎬", "Eğlence", EntryKind.Expense, "#f97316"),
            Make(9, "🔁", "Abonelikler", EntryKind.Expense, "#8b5cf6"),
            Make(10, "📦", "Diğer", EntryKind.Expense, "#8b93a3"),
            Make(1, "💰", "Maaş", EntryKind.Income, "#3fb950"),
            Make(2, "➕", "Ek Gelir", EntryKind.Income, "#2dd4bf"),
            // One per direction: a transfer leaves one account and arrives in another,
            // and each side needs a category of its own kind.
            Make(11, "🔄", TransferCategoryName, EntryKind.Expense, "#8b93a3"),
            Make(3, "🔄", TransferCategoryName, EntryKind.Income, "#8b93a3"));

        await db.SaveChangesAsync();
    }
}
