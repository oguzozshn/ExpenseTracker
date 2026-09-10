using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ExpenseTracker.Data;

namespace ExpenseTracker.Services.Import;

public class StatementImporter(IDbContextFactory<AppDbContext> dbf, ILogger<StatementImporter> log)
{
    public static StatementTable Read(byte[] content, string fileName) =>
        StatementReader.Read(content, fileName);

    /// <summary>
    /// Parses every row, applies the category rules and marks the lines that are
    /// already in the ledger. Nothing is written here — the user sees the result
    /// first, which is the whole point of having a preview.
    /// </summary>
    public async Task<ImportPreview> PreviewAsync(StatementTable table, ColumnMap map, int accountId)
    {
        var preview = new ImportPreview();
        if (!map.IsComplete) return preview;

        await using var db = await dbf.CreateDbContextAsync();

        var rules = await db.CategoryRules.AsNoTracking().ToListAsync();
        // Longer patterns are more specific: "GETIR YEMEK" must win over "GETIR".
        rules = [.. rules.OrderByDescending(r => r.Contains.Length)];

        var categories = await db.Categories.AsNoTracking().ToListAsync();

        var existing = (await db.Transactions.AsNoTracking()
                .Where(t => t.AccountId == accountId && t.ImportHash != null)
                .Select(t => t.ImportHash!)
                .ToListAsync())
            .ToHashSet();

        // Identical lines legitimately repeat on one statement — two coffees, same
        // shop, same price, same day. The ordinal keeps their fingerprints apart
        // while staying stable when the same file is imported again.
        var seen = new Dictionary<string, int>();

        var rowNumber = 0;
        foreach (var row in table.Rows)
        {
            rowNumber++;

            var rawDate = table.Cell(row, map.Date);
            var rawDescription = map.Description >= 0 ? table.Cell(row, map.Description) : "";
            var rawAmount = map.UsesDebitCredit
                ? $"{table.Cell(row, map.Debit)} / {table.Cell(row, map.Credit)}"
                : table.Cell(row, map.Amount);

            var line = new ImportLine
            {
                RowNumber = rowNumber,
                RawDate = rawDate,
                RawDescription = rawDescription,
                RawAmount = rawAmount
            };

            if (!StatementValues.TryDate(rawDate, out var date))
            {
                line.Error = rawDate.Length == 0 ? "Tarih boş" : $"Tarih okunamadı: {rawDate}";
                preview.Lines.Add(line);
                continue;
            }

            if (!TryReadAmount(table, row, map, out var signed))
            {
                line.Error = $"Tutar okunamadı: {rawAmount}";
                preview.Lines.Add(line);
                continue;
            }

            if (signed == 0)
            {
                line.Error = "Tutar sıfır";
                preview.Lines.Add(line);
                continue;
            }

            line.Date = date;
            line.Description = rawDescription.Length > 0 ? rawDescription : "(açıklama yok)";
            line.SignedMinor = map.InvertSign ? -signed : signed;

            var baseKey = $"{date:yyyy-MM-dd}|{line.SignedMinor}|{StatementText.Normalize(line.Description)}";
            var ordinal = seen.TryGetValue(baseKey, out var n) ? n + 1 : 1;
            seen[baseKey] = ordinal;

            line.Hash = Fingerprint($"{baseKey}|{ordinal}");
            line.AlreadyImported = existing.Contains(line.Hash);

            var rule = rules.FirstOrDefault(r => StatementText.Contains(line.Description, r.Contains));
            if (rule is not null)
            {
                line.IsTransfer = rule.MarksTransfer;
                line.RuleId = rule.Id;
                line.CategoryId = ResolveCategory(categories, rule.CategoryId, line.Kind);
            }

            preview.Lines.Add(line);
        }

        return preview;
    }

    /// <summary>
    /// A rule that says "Diğer" cannot categorise a line that turned out to be income:
    /// the row would end up as an income transaction filed under a spending category,
    /// which quietly corrupts every report that groups by the two together. The twin
    /// with the same name in the right kind is used when one exists — that is how a
    /// transfer rule works for both the money leaving and the money arriving — and the
    /// line is otherwise left for the user to categorise.
    /// </summary>
    private static int ResolveCategory(List<Category> categories, int suggestedId, EntryKind kind)
    {
        var suggested = categories.FirstOrDefault(c => c.Id == suggestedId);
        if (suggested is null) return 0;
        if (suggested.Kind == kind) return suggested.Id;

        var twin = categories.FirstOrDefault(c => c.Kind == kind && c.Name == suggested.Name);
        return twin?.Id ?? 0;
    }

    /// <summary>
    /// True when the file looks like a credit card export: the bank wrote purchases
    /// as positive numbers. Offered as a default for the sign switch, never forced.
    /// </summary>
    public static bool LooksInverted(ImportPreview preview, AccountType accountType)
    {
        if (accountType != AccountType.CreditCard) return false;

        var valid = preview.Lines.Where(l => l.IsValid).ToList();
        if (valid.Count == 0) return false;

        return valid.Count(l => l.SignedMinor > 0) > valid.Count / 2;
    }

    private static bool TryReadAmount(StatementTable table, string[] row, ColumnMap map, out long signed)
    {
        signed = 0;

        if (!map.UsesDebitCredit)
            return StatementValues.TryAmount(table.Cell(row, map.Amount), out signed);

        var debit = table.Cell(row, map.Debit);
        var credit = table.Cell(row, map.Credit);

        var hasDebit = StatementValues.TryAmount(debit, out var debitValue) && debitValue != 0;
        var hasCredit = StatementValues.TryAmount(credit, out var creditValue) && creditValue != 0;

        if (!hasDebit && !hasCredit) return false;

        // Split columns are already unsigned by convention: debit leaves, credit arrives.
        signed = (hasCredit ? Math.Abs(creditValue) : 0) - (hasDebit ? Math.Abs(debitValue) : 0);
        return true;
    }

    /// <summary>128 bits of SHA-256 as hex — short enough to index, far more than enough to be unique here.</summary>
    private static string Fingerprint(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 16));

    public async Task<ImportResult> CommitAsync(ImportPreview preview, int accountId, string fileName)
    {
        await using var db = await dbf.CreateDbContextAsync();

        var writable = preview.Lines.Where(l => l.Ready).ToList();
        var skipped = preview.Lines.Count - writable.Count;

        var batch = new ImportBatch
        {
            AccountId = accountId,
            FileName = fileName,
            ImportedAtUtc = DateTime.UtcNow,
            Imported = writable.Count,
            Skipped = skipped,
            FirstDate = writable.Count == 0 ? null : writable.Min(l => l.Date),
            LastDate = writable.Count == 0 ? null : writable.Max(l => l.Date)
        };
        db.ImportBatches.Add(batch);
        await db.SaveChangesAsync();

        foreach (var line in writable)
        {
            db.Transactions.Add(new Transaction
            {
                CategoryId = line.CategoryId,
                Kind = line.Kind,
                AmountMinor = line.AmountMinor,
                OccurredOn = line.Date,
                Payee = Truncate(line.Description, 120),
                Note = "",
                Method = PaymentMethod.Card,
                AccountId = accountId,
                IsTransfer = line.IsTransfer,
                ImportHash = line.Hash,
                ImportBatchId = batch.Id,
                CreatedAtUtc = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync();

        log.LogInformation("{File}: {Imported} işlem içe aktarıldı, {Skipped} atlandı.",
            fileName, writable.Count, skipped);

        return new ImportResult(batch.Id, writable.Count, skipped);
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max];

    public async Task<List<ImportBatch>> BatchesAsync(int take = 20)
    {
        await using var db = await dbf.CreateDbContextAsync();
        return await db.ImportBatches.AsNoTracking().Include(b => b.Account)
            .OrderByDescending(b => b.Id)
            .Take(take)
            .ToListAsync();
    }

    /// <summary>
    /// Removes everything one import wrote. The safety net that makes trying an
    /// import on a real ledger a reversible decision.
    /// </summary>
    public async Task<int> UndoAsync(int batchId)
    {
        await using var db = await dbf.CreateDbContextAsync();

        var removed = await db.Transactions.Where(t => t.ImportBatchId == batchId).ExecuteDeleteAsync();
        await db.ImportBatches.Where(b => b.Id == batchId).ExecuteDeleteAsync();

        log.LogInformation("İçe aktarma {Batch} geri alındı, {Count} işlem silindi.", batchId, removed);
        return removed;
    }
}
