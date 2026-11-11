using Microsoft.EntityFrameworkCore;

namespace PaymentOrchestration.Api;

public sealed class PaymentDbContext(DbContextOptions<PaymentDbContext> options) : DbContext(options)
{
    public DbSet<PaymentTransaction> Payments => Set<PaymentTransaction>();
    public DbSet<PaymentAttempt> Attempts => Set<PaymentAttempt>();
    public DbSet<PaymentAuditEntry> AuditEntries => Set<PaymentAuditEntry>();
    public DbSet<ProcessedWebhook> ProcessedWebhooks => Set<ProcessedWebhook>();

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
    }
}
