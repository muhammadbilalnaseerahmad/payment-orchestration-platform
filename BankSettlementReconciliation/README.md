# Bank Settlement & Reconciliation Service

Full-stack financial operations application with a .NET 10 API, React/TypeScript dashboard, SQLite persistence, and RabbitMQ-backed settlement reconciliation. The original payment sandbox remains available alongside the reconciliation workflows.

> **Development/demo only.** Authentication and tenant isolation are not implemented. Do not expose this service publicly or use it for production financial operations. The payment sandbox accepts provider-issued tokens only; it does not collect or store card numbers or CVV.

## Features

- **Settlement imports:** validate and persist CSV files with row-level field checks, duplicate-reference detection, and upload size/record limits.
- **Internal transaction ingestion:** add internal transaction records with unique, case-insensitive references.
- **Matching and exceptions:** match on reference, amount, and currency; classify matched, duplicate, mismatched, missing-internal, and missing-settlement records.
- **RabbitMQ processing:** durable queue messages, acknowledgements, single-message prefetch, retry-on-worker interruption, and recovery of unfinished batches.
- **Reports and audit:** batch summaries, detailed row findings, retryable failed batches, and timestamped audit entries for ingestion and reconciliation outcomes.
- **React reconciliation workspace:** upload bank files, add internal transactions, monitor background batch status, and inspect findings and audit history.
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
React + TypeScript operations dashboard
    |  /api (Vite or Nginx proxy)
    v
ASP.NET Core minimal API
    |-- ReconciliationService: CSV import, matching, findings, reports, audit
    |-- RabbitMqReconciliationQueue + hosted worker: durable background jobs
    |-- PaymentService / WebhookService: existing payment sandbox
    v
EF Core + SQLite: reconciliation, payments, attempts, webhooks, audit entries
```

### Technology

| Area | Technology |
| --- | --- |
| API | .NET 10, ASP.NET Core minimal APIs |
| Persistence | EF Core 10, SQLite |
| API docs | Swashbuckle / Swagger |
| UI | React, TypeScript, Vite, Lucide |
| Background jobs | RabbitMQ.Client, RabbitMQ |
| Local orchestration | Docker Compose |
| Tests | xUnit, SQLite in-memory |

## Run with Docker Compose (recommended)

Copy the development environment template and start the API, UI, and RabbitMQ:

```powershell
Copy-Item .env.example .env
docker compose up --build
```

Open the dashboard at `http://localhost:5173`, API/Swagger at `http://localhost:5208/swagger`, and RabbitMQ management at `http://localhost:15672`. Local demo credentials are read from `.env`; replace them before any non-local use. SQLite and RabbitMQ data are stored in named Docker volumes.

## Run the API and UI directly

Use .NET SDK `10.0.403` (selected by `global.json`) and Node.js 20.19+ or 22.12+. Start RabbitMQ first (for example with the Compose `rabbitmq` service), then configure the API credentials without putting them in source:

```powershell
dotnet user-secrets init --project src\PaymentOrchestration.Api
dotnet user-secrets set "RabbitMq:Password" "<local RabbitMQ password>" --project src\PaymentOrchestration.Api
dotnet run --project src\PaymentOrchestration.Api
```

The API listens at `http://localhost:5208` by default. In a second terminal:

```powershell
cd ui
npm install
npm run dev
```

Open the Vite URL, normally `http://localhost:5173`. The development server proxies `/api` to the backend at `http://127.0.0.1:5208`. Set `VITE_API_PROXY_TARGET` if the API uses a different address. Swagger UI is at `http://localhost:5208/swagger`; the OpenAPI document is at `/swagger/v1/swagger.json`.

The API initializes its SQLite schema on startup and creates `payment-orchestration.db` in its working directory. The database, build outputs, and local secret files are ignored by Git. Configure RabbitMQ host/user/port using `RabbitMq:*` user-secrets or environment variables when not using Compose.

## Reconcile a settlement file

The CSV requires a header and at least one row. Column order is flexible; header names are case-insensitive.

```csv
transactionReference,amount,currency,settledAt
PAY-1001,125.50,USD,2026-11-12T10:30:00Z
PAY-1002,42.00,EUR,2026-11-12T10:31:00Z
```

Use the dashboard's **Add internal transaction** form or `POST /api/reconciliation/transactions` to ingest internal records, then upload the CSV. Amounts in CSV files are decimal major-unit amounts with at most two fractional digits and are converted to integer minor units. References are matched case-insensitively; amount and currency must also match.

For each file, later duplicate bank references are reported as `Duplicate`; unmatched bank records as `MissingInternal`; amount or currency differences as `Mismatched`; and internal transactions within the file's UTC settlement-date range that do not appear in the bank file as `MissingSettlement`. Completed reports include all findings and an audit history. Failed batches can be requeued from the UI or API.

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
| `GET` | `/api/reconciliation/transactions?limit=100` | List recent internal transactions |
| `POST` | `/api/reconciliation/transactions` | Ingest an internal transaction |
| `POST` | `/api/reconciliation/batches` | Import a multipart CSV settlement file and enqueue processing |
| `GET` | `/api/reconciliation/batches?limit=50` | List recent batches and summaries |
| `GET` | `/api/reconciliation/batches/{id}` | Retrieve a batch's findings and audit history |
| `GET` | `/api/reconciliation/reports/{id}` | Retrieve the generated reconciliation report |
| `POST` | `/api/reconciliation/batches/{id}/requeue` | Requeue a pending or failed batch |
| `GET` | `/api/reconciliation/audit?limit=100` | List recent reconciliation audit events |

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
dotnet test BankSettlementReconciliation.slnx --configuration Release
dotnet format BankSettlementReconciliation.slnx --verify-no-changes --no-restore
cd ui
npm run build
npm run lint
```

The API tests cover payment lifecycle and webhook behavior, along with reconciliation matching, duplicate and mismatch classification, missing records, CSV validation, and case-insensitive transaction uniqueness. The provider adapter also has an HTTP request contract test.

## Project layout

```text
src/PaymentOrchestration.Api/       ASP.NET Core API, reconciliation, payments, persistence
tests/PaymentOrchestration.Tests/   xUnit service, provider, and reconciliation tests
ui/                                 React payment and reconciliation dashboards
Dockerfile / docker-compose.yml     API, UI, SQLite volume, and RabbitMQ local stack
```

## Resume-ready description

**Title:** Bank Settlement & Reconciliation Service

Built a full-stack reconciliation service with .NET 10, ASP.NET Core, EF Core/SQLite, RabbitMQ, and React/TypeScript. Implemented validated bank CSV imports, internal transaction ingestion, asynchronous matching, and reports for duplicates, missing records, and amount/currency mismatches. Added durable queue processing, retryable batches, audit trails, Docker Compose development setup, and focused automated tests.

## Production considerations

This project is a sandbox/demo foundation. Before processing real payments or exposing a deployment, add authentication and authorization, merchant/tenant isolation, production database migrations and backups, provider-specific signature/response verification, secret management, observability, and operational controls. Do not send or store primary account numbers or CVV.
