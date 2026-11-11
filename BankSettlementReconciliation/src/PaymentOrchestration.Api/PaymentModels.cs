namespace PaymentOrchestration.Api;

public enum PaymentStatus
{
    Processing,
    Succeeded,
    Failed
}

public sealed class PaymentTransaction
{
    public required string Id { get; set; }
    public required string IdempotencyKey { get; set; }
    public required string RequestFingerprint { get; set; }
    public long AmountMinor { get; set; }
    public required string Currency { get; set; }
    public required string Provider { get; set; }
    public string? ProviderReference { get; set; }
    public PaymentStatus Status { get; set; }
    public string? FailureCode { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<PaymentAttempt> Attempts { get; set; } = [];
    public List<PaymentAuditEntry> AuditEntries { get; set; } = [];
}

public sealed class PaymentAttempt
{
    public long Id { get; set; }
    public required string PaymentId { get; set; }
    public int Number { get; set; }
    public bool Succeeded { get; set; }
    public string? FailureCode { get; set; }
    public string? ProviderReference { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class PaymentAuditEntry
{
    public long Id { get; set; }
    public required string PaymentId { get; set; }
    public required string Event { get; set; }
    public required string Actor { get; set; }
    public string? Detail { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ProcessedWebhook
{
    public long Id { get; set; }
    public required string Provider { get; set; }
    public required string EventId { get; set; }
    public required string PaymentId { get; set; }
    public DateTimeOffset ProcessedAt { get; set; }
}

public sealed record CreatePaymentRequest(
    long AmountMinor,
    string Currency,
    string Provider,
    string PaymentToken);

public sealed record PaymentWebhookRequest(string EventId, string TransactionId, string Status);

public sealed record PaymentDetails(
    string Id,
    long AmountMinor,
    string Currency,
    string Provider,
    string? ProviderReference,
    PaymentStatus Status,
    string? FailureCode,
    int AttemptCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<PaymentAttemptView> Attempts);

public sealed record PaymentAttemptView(
    int Number,
    bool Succeeded,
    string? FailureCode,
    string? ProviderReference,
    DateTimeOffset CreatedAt);

public sealed class PaymentInputException(string message) : Exception(message);

public sealed class PaymentConflictException(string message) : Exception(message);

public sealed class InvalidWebhookSignatureException() : Exception("The webhook signature is invalid or expired.");

public sealed class WebhookProcessingException(string message) : Exception(message);

public sealed class PaymentProcessingOptions
{
    public int MaxAttempts { get; set; } = 3;
    public int RetryDelayMilliseconds { get; set; } = 100;
}

public sealed class PaymentGatewayOptions
{
    public string? SandboxBaseUrl { get; set; }
    public string? SandboxApiKey { get; set; }
    public Dictionary<string, SandboxProviderConfiguration> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class SandboxProviderConfiguration
{
    public string? BaseUrl { get; set; }
    public string? ApiKey { get; set; }
}

public sealed class PaymentWebhookOptions
{
    public string? SigningSecret { get; set; }
    public int ToleranceSeconds { get; set; } = 300;
}

internal static class PaymentMapping
{
    public static PaymentDetails ToDetails(this PaymentTransaction payment) =>
        new(
            payment.Id,
            payment.AmountMinor,
            payment.Currency,
            payment.Provider,
            payment.ProviderReference,
            payment.Status,
            payment.FailureCode,
            payment.AttemptCount,
            payment.CreatedAt,
            payment.UpdatedAt,
            payment.Attempts
                .OrderBy(attempt => attempt.Number)
                .Select(attempt => new PaymentAttemptView(
                    attempt.Number,
                    attempt.Succeeded,
                    attempt.FailureCode,
                    attempt.ProviderReference,
                    attempt.CreatedAt))
                .ToArray());
}
