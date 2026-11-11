using Microsoft.EntityFrameworkCore;

namespace PaymentOrchestration.Api;

public sealed class PaymentDbContext(DbContextOptions<PaymentDbContext> options) : DbContext(options)
{
    public DbSet<PaymentTransaction> Payments => Set<PaymentTransaction>();
    public DbSet<PaymentAttempt> Attempts => Set<PaymentAttempt>();
    public DbSet<PaymentAuditEntry> AuditEntries => Set<PaymentAuditEntry>();
    public DbSet<ProcessedWebhook> ProcessedWebhooks => Set<ProcessedWebhook>();
    public DbSet<InternalTransaction> InternalTransactions => Set<InternalTransaction>();
    public DbSet<ReconciliationBatch> ReconciliationBatches => Set<ReconciliationBatch>();
    public DbSet<SettlementItem> SettlementItems => Set<SettlementItem>();
    public DbSet<ReconciliationAuditEntry> ReconciliationAuditEntries => Set<ReconciliationAuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PaymentTransaction>(entity =>
        {
            entity.HasKey(payment => payment.Id);
            entity.HasIndex(payment => payment.IdempotencyKey).IsUnique();
            entity.Property(payment => payment.IdempotencyKey).HasMaxLength(200);
            entity.Property(payment => payment.RequestFingerprint).HasMaxLength(64);
            entity.Property(payment => payment.Currency).HasMaxLength(3);
            entity.Property(payment => payment.Provider).HasMaxLength(50);
            entity.Property(payment => payment.ProviderReference).HasMaxLength(200);
            entity.Property(payment => payment.FailureCode).HasMaxLength(100);
            entity.HasMany(payment => payment.Attempts)
                .WithOne()
                .HasForeignKey(attempt => attempt.PaymentId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(payment => payment.AuditEntries)
                .WithOne()
                .HasForeignKey(entry => entry.PaymentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PaymentAttempt>(entity =>
        {
            entity.HasKey(attempt => attempt.Id);
            entity.HasIndex(attempt => new { attempt.PaymentId, attempt.Number }).IsUnique();
        });

        modelBuilder.Entity<PaymentAuditEntry>(entity =>
        {
            entity.HasKey(entry => entry.Id);
            entity.Property(entry => entry.Detail).HasMaxLength(300);
        });

        modelBuilder.Entity<ProcessedWebhook>(entity =>
        {
            entity.HasKey(webhook => webhook.Id);
            entity.HasIndex(webhook => new { webhook.Provider, webhook.EventId }).IsUnique();
            entity.Property(webhook => webhook.Provider).HasMaxLength(50);
            entity.Property(webhook => webhook.EventId).HasMaxLength(200);
        });

        modelBuilder.Entity<InternalTransaction>(entity =>
        {
            entity.HasKey(transaction => transaction.Id);
            entity.HasIndex(transaction => transaction.ExternalReference).IsUnique();
            entity.Property(transaction => transaction.ExternalReference)
                .HasMaxLength(200)
                .UseCollation("NOCASE");
            entity.Property(transaction => transaction.Currency).HasMaxLength(3);
        });

        modelBuilder.Entity<ReconciliationBatch>(entity =>
        {
            entity.HasKey(batch => batch.Id);
            entity.Property(batch => batch.FileName).HasMaxLength(255);
            entity.Property(batch => batch.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(batch => batch.ErrorMessage).HasMaxLength(500);
            entity.HasMany(batch => batch.Items)
                .WithOne()
                .HasForeignKey(item => item.BatchId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(batch => batch.AuditEntries)
                .WithOne()
                .HasForeignKey(entry => entry.BatchId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SettlementItem>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.BatchId, item.RowNumber }).IsUnique();
            entity.HasIndex(item => item.InternalTransactionId);
            entity.Property(item => item.Source).HasMaxLength(20);
            entity.Property(item => item.TransactionReference).HasMaxLength(200);
            entity.Property(item => item.Currency).HasMaxLength(3);
            entity.Property(item => item.Status).HasConversion<string>().HasMaxLength(30);
            entity.Property(item => item.Detail).HasMaxLength(500);
            entity.HasOne<InternalTransaction>()
                .WithMany()
                .HasForeignKey(item => item.InternalTransactionId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ReconciliationAuditEntry>(entity =>
        {
            entity.HasKey(entry => entry.Id);
            entity.HasIndex(entry => new { entry.BatchId, entry.CreatedAt });
            entity.HasIndex(entry => entry.InternalTransactionId);
            entity.Property(entry => entry.Event).HasMaxLength(100);
            entity.Property(entry => entry.Actor).HasMaxLength(100);
            entity.Property(entry => entry.Detail).HasMaxLength(500);
            entity.HasOne<InternalTransaction>()
                .WithMany()
                .HasForeignKey(entry => entry.InternalTransactionId)
                .OnDelete(DeleteBehavior.SetNull);
        });
    }
}
