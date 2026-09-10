using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ExpenseTracker.Data;
using ExpenseTracker.Services.Import;

namespace ExpenseTracker.Tests;

/// <summary>
/// Re-importing an overlapping statement is the normal case — banks hand out the
/// last 90 days every time. It has to be a no-op, and two genuinely identical
/// purchases on the same day still have to survive as two rows.
/// </summary>
public class ImportDedupeTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestContextFactory _factory;
    private readonly StatementImporter _importer;

    private const int AccountId = 1;
    private const int CategoryId = 1;

    public ImportDedupeTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _factory = new TestContextFactory(options);

        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
        db.Categories.Add(new Category { Id = CategoryId, Name = "Market", Kind = EntryKind.Expense });
        db.Accounts.Add(new Account { Id = AccountId, Name = "Banka", Type = AccountType.Bank });
        db.SaveChanges();

        _importer = new StatementImporter(_factory, NullLogger<StatementImporter>.Instance);
    }

    private static StatementTable Statement() => StatementReader.ReadDelimited(string.Join('\n',
        "Tarih;Açıklama;Tutar",
        "03.08.2026;MIGROS KADIKOY;-125,40",
        "03.08.2026;KAHVE DUNYASI;-85,00",
        // The same coffee twice on the same day: two rows, not one.
        "03.08.2026;KAHVE DUNYASI;-85,00",
        "04.08.2026;MAAS ODEMESI;25.000,00"));

    private static ColumnMap Map() => new() { Date = 0, Description = 1, Amount = 2 };

    [Fact]
    public async Task Reads_every_line_and_signs_them_by_direction()
    {
        var preview = await _importer.PreviewAsync(Statement(), Map(), AccountId);

        Assert.Equal(4, preview.Valid);
        Assert.Equal(0, preview.Failed);
        Assert.Equal(3, preview.Lines.Count(l => l.Kind == EntryKind.Expense));
        Assert.Single(preview.Lines, l => l.Kind == EntryKind.Income);
        Assert.Equal(2500000, preview.Lines.Single(l => l.Kind == EntryKind.Income).AmountMinor);
    }

    [Fact]
    public async Task Two_identical_purchases_get_two_fingerprints()
    {
        var preview = await _importer.PreviewAsync(Statement(), Map(), AccountId);

        var coffees = preview.Lines.Where(l => l.Description.Contains("KAHVE")).ToList();
        Assert.Equal(2, coffees.Count);
        Assert.NotEqual(coffees[0].Hash, coffees[1].Hash);
    }

    [Fact]
    public async Task Importing_the_same_file_twice_writes_nothing_the_second_time()
    {
        var first = await _importer.PreviewAsync(Statement(), Map(), AccountId);
        foreach (var line in first.Lines) line.CategoryId = CategoryId;

        var result = await _importer.CommitAsync(first, AccountId, "ekstre.csv");
        Assert.Equal(4, result.Imported);

        var second = await _importer.PreviewAsync(Statement(), Map(), AccountId);
        Assert.Equal(4, second.Duplicates);
        Assert.Equal(0, second.Ready);
    }

    [Fact]
    public async Task An_overlapping_statement_only_adds_what_is_new()
    {
        var first = await _importer.PreviewAsync(Statement(), Map(), AccountId);
        foreach (var line in first.Lines) line.CategoryId = CategoryId;
        await _importer.CommitAsync(first, AccountId, "agustos.csv");

        var overlapping = StatementReader.ReadDelimited(string.Join('\n',
            "Tarih;Açıklama;Tutar",
            "04.08.2026;MAAS ODEMESI;25.000,00",
            "05.08.2026;SHELL ATASEHIR;-1.200,00"));

        var second = await _importer.PreviewAsync(overlapping, Map(), AccountId);
        foreach (var line in second.Lines) line.CategoryId = CategoryId;

        Assert.Equal(1, second.Duplicates);
        Assert.Equal(1, second.Ready);

        var result = await _importer.CommitAsync(second, AccountId, "eylul.csv");
        Assert.Equal(1, result.Imported);

        await using var db = _factory.CreateDbContext();
        Assert.Equal(5, await db.Transactions.CountAsync());
    }

    [Fact]
    public async Task A_line_without_a_category_is_never_written()
    {
        var preview = await _importer.PreviewAsync(Statement(), Map(), AccountId);
        foreach (var line in preview.Lines) line.CategoryId = 0;

        var result = await _importer.CommitAsync(preview, AccountId, "ekstre.csv");

        Assert.Equal(0, result.Imported);
        Assert.Equal(4, result.Skipped);
    }

    [Fact]
    public async Task Undoing_a_batch_removes_exactly_what_it_wrote()
    {
        var preview = await _importer.PreviewAsync(Statement(), Map(), AccountId);
        foreach (var line in preview.Lines) line.CategoryId = CategoryId;
        var result = await _importer.CommitAsync(preview, AccountId, "ekstre.csv");

        var removed = await _importer.UndoAsync(result.BatchId);

        Assert.Equal(4, removed);
        await using var db = _factory.CreateDbContext();
        Assert.Equal(0, await db.Transactions.CountAsync());
        Assert.Equal(0, await db.ImportBatches.CountAsync());

        // And the ledger is genuinely clean, so the file can be imported again.
        var again = await _importer.PreviewAsync(Statement(), Map(), AccountId);
        Assert.Equal(0, again.Duplicates);
    }

    [Fact]
    public async Task Inverting_the_sign_turns_a_card_export_the_right_way_round()
    {
        // A credit card lists purchases as positive numbers.
        var card = StatementReader.ReadDelimited(string.Join('\n',
            "Tarih;Açıklama;Tutar",
            "03.08.2026;MIGROS KADIKOY;125,40",
            "10.08.2026;KREDI KARTI ODEMESI;-5.000,00"));

        var map = new ColumnMap { Date = 0, Description = 1, Amount = 2, InvertSign = true };
        var preview = await _importer.PreviewAsync(card, map, AccountId);

        var purchase = preview.Lines[0];
        Assert.Equal(EntryKind.Expense, purchase.Kind);
        Assert.Equal(12540, purchase.AmountMinor);

        var payment = preview.Lines[1];
        Assert.Equal(EntryKind.Income, payment.Kind);
    }

    [Fact]
    public async Task A_rule_never_files_income_under_a_spending_category()
    {
        await using (var db = _factory.CreateDbContext())
        {
            db.CategoryRules.Add(new CategoryRule { Contains = "IADE", CategoryId = CategoryId });
            await db.SaveChangesAsync();
        }

        var refund = StatementReader.ReadDelimited(string.Join('\n',
            "Tarih;Açıklama;Tutar",
            "03.08.2026;IADE MIGROS;125,40"));

        var preview = await _importer.PreviewAsync(refund, Map(), AccountId);

        // "Market" is an expense category and the line is income, so nothing is guessed.
        Assert.Equal(EntryKind.Income, preview.Lines[0].Kind);
        Assert.Equal(0, preview.Lines[0].CategoryId);
        Assert.Equal(0, preview.Ready);
    }

    [Fact]
    public async Task A_transfer_rule_uses_the_twin_category_in_the_other_direction()
    {
        int expenseSide, incomeSide;
        await using (var db = _factory.CreateDbContext())
        {
            var outgoing = new Category { Name = "Hesaplar arası", Kind = EntryKind.Expense };
            var incoming = new Category { Name = "Hesaplar arası", Kind = EntryKind.Income };
            db.Categories.AddRange(outgoing, incoming);
            await db.SaveChangesAsync();

            expenseSide = outgoing.Id;
            incomeSide = incoming.Id;

            db.CategoryRules.Add(new CategoryRule
            {
                Contains = "KREDI KARTI ODEMESI",
                CategoryId = expenseSide,
                MarksTransfer = true
            });
            await db.SaveChangesAsync();
        }

        var statement = StatementReader.ReadDelimited(string.Join('\n',
            "Tarih;Açıklama;Tutar",
            "10.08.2026;KREDI KARTI ODEMESI;-8.500,00",
            "10.09.2026;KREDI KARTI ODEMESI;8.500,00"));

        var preview = await _importer.PreviewAsync(statement, Map(), AccountId);

        Assert.Equal(expenseSide, preview.Lines[0].CategoryId);
        Assert.Equal(incomeSide, preview.Lines[1].CategoryId);
        Assert.All(preview.Lines, l => Assert.True(l.IsTransfer));
    }

    [Fact]
    public async Task Rows_that_cannot_be_parsed_are_reported_not_dropped()
    {
        var messy = StatementReader.ReadDelimited(string.Join('\n',
            "Tarih;Açıklama;Tutar",
            "03.08.2026;MIGROS;-125,40",
            "TOPLAM;;-125,40"));

        var preview = await _importer.PreviewAsync(messy, Map(), AccountId);

        Assert.Equal(2, preview.Lines.Count);
        Assert.Equal(1, preview.Failed);
        Assert.NotNull(preview.Lines[1].Error);
    }

    public void Dispose() => _connection.Dispose();

    private sealed class TestContextFactory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }
}
