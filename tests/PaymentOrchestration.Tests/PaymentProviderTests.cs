using System.Net;
using System.Text;
using System.Text.Json;
using PaymentOrchestration.Api;

namespace PaymentOrchestration.Tests;

public sealed class PaymentProviderTests
{
    [Fact]
    public async Task HttpSandboxProviderSendsTokenizedIdempotentRequest()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://gateway.example.test/v1/payments", request.RequestUri!.ToString());
            Assert.Equal("Bearer sandbox-api-key", request.Headers.Authorization!.ToString());
            Assert.Equal("idem-123", request.Headers.GetValues("Idempotency-Key").Single());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"id":"provider-payment-1","status":"succeeded"}""",
                    Encoding.UTF8,
                    "application/json")
            });
        });
        using var client = new HttpClient(handler);
        var provider = new HttpSandboxPaymentProvider(
            client,
            "sandbox-east",
            "https://gateway.example.test/v1",
            "sandbox-api-key");

        var result = await provider.ChargeAsync(
            new ProviderChargeRequest("payment-1", 2500, "USD", "tok_provider_123", "idem-123"),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("provider-payment-1", result.ProviderReference);
        using var content = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("payment-1", content.RootElement.GetProperty("transactionId").GetString());
        Assert.Equal(2500, content.RootElement.GetProperty("amountMinor").GetInt64());
        Assert.Equal("USD", content.RootElement.GetProperty("currency").GetString());
        Assert.Equal("tok_provider_123", content.RootElement.GetProperty("paymentToken").GetString());
        Assert.False(content.RootElement.TryGetProperty("cardNumber", out _));
        Assert.False(content.RootElement.TryGetProperty("cvv", out _));
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory)
        : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return await responseFactory(request, cancellationToken);
        }
    }
}
