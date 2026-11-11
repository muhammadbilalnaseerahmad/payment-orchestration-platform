using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace PaymentOrchestration.Api;

public sealed class WebhookSignatureValidator(
    IOptions<PaymentWebhookOptions> options,
    TimeProvider timeProvider)
{
    private readonly PaymentWebhookOptions _options = options.Value;

    public bool IsValid(ReadOnlySpan<byte> body, string? signature)
    {
        if (string.IsNullOrWhiteSpace(_options.SigningSecret)
            || Encoding.UTF8.GetByteCount(_options.SigningSecret) < 32
            || string.IsNullOrWhiteSpace(signature)
            || _options.ToleranceSeconds <= 0)
        {
            return false;
        }

        long timestamp = 0;
        var signatures = new List<string>();
        foreach (var part in signature.Split(','))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2)
            {
                continue;
            }

            if (pair[0] == "t")
            {
                long.TryParse(pair[1], NumberStyles.None, CultureInfo.InvariantCulture, out timestamp);
            }
            else if (pair[0] == "v1")
            {
                signatures.Add(pair[1]);
            }
        }

        var currentTimestamp = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        if (timestamp <= 0
            || Math.Abs((double)currentTimestamp - timestamp) > _options.ToleranceSeconds
            || signatures.Count == 0)
        {
            return false;
        }

        var prefix = Encoding.UTF8.GetBytes($"{timestamp}.");
        var signedPayload = new byte[prefix.Length + body.Length];
        prefix.CopyTo(signedPayload, 0);
        body.CopyTo(signedPayload.AsSpan(prefix.Length));
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(_options.SigningSecret), signedPayload);

        foreach (var candidate in signatures)
        {
            try
            {
                var provided = Convert.FromHexString(candidate);
                if (provided.Length == expected.Length
                    && CryptographicOperations.FixedTimeEquals(provided, expected))
                {
                    return true;
                }
            }
            catch (FormatException)
            {
                // Ignore malformed signatures; all supplied v1 values must fail validation.
            }
        }

        return false;
    }
}

public sealed class WebhookService(
    PaymentDbContext database,
    WebhookSignatureValidator signatureValidator,
    TimeProvider timeProvider)
{
    public async Task<bool> HandleAsync(
        string provider,
        ReadOnlyMemory<byte> body,
        string? signature,
        CancellationToken cancellationToken)
    {
        if (!signatureValidator.IsValid(body.Span, signature))
        {
            throw new InvalidWebhookSignatureException();
        }

        PaymentWebhookRequest webhookEvent;
        try
        {
            webhookEvent = JsonSerializer.Deserialize<PaymentWebhookRequest>(
                body.Span,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw new WebhookProcessingException("Webhook body must be a valid payment event.");
        }

        if (string.IsNullOrWhiteSpace(webhookEvent.EventId)
            || webhookEvent.EventId.Length > 200
            || string.IsNullOrWhiteSpace(webhookEvent.TransactionId)
            || string.IsNullOrWhiteSpace(webhookEvent.Status)
            || string.IsNullOrWhiteSpace(provider))
        {
            throw new WebhookProcessingException("Webhook must contain an eventId, transactionId, and terminal status.");
        }

        var nextStatus = webhookEvent.Status.ToLowerInvariant() switch
        {
            "succeeded" => PaymentStatus.Succeeded,
            "failed" => PaymentStatus.Failed,
            _ => throw new WebhookProcessingException(
                "Webhook must contain an eventId, transactionId, and terminal status.")
        };

        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var existingEvent = await database.ProcessedWebhooks
            .AnyAsync(item => item.Provider == provider && item.EventId == webhookEvent.EventId, cancellationToken);
        if (existingEvent)
        {
            return true;
        }

        var payment = await database.Payments.SingleOrDefaultAsync(
            item => item.Id == webhookEvent.TransactionId,
            cancellationToken);
        if (payment is null)
        {
            throw new WebhookProcessingException("The referenced payment does not exist.");
        }

        if (!string.Equals(payment.Provider, provider, StringComparison.OrdinalIgnoreCase))
        {
            throw new WebhookProcessingException("The webhook provider does not match the payment provider.");
        }

        var now = timeProvider.GetUtcNow();
        database.ProcessedWebhooks.Add(new ProcessedWebhook
        {
            Provider = provider,
            EventId = webhookEvent.EventId,
            PaymentId = payment.Id,
            ProcessedAt = now
        });

        if (payment.Status == PaymentStatus.Processing)
        {
            payment.Status = nextStatus;
            payment.UpdatedAt = now;
            payment.FailureCode = nextStatus == PaymentStatus.Failed ? "provider_webhook_failed" : null;
            database.AuditEntries.Add(new PaymentAuditEntry
            {
                PaymentId = payment.Id,
                Event = nextStatus == PaymentStatus.Succeeded ? "payment_succeeded" : "payment_failed",
                Actor = $"webhook:{provider}",
                CreatedAt = now
            });
        }
        else if (payment.Status != nextStatus)
        {
            throw new WebhookProcessingException("A terminal payment status cannot be changed by a webhook.");
        }

        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return false;
    }
}
