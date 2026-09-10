using Microsoft.EntityFrameworkCore;
using ExpenseTracker.Data;
using ExpenseTracker.Services.Import;

namespace ExpenseTracker.Services;

/// <summary>
/// Text-to-category rules. They exist so the second import of the same bank does
/// not need every line categorising again by hand.
/// </summary>
public class CategoryRuleService(IDbContextFactory<AppDbContext> dbf)
{
    public async Task<List<CategoryRule>> ListAsync()
    {
        await using var db = await dbf.CreateDbContextAsync();
        return await db.CategoryRules.AsNoTracking().Include(r => r.Category)
            .OrderByDescending(r => r.TimesApplied).ThenBy(r => r.Contains)
            .ToListAsync();
    }

    /// <summary>Returns the saved rule, or null when the same text is already mapped.</summary>
    public async Task<CategoryRule?> SaveAsync(CategoryRule input)
    {
        await using var db = await dbf.CreateDbContextAsync();

        var text = StatementText.Normalize(input.Contains);
        if (text.Length < 2) return null;

        var existing = await db.CategoryRules.FirstOrDefaultAsync(r => r.Contains == text);
        if (existing is not null && existing.Id != input.Id)
        {
            // Re-pointing an existing pattern is what the user means when they map
            // the same merchant to a different category, so update instead of failing.
            existing.CategoryId = input.CategoryId;
            existing.MarksTransfer = input.MarksTransfer;
            await db.SaveChangesAsync();
            return existing;
        }

        CategoryRule row;
        if (input.Id == 0)
        {
            row = new CategoryRule();
            db.CategoryRules.Add(row);
        }
        else
        {
            row = await db.CategoryRules.FirstOrDefaultAsync(r => r.Id == input.Id)
                  ?? throw new InvalidOperationException($"Kural {input.Id} bulunamadı.");
        }

        row.Contains = text;
        row.CategoryId = input.CategoryId;
        row.MarksTransfer = input.MarksTransfer;

        await db.SaveChangesAsync();
        return row;
    }

    public async Task DeleteAsync(int id)
    {
        await using var db = await dbf.CreateDbContextAsync();
        await db.CategoryRules.Where(r => r.Id == id).ExecuteDeleteAsync();
    }

    /// <summary>Counts a rule as used, so the list surfaces the ones that actually earn their keep.</summary>
    public async Task RecordUseAsync(IEnumerable<int> ruleIds)
    {
        var ids = ruleIds.Distinct().ToList();
        if (ids.Count == 0) return;

        await using var db = await dbf.CreateDbContextAsync();
        await db.CategoryRules.Where(r => ids.Contains(r.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.TimesApplied, r => r.TimesApplied + 1));
    }

    /// <summary>
    /// Turkish merchants that appear on nearly every statement. Seeded once so the
    /// very first import already arrives mostly categorised; the user edits from there.
    /// </summary>
    public static async Task SeedDefaultsAsync(AppDbContext db)
    {
        if (await db.CategoryRules.AnyAsync()) return;

        var categories = await db.Categories.ToListAsync();
        int? IdOf(string name, EntryKind kind) =>
            categories.FirstOrDefault(c => c.Name == name && c.Kind == kind)?.Id;

        var expense = new (string Category, string[] Patterns)[]
        {
            ("Market", ["MIGROS", "BIM ", "A101", "SOK MARKET", "CARREFOUR", "MACROCENTER", "GETIR", "BIZIM TOPTAN", "TARIM KREDI"]),
            ("Ulaşım", ["SHELL", "OPET", "PETROL OFISI", "BP ", "TOTAL AKARYAKIT", "ISTANBULKART", "UBER", "BITAKSI", "MARTI", "OTOYOL", "HGS", "OGS"]),
            ("Yeme-İçme", ["YEMEKSEPETI", "GETIR YEMEK", "TRENDYOL YEMEK", "STARBUCKS", "DOMINO", "BURGER KING", "MCDONALD", "KAHVE DUNYASI", "SIMIT SARAYI"]),
            ("Faturalar", ["TURKCELL", "VODAFONE", "TURK TELEKOM", "TURKNET", "SUPERONLINE", "BEDAS", "AYEDAS", "IGDAS", "ISKI", "ASKI", "ENERJISA", "AYDEM"]),
            ("Abonelikler", ["NETFLIX", "SPOTIFY", "YOUTUBEPREMIUM", "AMAZON PRIME", "APPLE.COM", "GOOGLE", "ADOBE", "MICROSOFT", "OPENAI", "EXXEN", "BLUTV", "DISNEY"]),
            ("Sağlık", ["ECZANE", "HASTANE", "MEDICAL PARK", "ACIBADEM", "LABORATUVAR", "DIS POLIKLINIK"]),
            ("Giyim", ["LC WAIKIKI", "DEFACTO", "ZARA", "KOTON", "BOYNER", "MAVI", "H&M", "BERSHKA", "PULL BEAR"]),
            ("Eğlence", ["CINEMAXIMUM", "BILETIX", "PASSO", "STEAMGAMES", "STEAM PURCHASE", "PLAYSTATION", "SPOR SALONU", "MACFIT"])
        };

        foreach (var (category, patterns) in expense)
        {
            if (IdOf(category, EntryKind.Expense) is not { } categoryId) continue;
            foreach (var pattern in patterns)
                db.CategoryRules.Add(new CategoryRule
                {
                    Contains = StatementText.Normalize(pattern),
                    CategoryId = categoryId
                });
        }

        if (IdOf("Maaş", EntryKind.Income) is { } salaryId)
        {
            foreach (var pattern in new[] { "MAAS ODEMESI", "UCRET ODEMESI", "MAAS" })
                db.CategoryRules.Add(new CategoryRule
                {
                    Contains = StatementText.Normalize(pattern),
                    CategoryId = salaryId
                });
        }

        // Money moving between the user's own accounts. Marked as transfers so that
        // importing the bank statement and the card statement does not count the
        // same spending twice — once as the card payment, once as the purchases.
        // The importer swaps in the income twin when the line goes the other way.
        if (IdOf(CategoryService.TransferCategoryName, EntryKind.Expense) is { } otherId)
        {
            foreach (var pattern in new[]
                     {
                         "KREDI KARTI ODEMESI", "KREDI KARTI BORC ODEME", "KART BORCU",
                         "ATM PARA CEKME", "NAKIT CEKIM", "HESAPLAR ARASI", "VIRMAN"
                     })
                db.CategoryRules.Add(new CategoryRule
                {
                    Contains = StatementText.Normalize(pattern),
                    CategoryId = otherId,
                    MarksTransfer = true
                });
        }

        await db.SaveChangesAsync();
    }
}
