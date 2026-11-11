using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace PaymentOrchestration.Api;

public sealed class PaymentService(
    PaymentDbContext database,
    IPaymentProviderResolver providerResolver,
    IOptions<PaymentProcessingOptions> processingOptions,
    TimeProvider timeProvider)
{
    private readonly PaymentProcessingOptions _options = processingOptions.Value;

    public async Task<PaymentDetails> InitiateAsync(
        CreatePaymentRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        Validate(request, idempotencyKey);
        var provider = providerResolver.Resolve(request.Provider);
        if (provider is null)
        {
            throw new PaymentInputException($"Unsupported payment provider '{request.Provider}'.");
        }

        var normalized = request with
        {
            Currency = request.Currency.ToUpperInvariant(),
            Provider = provider.Name
        };
        var fingerprint = CreateFingerprint(normalized);

        var existing = await database.Payments
            .Include(payment => payment.Attempts)
            .SingleOrDefaultAsync(payment => payment.IdempotencyKey == idempotencyKey, cancellationToken);
        if (existing is not null)
        {
            return ReturnExistingOrConflict(existing, fingerprint);
        }

        var now = timeProvider.GetUtcNow();
        var payment = new PaymentTransaction
        {
            Id = Guid.NewGuid().ToString("N"),
            IdempotencyKey = idempotencyKey,
            RequestFingerprint = fingerprint,
            AmountMinor = normalized.AmountMinor,
            Currency = normalized.Currency,
            Provider = normalized.Provider,
            Status = PaymentStatus.Processing,
            CreatedAt = now,
            UpdatedAt = now
        };
        payment.AuditEntries.Add(new PaymentAuditEntry
        {
            PaymentId = payment.Id,
            Event = "payment_created",
            Actor = "api",
            CreatedAt = now
        });

        database.Payments.Add(payment);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            database.ChangeTracker.Clear();
            var racedPayment = await database.Payments
                .Include(item => item.Attempts)
                .SingleOrDefaultAsync(item => item.IdempotencyKey == idempotencyKey, cancellationToken);
            if (racedPayment is null)
            {
                throw;
            }

            return ReturnExistingOrConflict(racedPayment, fingerprint);
        }

        await ChargeAsync(payment, normalized.PaymentToken, provider, cancellationToken);
        return payment.ToDetails();
    }

    public async Task<PaymentDetails?> GetAsync(string id, CancellationToken cancellationToken)
    {
        var payment = await database.Payments
            .Include(item => item.Attempts)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        return payment?.ToDetails();
    }

    public async Task<IReadOnlyList<PaymentDetails>> ListAsync(int limit, CancellationToken cancellationToken)
    {
        var pageSize = Math.Clamp(limit, 1, 100);
        var payments = await database.Payments
            .FromSqlInterpolated(
                $"SELECT * FROM \"Payments\" ORDER BY \"CreatedAt\" COLLATE BINARY DESC, \"Id\" DESC LIMIT {pageSize}")
            .AsNoTracking()
            .ToArrayAsync(cancellationToken);

        var paymentIds = payments.Select(payment => payment.Id).ToArray();
        var attempts = await database.Attempts
            .AsNoTracking()
            .Where(attempt => paymentIds.Contains(attempt.PaymentId))
            .ToArrayAsync(cancellationToken);
        var attemptsByPayment = attempts.ToLookup(attempt => attempt.PaymentId);
        foreach (var payment in payments)
        {
            payment.Attempts = attemptsByPayment[payment.Id].ToList();
        }

        return payments.Select(payment => payment.ToDetails()).ToArray();
    }

    public async Task<IReadOnlyList<PaymentAuditEntry>> GetAuditAsync(
        string id,
        CancellationToken cancellationToken)
    {
        return await database.AuditEntries
            .Where(entry => entry.PaymentId == id)
            .OrderBy(entry => entry.Id)
            .ToArrayAsync(cancellationToken);
    }

    private async Task ChargeAsync(
        PaymentTransaction payment,
        string paymentToken,
        IPaymentProvider provider,
        CancellationToken cancellationToken)
    {
        var maxAttempts = Math.Clamp(_options.MaxAttempts, 1, 10);
        for (var number = 1; number <= maxAttempts; number++)
        {
            ProviderChargeResult result;
            try
            {
                result = await provider.ChargeAsync(
                    new ProviderChargeRequest(
                        payment.Id,
                        payment.AmountMinor,
                        payment.Currency,
                        paymentToken,
                        payment.IdempotencyKey),
                    cancellationToken);
            }
            catch (HttpRequestException)
            {
                result = new(false, true, null, "provider_unavailable");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                result = new(false, true, null, "provider_timeout");
            }

            var now = timeProvider.GetUtcNow();
            payment.AttemptCount = number;
            payment.ProviderReference = result.ProviderReference ?? payment.ProviderReference;
            payment.Attempts.Add(new PaymentAttempt
            {
                PaymentId = payment.Id,
                Number = number,
                Succeeded = result.Succeeded,
                FailureCode = result.FailureCode,
                ProviderReference = result.ProviderReference,
                CreatedAt = now
            });

            if (result.Pending)
            {
                payment.AuditEntries.Add(new PaymentAuditEntry
                {
                    PaymentId = payment.Id,
                    Event = "payment_pending",
                    Actor = $"provider:{provider.Name}",
                    CreatedAt = now
                });
            }
            else if (result.Succeeded)
            {
                payment.Status = PaymentStatus.Succeeded;
                payment.FailureCode = null;
                payment.AuditEntries.Add(new PaymentAuditEntry
                {
                    PaymentId = payment.Id,
                    Event = "payment_succeeded",
                    Actor = $"provider:{provider.Name}",
                    CreatedAt = now
                });
            }
            else if (!result.Retryable || number == maxAttempts)
            {
                payment.Status = PaymentStatus.Failed;
                payment.FailureCode = result.FailureCode ?? "provider_error";
                payment.AuditEntries.Add(new PaymentAuditEntry
                {
                    PaymentId = payment.Id,
                    Event = "payment_failed",
                    Actor = $"provider:{provider.Name}",
                    Detail = payment.FailureCode,
                    CreatedAt = now
                });
            }
            else
            {
                payment.AuditEntries.Add(new PaymentAuditEntry
                {
                    PaymentId = payment.Id,
                    Event = "payment_retry_scheduled",
                    Actor = "system",
                    Detail = result.FailureCode,
                    CreatedAt = now
                });
            }

            payment.UpdatedAt = now;
            await database.SaveChangesAsync(cancellationToken);

            if (payment.Status != PaymentStatus.Processing || result.Pending)
            {
                return;
            }

            var delay = Math.Max(0, _options.RetryDelayMilliseconds) * (1 << Math.Min(number - 1, 5));
            if (delay > 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(delay), timeProvider, cancellationToken);
            }
        }
    }

    private static PaymentDetails ReturnExistingOrConflict(
        PaymentTransaction payment,
        string fingerprint)
    {
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(payment.RequestFingerprint),
                Convert.FromHexString(fingerprint)))
        {
            throw new PaymentConflictException(
                "This idempotency key has already been used with a different payment request.");
        }

        return payment.ToDetails();
    }

    private static void Validate(CreatePaymentRequest request, string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 200)
        {
            throw new PaymentInputException("A non-empty Idempotency-Key header of at most 200 characters is required.");
        }

        if (request.AmountMinor <= 0 || request.AmountMinor > 1_000_000_000_000)
        {
            throw new PaymentInputException("AmountMinor must be between 1 and 1,000,000,000,000.");
        }

        if (string.IsNullOrWhiteSpace(request.Currency)
            || request.Currency.Length != 3
            || request.Currency.Any(character => !char.IsAsciiLetter(character)))
        {
            throw new PaymentInputException("Currency must be a three-letter ISO currency code.");
        }

        if (string.IsNullOrWhiteSpace(request.PaymentToken) || request.PaymentToken.Length > 512)
        {
            throw new PaymentInputException("A provider payment token of at most 512 characters is required.");
        }
    }

    private static string CreateFingerprint(CreatePaymentRequest request)
    {
        var canonical = $"{request.AmountMinor}\n{request.Currency}\n{request.Provider}\n{request.PaymentToken}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
