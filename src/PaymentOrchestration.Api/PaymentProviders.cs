using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace PaymentOrchestration.Api;

public sealed record ProviderChargeRequest(
    string PaymentId,
    long AmountMinor,
    string Currency,
    string PaymentToken,
    string IdempotencyKey);

public sealed record ProviderChargeResult(
    bool Succeeded,
    bool Retryable,
    string? ProviderReference,
    string? FailureCode,
    bool Pending = false);

public interface IPaymentProvider
{
    string Name { get; }
    Task<ProviderChargeResult> ChargeAsync(ProviderChargeRequest request, CancellationToken cancellationToken);
}

public interface IPaymentProviderResolver
{
    IPaymentProvider? Resolve(string name);
    IReadOnlyList<string> GetNames();
}

public sealed class PaymentProviderRegistry(
    IEnumerable<IPaymentProvider> providers,
    IHttpClientFactory httpClientFactory,
    IOptions<PaymentGatewayOptions> options) : IPaymentProviderResolver
{
    private readonly Dictionary<string, IPaymentProvider> _providers =
        providers.ToDictionary(provider => provider.Name, StringComparer.OrdinalIgnoreCase);
    private readonly PaymentGatewayOptions _options = options.Value;

    public IPaymentProvider? Resolve(string name)
    {
        var configuredProvider = _options.Providers.FirstOrDefault(
            item => string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase));
        if (configuredProvider.Value is not null
            && !string.IsNullOrWhiteSpace(configuredProvider.Value.BaseUrl))
        {
            return new HttpSandboxPaymentProvider(
                httpClientFactory.CreateClient($"payment-gateway:{configuredProvider.Key}"),
                configuredProvider.Key,
                configuredProvider.Value.BaseUrl,
                configuredProvider.Value.ApiKey);
        }

        if (string.Equals(name, "sandbox", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(_options.SandboxBaseUrl))
        {
            return new HttpSandboxPaymentProvider(
                httpClientFactory.CreateClient("payment-gateway:sandbox"),
                "sandbox",
                _options.SandboxBaseUrl,
                _options.SandboxApiKey);
        }

        return _providers.GetValueOrDefault(name);
    }

    public IReadOnlyList<string> GetNames()
    {
        var names = _providers.Keys
            .Concat(_options.Providers
                .Where(item => !string.IsNullOrWhiteSpace(item.Value.BaseUrl))
                .Select(item => item.Key));
        if (!string.IsNullOrWhiteSpace(_options.SandboxBaseUrl))
        {
            names = names.Append("sandbox");
        }

        return names
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

public sealed class SandboxSimulationProvider : IPaymentProvider
{
    private readonly Dictionary<string, int> _transientCalls = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public string Name => "sandbox";

    public Task<ProviderChargeResult> ChargeAsync(
        ProviderChargeRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ProviderChargeResult result;
        if (request.PaymentToken == "sandbox_decline")
        {
            result = new(false, false, null, "card_declined");
        }
        else if (request.PaymentToken == "sandbox_retry")
        {
            lock (_lock)
            {
                _transientCalls.TryGetValue(request.PaymentId, out var calls);
                _transientCalls[request.PaymentId] = calls + 1;
                result = calls == 0
                    ? new(false, true, null, "temporary_provider_error")
                    : new(true, false, $"sim_{request.PaymentId}", null);
            }
        }
        else if (request.PaymentToken == "sandbox_failure")
        {
            result = new(false, true, null, "temporary_provider_error");
        }
        else
        {
            result = new(true, false, $"sim_{request.PaymentId}", null);
        }

        return Task.FromResult(result);
    }
}

public sealed class HttpSandboxPaymentProvider(
    HttpClient httpClient,
    string name,
    string baseUrl,
    string? apiKey) : IPaymentProvider
{
    public string Name { get; } = name;

    public async Task<ProviderChargeResult> ChargeAsync(
        ProviderChargeRequest request,
        CancellationToken cancellationToken)
    {
        var endpoint = $"{baseUrl.TrimEnd('/')}/payments";
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new
            {
                transactionId = request.PaymentId,
                amountMinor = request.AmountMinor,
                currency = request.Currency,
                paymentToken = request.PaymentToken
            })
        };
        message.Headers.TryAddWithoutValidation("Idempotency-Key", request.IdempotencyKey);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            message.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        }

        using var response = await httpClient.SendAsync(message, cancellationToken);
        if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
        {
            return new(false, true, null, "provider_unavailable");
        }

        if (!response.IsSuccessStatusCode)
        {
            return new(false, false, null, $"provider_http_{(int)response.StatusCode}");
        }

        try
        {
            var result = await response.Content.ReadFromJsonAsync<SandboxChargeResponse>(
                cancellationToken: cancellationToken);
            if (result is null || string.IsNullOrWhiteSpace(result.Status))
            {
                return new(false, true, null, "invalid_provider_response");
            }

            return result.Status.ToLowerInvariant() switch
            {
                "succeeded" => new(true, false, result.Id, null),
                "pending" => new(false, false, result.Id, null, true),
                "failed" => new(false, result.Retryable, result.Id, result.ErrorCode ?? "payment_failed"),
                _ => new(false, true, null, "invalid_provider_response")
            };
        }
        catch (JsonException)
        {
            return new(false, true, null, "invalid_provider_response");
        }
    }

    private sealed record SandboxChargeResponse(string? Id, string? Status, string? ErrorCode, bool Retryable);
}
