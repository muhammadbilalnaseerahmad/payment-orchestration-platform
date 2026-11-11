using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace PaymentOrchestration.Api;

public static class PaymentEndpoints
{
    public static IEndpointRouteBuilder MapPaymentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var payments = endpoints.MapGroup("/api/payments");
        payments.MapGet("/", ListAsync)
            .WithName("ListPayments")
            .WithTags("Payments")
            .WithSummary("List recent payments")
            .Produces<PaymentDetails[]>(StatusCodes.Status200OK);
        payments.MapPost("/", InitiateAsync)
            .WithName("InitiatePayment")
            .WithTags("Payments")
            .WithSummary("Initiate a tokenized payment")
            .WithDescription("Requires an Idempotency-Key header. Only provider-issued payment tokens are accepted; never send card data.")
            .Produces<PaymentDetails>(StatusCodes.Status201Created)
            .Produces<PaymentDetails>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status409Conflict);
        payments.MapGet("/{id}", GetAsync)
            .WithName("GetPayment")
            .WithTags("Payments")
            .WithSummary("Get payment status")
            .Produces<PaymentDetails>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);
        payments.MapGet("/{id}/audit", GetAuditAsync)
            .WithName("GetPaymentAudit")
            .WithTags("Payments")
            .WithSummary("Get payment audit history")
            .Produces<PaymentAuditEntry[]>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);
        endpoints.MapPost("/api/webhooks/{provider}", HandleWebhookAsync)
            .WithName("ReceivePaymentWebhook")
            .WithTags("Webhooks")
            .WithSummary("Receive a signed payment status event")
            .WithDescription("Validates X-Webhook-Signature over the exact raw JSON body. Duplicate events are acknowledged without reapplying them.")
            .Accepts<PaymentWebhookRequest>("application/json")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status413PayloadTooLarge);
        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        PaymentService paymentService,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        return Results.Ok(await paymentService.ListAsync(limit, cancellationToken));
    }

    private static async Task<IResult> InitiateAsync(
        CreatePaymentRequest request,
        [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
        PaymentService paymentService,
        CancellationToken cancellationToken)
    {
        try
        {
            var payment = await paymentService.InitiateAsync(
                request,
                idempotencyKey,
                cancellationToken);
            return payment.Status == PaymentStatus.Processing
                ? Results.Accepted($"/api/payments/{payment.Id}", payment)
                : Results.Created($"/api/payments/{payment.Id}", payment);
        }
        catch (PaymentInputException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
        catch (PaymentConflictException exception)
        {
            return Results.Conflict(new { error = exception.Message });
        }
    }

    private static async Task<IResult> GetAsync(
        string id,
        PaymentService paymentService,
        CancellationToken cancellationToken)
    {
        var payment = await paymentService.GetAsync(id, cancellationToken);
        return payment is null ? Results.NotFound() : Results.Ok(payment);
    }

    private static async Task<IResult> GetAuditAsync(
        string id,
        PaymentService paymentService,
        CancellationToken cancellationToken)
    {
        if (await paymentService.GetAsync(id, cancellationToken) is null)
        {
            return Results.NotFound();
        }

        return Results.Ok(await paymentService.GetAuditAsync(id, cancellationToken));
    }

    private static async Task<IResult> HandleWebhookAsync(
        string provider,
        HttpRequest request,
        [FromHeader(Name = "X-Webhook-Signature")] string signature,
        WebhookService webhookService,
        CancellationToken cancellationToken)
    {
        const int maxBodyBytes = 64 * 1024;
        if (request.ContentLength > maxBodyBytes)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        await using var body = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (body.Length + read > maxBodyBytes)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            await body.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        try
        {
            var duplicate = await webhookService.HandleAsync(
                provider,
                body.ToArray(),
                signature,
                cancellationToken);
            return Results.Ok(new { received = true, duplicate });
        }
        catch (InvalidWebhookSignatureException)
        {
            return Results.Unauthorized();
        }
        catch (WebhookProcessingException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
    }
}
