using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PaymentOrchestration.Api;

namespace PaymentOrchestration.Tests;

public sealed class ReconciliationServiceTests
{
    [Fact]
    public async Task ProcessesSettlementRowsAndReportsEveryFindingType()
    {
        await using var testDatabase = await TestDatabase.CreateAsync();
        var service = new ReconciliationService(testDatabase.Context, TimeProvider.System);
        var settledAt = new DateTimeOffset(2026, 11, 12, 10, 30, 0, TimeSpan.Zero);
        await service.CreateInternalTransactionAsync(
            new("PAY-1001", 12550, "usd", settledAt.AddMinutes(-4)),
            CancellationToken.None);
        await service.CreateInternalTransactionAsync(
            new("PAY-1002", 500, "USD", settledAt.AddMinutes(-3)),
            CancellationToken.None);
        await service.CreateInternalTransactionAsync(
            new("PAY-1003", 750, "EUR", settledAt.AddMinutes(-2)),
            CancellationToken.None);
        await service.CreateInternalTransactionAsync(
            new("PAY-1004", 1000, "USD", settledAt.AddDays(-1)),
            CancellationToken.None);
        const string csv = """
            transactionReference,amount,currency,settledAt
            PAY-1001,125.50,USD,2026-11-12T10:30:00Z
            PAY-1001,125.50,USD,2026-11-12T10:30:00Z
            PAY-1002,5.51,CAD,2026-11-12T10:31:00Z
            PAY-UNKNOWN,10.00,USD,2026-11-12T10:32:00Z
            PAY-1004,10.00,USD,2026-11-12T10:33:00Z
            """;

        var batch = await service.ImportSettlementCsvAsync("bank.csv", csv, CancellationToken.None);
        await service.ProcessBatchAsync(batch.Id, CancellationToken.None);
        var report = await service.GetBatchAsync(batch.Id, CancellationToken.None);

        Assert.NotNull(report);
        Assert.Equal(ReconciliationBatchStatus.Completed, report.Batch.Status);
        Assert.Equal(new ReconciliationBatchSummary(6, 2, 1, 1, 1, 1), report.Batch.Summary);
        Assert.Equal(
            new[]
            {
                ReconciliationItemStatus.Matched,
                ReconciliationItemStatus.Duplicate,
                ReconciliationItemStatus.Mismatched,
                ReconciliationItemStatus.MissingInternal,
                ReconciliationItemStatus.Matched,
                ReconciliationItemStatus.MissingSettlement
            },
            report.Items.Select(item => item.Status));
        Assert.Contains(report.Items, item => item.Detail!.Contains("amount", StringComparison.Ordinal));
        Assert.Contains(report.Items, item => item.Detail!.Contains("currency", StringComparison.Ordinal));
        Assert.Contains(report.AuditEntries, entry => entry.Event == "reconciliation_duplicate");
        Assert.Contains(report.AuditEntries, entry => entry.Event == "missing_settlement");
        Assert.Single(await service.ListBatchesAsync(10, CancellationToken.None));
        Assert.Equal(4, (await service.ListInternalTransactionsAsync(10, CancellationToken.None)).Count);
        Assert.True((await service.ListAuditAsync(100, CancellationToken.None)).Count >= report.AuditEntries.Count);
    }

    [Fact]
    public async Task RejectsMalformedCsvWithoutCreatingBatch()
    {
        await using var testDatabase = await TestDatabase.CreateAsync();
        var service = new ReconciliationService(testDatabase.Context, TimeProvider.System);

        await Assert.ThrowsAsync<ReconciliationInputException>(() =>
            service.ImportSettlementCsvAsync(
                "bad.csv",
                "transactionReference,amount,currency,settledAt\nPAY-1,1.001,USD,not-a-date",
                CancellationToken.None));

        Assert.Empty(await testDatabase.Context.ReconciliationBatches.ToArrayAsync());
    }

    [Fact]
    public async Task ParsesQuotedReferencesAndPreventsCaseInsensitiveInternalDuplicates()
    {
        await using var testDatabase = await TestDatabase.CreateAsync();
        var service = new ReconciliationService(testDatabase.Context, TimeProvider.System);
        var occurredAt = new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
        await service.CreateInternalTransactionAsync(
            new("PAY,1001", 250, "USD", occurredAt),
            CancellationToken.None);

        await Assert.ThrowsAsync<ReconciliationConflictException>(() =>
            service.CreateInternalTransactionAsync(
                new("pay,1001", 250, "USD", occurredAt),
                CancellationToken.None));

        var batch = await service.ImportSettlementCsvAsync(
            "quoted.csv",
            """
            transactionReference,amount,currency,settledAt
            "PAY,1001",2.50,USD,2026-11-12T10:30:00Z
            """,
            CancellationToken.None);
        await service.ProcessBatchAsync(batch.Id, CancellationToken.None);

        var report = await service.GetBatchAsync(batch.Id, CancellationToken.None);
        Assert.NotNull(report);
        Assert.Equal(ReconciliationItemStatus.Matched, Assert.Single(report.Items).Status);
    }

    [Fact]
    public async Task SchemaInitializerAddsReconciliationTablesToAnExistingDatabase()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE Payments (Id TEXT NOT NULL PRIMARY KEY);";
        await command.ExecuteNonQueryAsync();
        var context = new PaymentDbContext(
            new DbContextOptionsBuilder<PaymentDbContext>()
                .UseSqlite(connection)
                .Options);
        await using (context)
        {
            await ReconciliationSchema.EnsureCreatedAsync(context, CancellationToken.None);
            context.InternalTransactions.Add(new InternalTransaction
            {
                Id = "internal-1",
                ExternalReference = "PAY-1",
                AmountMinor = 100,
                Currency = "USD",
                OccurredAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await context.SaveChangesAsync();
            Assert.Single(await context.InternalTransactions.ToArrayAsync());
        }

        await connection.DisposeAsync();
    }

    private sealed class TestDatabase(SqliteConnection connection, PaymentDbContext context) : IAsyncDisposable
    {
        public PaymentDbContext Context { get; } = context;

        public static async Task<TestDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var context = new PaymentDbContext(
                new DbContextOptionsBuilder<PaymentDbContext>()
                    .UseSqlite(connection)
                    .Options);
            await context.Database.EnsureCreatedAsync();
            return new TestDatabase(connection, context);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
