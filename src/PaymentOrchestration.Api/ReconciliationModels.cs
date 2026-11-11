namespace PaymentOrchestration.Api;

public enum ReconciliationBatchStatus
{
    Pending,
    Processing,
    Completed,
    Failed
}

public enum ReconciliationItemStatus
{
    Pending,
    Matched,
    Duplicate,
    Mismatched,
    MissingInternal,
    MissingSettlement
}

public sealed class InternalTransaction
{
    public required string Id { get; set; }
    public required string ExternalReference { get; set; }
    public long AmountMinor { get; set; }
    public required string Currency { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ReconciliationBatch
{
    public required string Id { get; set; }
    public required string FileName { get; set; }
    public ReconciliationBatchStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public List<SettlementItem> Items { get; set; } = [];
    public List<ReconciliationAuditEntry> AuditEntries { get; set; } = [];
}

public sealed class SettlementItem
{
    public long Id { get; set; }
    public required string BatchId { get; set; }
    public int RowNumber { get; set; }
    public required string Source { get; set; }
    public required string TransactionReference { get; set; }
    public long AmountMinor { get; set; }
    public required string Currency { get; set; }
    public DateTimeOffset SettledAt { get; set; }
    public ReconciliationItemStatus Status { get; set; }
    public string? InternalTransactionId { get; set; }
    public string? Detail { get; set; }
}

public sealed class ReconciliationAuditEntry
{
    public long Id { get; set; }
    public string? BatchId { get; set; }
    public string? InternalTransactionId { get; set; }
    public required string Event { get; set; }
    public required string Actor { get; set; }
    public string? Detail { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed record CreateInternalTransactionRequest(
    string ExternalReference,
    long AmountMinor,
    string Currency,
    DateTimeOffset OccurredAt);

public sealed record InternalTransactionView(
    string Id,
    string ExternalReference,
    long AmountMinor,
    string Currency,
    DateTimeOffset OccurredAt,
    DateTimeOffset CreatedAt);

public sealed record ReconciliationBatchSummary(
    int Total,
    int Matched,
    int Duplicate,
    int Mismatched,
    int MissingInternal,
    int MissingSettlement);

public sealed record ReconciliationBatchView(
    string Id,
    string FileName,
    ReconciliationBatchStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ProcessedAt,
    string? ErrorMessage,
    ReconciliationBatchSummary Summary);

public sealed record ReconciliationItemView(
    int RowNumber,
    string Source,
    string TransactionReference,
    long AmountMinor,
    string Currency,
    DateTimeOffset SettledAt,
    ReconciliationItemStatus Status,
    string? InternalTransactionId,
    string? Detail);

public sealed record ReconciliationAuditView(
    long Id,
    string? BatchId,
    string? InternalTransactionId,
    string Event,
    string Actor,
    string? Detail,
    DateTimeOffset CreatedAt);

public sealed record ReconciliationBatchDetails(
    ReconciliationBatchView Batch,
    IReadOnlyList<ReconciliationItemView> Items,
    IReadOnlyList<ReconciliationAuditView> AuditEntries);

public sealed class ReconciliationInputException(string message) : Exception(message);

public sealed class ReconciliationConflictException(string message) : Exception(message);

public sealed class ReconciliationQueueUnavailableException(string message, Exception innerException)
    : Exception(message, innerException);

public sealed class RabbitMqOptions
{
    public string HostName { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string UserName { get; set; } = "guest";
    public string? Password { get; set; }
    public string VirtualHost { get; set; } = "/";
    public string QueueName { get; set; } = "reconciliation.jobs";
    public int RetryDelaySeconds { get; set; } = 5;
}

internal static class ReconciliationMapping
{
    public static InternalTransactionView ToView(this InternalTransaction transaction) =>
        new(
            transaction.Id,
            transaction.ExternalReference,
            transaction.AmountMinor,
            transaction.Currency,
            transaction.OccurredAt,
            transaction.CreatedAt);

    public static ReconciliationBatchView ToView(this ReconciliationBatch batch)
    {
        var counts = batch.Items
            .Where(item => item.Status != ReconciliationItemStatus.Pending)
            .GroupBy(item => item.Status)
            .ToDictionary(group => group.Key, group => group.Count());
        return batch.ToView(new(
            batch.Items.Count,
            counts.GetValueOrDefault(ReconciliationItemStatus.Matched),
            counts.GetValueOrDefault(ReconciliationItemStatus.Duplicate),
            counts.GetValueOrDefault(ReconciliationItemStatus.Mismatched),
            counts.GetValueOrDefault(ReconciliationItemStatus.MissingInternal),
            counts.GetValueOrDefault(ReconciliationItemStatus.MissingSettlement)));
    }

    public static ReconciliationBatchView ToView(
        this ReconciliationBatch batch,
        ReconciliationBatchSummary summary) =>
        new(
            batch.Id,
            batch.FileName,
            batch.Status,
            batch.CreatedAt,
            batch.ProcessedAt,
            batch.ErrorMessage,
            summary);

    public static ReconciliationBatchDetails ToDetails(this ReconciliationBatch batch) =>
        new(
            batch.ToView(),
            batch.Items
                .OrderBy(item => item.RowNumber)
                .Select(item => new ReconciliationItemView(
                    item.RowNumber,
                    item.Source,
                    item.TransactionReference,
                    item.AmountMinor,
                    item.Currency,
                    item.SettledAt,
                    item.Status,
                    item.InternalTransactionId,
                    item.Detail))
                .ToArray(),
            batch.AuditEntries
                .OrderBy(entry => entry.CreatedAt)
                .ThenBy(entry => entry.Id)
                .Select(entry => new ReconciliationAuditView(
                    entry.Id,
                    entry.BatchId,
                    entry.InternalTransactionId,
                    entry.Event,
                    entry.Actor,
                    entry.Detail,
                    entry.CreatedAt))
                .ToArray());
}
