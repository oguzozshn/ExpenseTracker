using Microsoft.EntityFrameworkCore;

namespace ExpenseTracker.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<AppSetting> Settings => Set<AppSetting>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<Budget> Budgets => Set<Budget>();
    public DbSet<RecurringRule> RecurringRules => Set<RecurringRule>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<CategoryRule> CategoryRules => Set<CategoryRule>();
    public DbSet<ImportBatch> ImportBatches => Set<ImportBatch>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<AppSetting>()
            .HasIndex(x => x.Key).IsUnique();

        b.Entity<Category>(e =>
        {
            // The same word can be both a category of spending and of income
            // ("Kira" paid vs. rent received), so the kind is part of the key.
            e.HasIndex(x => new { x.Name, x.Kind }).IsUnique();
            e.HasIndex(x => x.SortOrder);
        });

        b.Entity<Transaction>(e =>
        {
            e.HasIndex(x => x.OccurredOn);
            e.HasIndex(x => new { x.CategoryId, x.OccurredOn });

            // A rule may fire at most once per date. The scheduler can therefore
            // run as often as it likes — a catch-up pass after a week offline
            // replays the same dates without creating duplicates.
            e.HasIndex(x => new { x.RecurringRuleId, x.OccurredOn })
                .IsUnique()
                .HasFilter("[RecurringRuleId] IS NOT NULL");

            // Deleting a category that still has history would silently destroy
            // past totals; the UI archives instead.
            e.HasOne(x => x.Category)
                .WithMany()
                .HasForeignKey(x => x.CategoryId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(x => x.Account)
                .WithMany()
                .HasForeignKey(x => x.AccountId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(x => new { x.AccountId, x.OccurredOn });

            // The dedupe key for imports. Filtered, so the many hand-entered rows
            // with a null hash do not collide with each other.
            e.HasIndex(x => new { x.AccountId, x.ImportHash })
                .IsUnique()
                .HasFilter("[ImportHash] IS NOT NULL");

            e.HasIndex(x => x.ImportBatchId);
        });

        b.Entity<Account>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
            e.HasIndex(x => x.SortOrder);
        });

        b.Entity<CategoryRule>(e =>
        {
            e.HasIndex(x => x.Contains).IsUnique();
            e.HasOne(x => x.Category)
                .WithMany()
                .HasForeignKey(x => x.CategoryId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ImportBatch>(e =>
        {
            e.HasOne(x => x.Account)
                .WithMany()
                .HasForeignKey(x => x.AccountId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Budget>(e =>
        {
            e.HasIndex(x => x.CategoryId).IsUnique();
            e.HasOne(x => x.Category)
                .WithMany()
                .HasForeignKey(x => x.CategoryId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RecurringRule>(e =>
        {
            e.HasOne(x => x.Category)
                .WithMany()
                .HasForeignKey(x => x.CategoryId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(x => x.Account)
                .WithMany()
                .HasForeignKey(x => x.AccountId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
