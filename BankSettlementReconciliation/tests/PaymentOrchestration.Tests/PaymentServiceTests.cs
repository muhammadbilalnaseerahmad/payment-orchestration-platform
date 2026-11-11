using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaymentOrchestration.Api;

namespace PaymentOrchestration.Tests;

public sealed class PaymentServiceTests
{
    [Fact]
    public async Task RepeatedIdempotencyKeyReturnsExistingPaymentAndRejectsChangedPayload()
    {
        await using var testDatabase = await TestDatabase.CreateAsync();
        var provider = new TestProvider(new ProviderChargeResult(true, false, "provider-123", null));
        var service = CreatePaymentService(testDatabase.Context, provider);
        var request = new CreatePaymentRequest(1500, "usd", "sandbox", "tok_test_123");

        var original = await service.InitiateAsync(request, "order-1", CancellationToken.None);
        var repeated = await service.InitiateAsync(request, "order-1", CancellationToken.None);

        Assert.Equal(original.Id, repeated.Id);
        Assert.Equal(1, provider.CallCount);
        await Assert.ThrowsAsync<PaymentConflictException>(() =>
            service.InitiateAsync(request with { AmountMinor = 1501 }, "order-1", CancellationToken.None));
    }

    [Fact]
    public async Task ListPaymentsReturnsNewestFirstAndCapsRequestedPageSize()
    {
        await using var testDatabase = await TestDatabase.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        testDatabase.Context.Payments.AddRange(
            Enumerable.Range(1, 101).Select(index => new PaymentTransaction
            {
                Id = $"payment-{index}",
                IdempotencyKey = $"order-{index}",
                RequestFingerprint = new string('A', 64),
                AmountMinor = index * 100,
                Currency = "USD",
                Provider = "sandbox",
                Status = PaymentStatus.Succeeded,
                CreatedAt = now.AddMinutes(index),
                UpdatedAt = now.AddMinutes(index)
            }));
        await testDatabase.Context.SaveChangesAsync();
        var service = CreatePaymentService(
            testDatabase.Context,
            new TestProvider(new ProviderChargeResult(true, false, "provider-123", null)));

        var payments = await service.ListAsync(1000, CancellationToken.None);

        Assert.Equal(100, payments.Count);
        Assert.Equal("payment-101", payments[0].Id);
        Assert.Equal("payment-2", payments[^1].Id);
    }

    [Fact]
    public async Task RetryableProviderFailureIsRetriedAndRecorded()
    {
        await using var testDatabase = await TestDatabase.CreateAsync();
        var provider = new TestProvider(
            new ProviderChargeResult(false, true, null, "temporary_error"),
            new ProviderChargeResult(true, false, "provider-456", null));
        var service = CreatePaymentService(testDatabase.Context, provider);

        var payment = await service.InitiateAsync(
            new CreatePaymentRequest(2200, "EUR", "sandbox", "tok_test_456"),
            "order-2",
            CancellationToken.None);

        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.Equal(2, payment.AttemptCount);
        Assert.Equal(2, provider.CallCount);
        Assert.Equal(2, await testDatabase.Context.Attempts.CountAsync());
        Assert.Contains(
            await testDatabase.Context.AuditEntries.ToArrayAsync(),
            entry => entry.Event == "payment_retry_scheduled");
    }

    [Fact]
    public async Task PendingProviderResultWaitsForWebhookInsteadOfRetryingCharge()
    {
        await using var testDatabase = await TestDatabase.CreateAsync();
        var provider = new TestProvider(new ProviderChargeResult(false, false, "provider-pending", null, true));
        var service = CreatePaymentService(testDatabase.Context, provider);

        var payment = await service.InitiateAsync(
            new CreatePaymentRequest(1700, "USD", "sandbox", "tok_pending"),
            "order-pending",
            CancellationToken.None);

        Assert.Equal(PaymentStatus.Processing, payment.Status);
        Assert.Equal("provider-pending", payment.ProviderReference);
        Assert.Equal(1, payment.AttemptCount);
        Assert.Equal(1, provider.CallCount);
        Assert.Contains(
            await testDatabase.Context.AuditEntries.ToArrayAsync(),
            entry => entry.Event == "payment_pending");
    }

    [Fact]
    public async Task SignedWebhookIsAppliedOnceAndAudited()
    {
        await using var testDatabase = await TestDatabase.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        var payment = new PaymentTransaction
        {
            Id = "payment-1",
            IdempotencyKey = "order-3",
            RequestFingerprint = new string('A', 64),
            AmountMinor = 300,
            Currency = "USD",
            Provider = "sandbox",
            Status = PaymentStatus.Processing,
            CreatedAt = now,
            UpdatedAt = now
        };
        testDatabase.Context.Payments.Add(payment);
        await testDatabase.Context.SaveChangesAsync();

        const string secret = "test-webhook-signing-secret-that-is-long-enough";
        var validator = new WebhookSignatureValidator(
            Options.Create(new PaymentWebhookOptions { SigningSecret = secret }),
            TimeProvider.System);
        var service = new WebhookService(testDatabase.Context, validator, TimeProvider.System);
        var body = Encoding.UTF8.GetBytes(
            """{"eventId":"evt-1","transactionId":"payment-1","status":"succeeded"}""");
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signature = Sign(secret, timestamp, body);

        var duplicate = await service.HandleAsync("sandbox", body, signature, CancellationToken.None);
        var repeated = await service.HandleAsync("sandbox", body, signature, CancellationToken.None);

        Assert.False(duplicate);
        Assert.True(repeated);
        Assert.Equal(PaymentStatus.Succeeded, (await testDatabase.Context.Payments.SingleAsync()).Status);
        Assert.Single(await testDatabase.Context.ProcessedWebhooks.ToArrayAsync());
        Assert.Contains(
            await testDatabase.Context.AuditEntries.ToArrayAsync(),
            entry => entry.Event == "payment_succeeded" && entry.Actor == "webhook:sandbox");
    }

    [Fact]
    public async Task WebhookRejectsInvalidSignature()
    {
        await using var testDatabase = await TestDatabase.CreateAsync();
        var validator = new WebhookSignatureValidator(
            Options.Create(new PaymentWebhookOptions { SigningSecret = "a-secret-long-enough-for-this-test-case" }),
            TimeProvider.System);
        var service = new WebhookService(testDatabase.Context, validator, TimeProvider.System);

        await Assert.ThrowsAsync<InvalidWebhookSignatureException>(() =>
            service.HandleAsync("sandbox", Encoding.UTF8.GetBytes("{}"), "t=1,v1=00", CancellationToken.None));
    }

    [Fact]
    public async Task WebhookRejectsNumericStatusValues()
    {
        await using var testDatabase = await TestDatabase.CreateAsync();
        const string secret = "test-webhook-signing-secret-that-is-long-enough";
        var validator = new WebhookSignatureValidator(
            Options.Create(new PaymentWebhookOptions { SigningSecret = secret }),
            TimeProvider.System);
        var service = new WebhookService(testDatabase.Context, validator, TimeProvider.System);
        var body = Encoding.UTF8.GetBytes(
            """{"eventId":"evt-2","transactionId":"payment-1","status":"1"}""");
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        await Assert.ThrowsAsync<WebhookProcessingException>(() =>
            service.HandleAsync("sandbox", body, Sign(secret, timestamp, body), CancellationToken.None));
    }

    [Fact]
    public void ProviderRegistryResolvesMultipleConfiguredSandboxGateways()
    {
        var registry = new PaymentProviderRegistry(
            [new SandboxSimulationProvider()],
            new TestHttpClientFactory(),
            Options.Create(new PaymentGatewayOptions
            {
                Providers = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["sandbox-east"] = new() { BaseUrl = "https://east.example.test", ApiKey = "east-key" },
                    ["sandbox-west"] = new() { BaseUrl = "https://west.example.test", ApiKey = "west-key" }
                }
            }));

        Assert.Equal("sandbox", registry.Resolve("sandbox")!.Name);
        Assert.Equal("sandbox-east", registry.Resolve("SANDBOX-EAST")!.Name);
        Assert.Equal("sandbox-west", registry.Resolve("sandbox-west")!.Name);
        Assert.Null(registry.Resolve("unknown"));
        Assert.Equal(["sandbox", "sandbox-east", "sandbox-west"], registry.GetNames());
    }

    private static PaymentService CreatePaymentService(PaymentDbContext context, IPaymentProvider provider) =>
        new(
            context,
            new TestProviderResolver(provider),
            Options.Create(new PaymentProcessingOptions
            {
                MaxAttempts = 3,
                RetryDelayMilliseconds = 0
            }),
            TimeProvider.System);

    private sealed class TestProviderResolver(IPaymentProvider provider) : IPaymentProviderResolver
    {
        public IPaymentProvider? Resolve(string name) =>
            string.Equals(provider.Name, name, StringComparison.OrdinalIgnoreCase) ? provider : null;

        public IReadOnlyList<string> GetNames() => [provider.Name];
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static string Sign(string secret, long timestamp, byte[] body)
    {
        var prefix = Encoding.UTF8.GetBytes($"{timestamp}.");
        var payload = prefix.Concat(body).ToArray();
        return $"t={timestamp},v1={Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payload))}";
    }

    private sealed class TestProvider(params ProviderChargeResult[] results) : IPaymentProvider
    {
        private int _calls;

        public string Name => "sandbox";
        public int CallCount => _calls;

        public Task<ProviderChargeResult> ChargeAsync(
            ProviderChargeRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var call = Interlocked.Increment(ref _calls);
            return Task.FromResult(results[Math.Min(call - 1, results.Length - 1)]);
        }
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
