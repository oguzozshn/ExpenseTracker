using Microsoft.EntityFrameworkCore;
using ExpenseTracker.Components;
using ExpenseTracker.Data;
using ExpenseTracker.Services;
using ExpenseTracker.Services.Import;

var builder = WebApplication.CreateBuilder(args);

// The ledger lives in the user profile, not next to the binaries, so a rebuild
// or a republish never wipes the accumulated history.
var dataDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "ExpenseTracker");
Directory.CreateDirectory(dataDir);
var dbPath = Path.Combine(dataDir, "expenses.db");

builder.Services.AddDbContextFactory<AppDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));
builder.Services.AddSingleton(new DataPaths(dataDir, dbPath));

// SettingsService caches its values, so it has to outlive the request; the rest
// only wrap the context factory and stay scoped.
builder.Services.AddSingleton<SettingsService>();
builder.Services.AddScoped<CategoryService>();
builder.Services.AddScoped<TransactionService>();
builder.Services.AddScoped<BudgetService>();
builder.Services.AddScoped<RecurringService>();
builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<CategoryRuleService>();
builder.Services.AddScoped<StatementImporter>();
builder.Services.AddScoped<ForecastService>();

builder.Services.AddHostedService<RecurringBackgroundService>();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var dbf = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    await using var db = await dbf.CreateDbContextAsync();
    await db.Database.MigrateAsync();
    await CategoryService.SeedDefaultsAsync(db);
    await AccountService.SeedDefaultsAsync(db);
    await CategoryRuleService.SeedDefaultsAsync(db);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Exporting through a plain GET keeps the download out of the SignalR circuit:
// a Blazor Server page cannot hand the browser a file without JS interop.
app.MapGet("/export.csv", async (
    TransactionService transactions,
    DateOnly? from,
    DateOnly? to,
    int? category,
    int? account,
    string? kind,
    string? search) =>
{
    var period = from is not null && to is not null ? new Period(from.Value, to.Value) : (Period?)null;

    // kind arrives as "", "0" or "1" straight from the filter dropdown.
    EntryKind? kindFilter = kind switch
    {
        "0" => EntryKind.Expense,
        "1" => EntryKind.Income,
        _ => null
    };

    var csv = await transactions.ExportCsvAsync(new TransactionFilter(
        Period: period,
        CategoryId: category is > 0 ? category : null,
        AccountId: account is > 0 ? account : null,
        Kind: kindFilter,
        Search: string.IsNullOrWhiteSpace(search) ? null : search));

    var name = period is { } p ? $"harcamalar-{p.Start:yyyy-MM-dd}-{p.End:yyyy-MM-dd}.csv" : "harcamalar.csv";

    // Excel in a Turkish locale needs the BOM to detect UTF-8.
    var bytes = new System.Text.UTF8Encoding(true).GetBytes(csv);
    return Results.File(bytes, "text/csv; charset=utf-8", name);
});

app.Run();
