# Northstar Pay - Payment Processing & Transaction Orchestration

Northstar Pay is a full-stack payment operations demo: a .NET 10 API, React/TypeScript dashboard, and SQLite-backed transaction store. It demonstrates payment initiation, provider abstraction, retry handling, signed webhooks, idempotency, transaction status, and audit history.

> **Sandbox only.** The included simulator and configurable HTTP adapters are for development and provider sandbox testing, not live payment processing. The app accepts provider-issued payment tokens only; it does not collect or store card numbers or CVV. Authentication and tenant isolation are not implemented, so do not expose this service publicly as a running application.

## Features

- **Payment lifecycle:** initiate a payment, record provider attempts, and track `Processing`, `Succeeded`, or `Failed` status.
- **Provider abstraction:** `IPaymentProvider` and a provider registry support a local simulator and multiple named HTTP sandbox gateways.
- **Idempotency:** a unique database constraint prevents duplicate payments for a key. Identical retries return the existing payment; a different payload with the same key returns `409 Conflict`.
- **Provider retry handling:** bounded exponential retries for retryable failures, with per-attempt records and retry audit events. Pending provider results wait for a webhook rather than repeating the charge.
- **Signed webhooks:** validate HMAC-SHA256 signatures and timestamp tolerance, reject malformed or mismatched events, and deduplicate provider event IDs.
- **Persistence and audit:** SQLite stores payment status, provider references, attempts, audit entries, and processed webhook IDs.
- **React operations dashboard:** recent transactions, summary metrics by status and currency, search and status filters, CSV export, attempt/audit details, and sandbox payment initiation.
- **API documentation:** Swagger UI and OpenAPI specification in the Development environment.

## Architecture

```text
React + TypeScript dashboard
    |  /api (Vite development proxy)
    v
ASP.NET Core minimal API
    |-- PaymentService: validation, idempotency, lifecycle, retries
    |-- IPaymentProvider: simulator or configured HTTP sandbox adapters
    |-- WebhookService: signature validation and event deduplication
    v
EF Core + SQLite: transactions, attempts, audit entries, webhook receipts
```

### Technology

| Area | Technology |
| --- | --- |
| API | .NET 10, ASP.NET Core minimal APIs |
| Persistence | EF Core 10, SQLite |
| API docs | Swashbuckle / Swagger |
| UI | React, TypeScript, Vite, Lucide |
| Tests | xUnit, SQLite in-memory |

## Run locally

Use .NET SDK `10.0.403` (selected by `global.json`) and Node.js 20.19+ or 22.12+.

Start the API from the repository root:

```powershell
dotnet run --project src\PaymentOrchestration.Api
```

The API listens at `http://localhost:5208` by default. In a second terminal:

```powershell
cd ui
npm install
npm run dev
```

Open the Vite URL, normally `http://localhost:5173`. The development server proxies `/api` to the backend at `http://127.0.0.1:5208`. Set `VITE_API_PROXY_TARGET` if the API uses a different address. Swagger UI is at `http://localhost:5208/swagger`; the OpenAPI document is at `/swagger/v1/swagger.json`.

The API creates `payment-orchestration.db` in its working directory on first run. The database, build outputs, and local secret files are ignored by Git.

## Try the sandbox

In the dashboard, choose **Create payment**. Enter an amount, select a configured provider, and provide an opaque payment token. The simulator offers sample tokens:

| Token | Result |
| --- | --- |
| Any other non-empty token | Succeeds |
| `sandbox_retry` | Fails once transiently, then succeeds |
| `sandbox_decline` | Terminal decline |
| `sandbox_failure` | Fails after retry attempts are exhausted |

These are simulator-only values. For an external gateway, use its sandbox-issued token from that provider's hosted checkout or client SDK. Never enter card numbers or CVV.

## Configure sandbox gateways

Credentials belong in .NET user-secrets or environment variables, never in checked-in settings:

```powershell
dotnet user-secrets init --project src\PaymentOrchestration.Api
dotnet user-secrets set "PaymentGateway:Providers:providerA:BaseUrl" "https://sandbox-a.example.test/v1" --project src\PaymentOrchestration.Api
dotnet user-secrets set "PaymentGateway:Providers:providerA:ApiKey" "<provider A sandbox API key>" --project src\PaymentOrchestration.Api
```

Add more entries under `PaymentGateway:Providers:{providerName}` for additional gateways. Configured provider names appear in `GET /api/providers` and in the dashboard's provider selector. The HTTP adapter sends `POST {BaseUrl}/payments` with `transactionId`, `amountMinor`, `currency`, and `paymentToken`, plus the payment's `Idempotency-Key` header and optional bearer API key. It expects JSON containing `id` and `status` (`succeeded`, `pending`, or `failed`); failure responses may include `errorCode` and `retryable`. HTTP 429 and 5xx responses are retried.

The legacy `PaymentGateway:SandboxBaseUrl` and `PaymentGateway:SandboxApiKey` settings configure the HTTP adapter named `sandbox` instead of the local simulator.

## API reference

| Method | Route | Description |
| --- | --- | --- |
| `GET` | `/health` | Health check |
| `GET` | `/api/providers` | List configured provider names |
| `GET` | `/api/payments?limit=50` | List recent payments; limit capped at 100 |
| `POST` | `/api/payments` | Initiate a payment; requires `Idempotency-Key` |
| `GET` | `/api/payments/{id}` | Retrieve status and provider attempts |
| `GET` | `/api/payments/{id}/audit` | Retrieve audit history |
| `POST` | `/api/webhooks/{provider}` | Process a signed provider event |

Example payment request:

```http
POST /api/payments
Idempotency-Key: order-123-payment-1
Content-Type: application/json

{
  "amountMinor": 2599,
  "currency": "USD",
  "provider": "sandbox",
  "paymentToken": "sandbox_retry"
}
```

`amountMinor` is an integer in the currency's minor unit. The idempotency key is unique across this service. A SHA-256 request fingerprint (including the token) is retained to detect conflicting reuse, but the raw token is only sent to the provider and is not persisted or returned.

### Webhook signatures

Set a signing secret of at least 32 characters:

```powershell
dotnet user-secrets set "PaymentWebhook:SigningSecret" "<random secret of at least 32 characters>" --project src\PaymentOrchestration.Api
```

The service verifies HMAC-SHA256 over `{unix_timestamp}.{raw_request_body}` from `X-Webhook-Signature: t={unix_timestamp},v1={hex_digest}`. Timestamps outside `PaymentWebhook:ToleranceSeconds` are rejected. The raw body is limited to 64 KiB. Example body:

```json
{
  "eventId": "evt_123",
  "transactionId": "payment-id",
  "status": "succeeded"
}
```

Only terminal webhook statuses (`succeeded` or `failed`) are accepted. Duplicate event IDs are acknowledged without applying the transition again, and the URL provider must match the payment's provider.

## Build and test

```powershell
dotnet test PaymentOrchestration.slnx --configuration Release
dotnet format PaymentOrchestration.slnx --verify-no-changes --no-restore
cd ui
npm run build
npm run lint
```

The API tests cover idempotency, retry behavior, pending payments, webhook signature and deduplication, provider resolution, and recent-payment listing. The provider adapter also has an HTTP request contract test.

## Project layout

```text
src/PaymentOrchestration.Api/       ASP.NET Core API, providers, persistence, webhooks
tests/PaymentOrchestration.Tests/   xUnit service and provider tests
ui/                                 React payment operations dashboard
```

## Resume-ready description

**Title:** Payment Processing & Transaction Orchestration Platform

Built a full-stack payment orchestration demo using .NET 10, ASP.NET Core, EF Core/SQLite, and React/TypeScript. Designed provider abstractions and sandbox integrations with idempotent payment initiation, bounded retries, signed and deduplicated webhooks, persistent transaction audit history, and an interactive operations dashboard. Added focused automated tests for payment lifecycle, provider behavior, and webhook validation.

## Production considerations

This project is a sandbox/demo foundation. Before processing real payments or exposing a deployment, add authentication and authorization, merchant/tenant isolation, production database migrations and backups, provider-specific signature/response verification, secret management, observability, and operational controls. Do not send or store primary account numbers or CVV.
