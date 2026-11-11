using System.Text;
using Microsoft.AspNetCore.Mvc;

namespace PaymentOrchestration.Api;

public static class ReconciliationEndpoints
{
    private const long MaxUploadBytes = 5 * 1024 * 1024;

    public static IEndpointRouteBuilder MapReconciliationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var reconciliation = endpoints.MapGroup("/api/reconciliation");
        reconciliation.MapGet("/transactions", ListTransactionsAsync)
            .WithName("ListInternalTransactions")
            .WithTags("Reconciliation")
            .WithSummary("List recent internal transactions")
            .Produces<IReadOnlyList<InternalTransactionView>>(StatusCodes.Status200OK);
        reconciliation.MapPost("/transactions", CreateTransactionAsync)
            .WithName("CreateInternalTransaction")
            .WithTags("Reconciliation")
            .WithSummary("Ingest an internal transaction for reconciliation")
            .Produces<InternalTransactionView>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status409Conflict);
        reconciliation.MapPost("/batches", ImportSettlementAsync)
            .WithName("ImportSettlementFile")
            .WithTags("Reconciliation")
            .WithSummary("Import a CSV bank settlement file")
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<ReconciliationBatchView>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status503ServiceUnavailable)
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(MaxUploadBytes + 64 * 1024));
        reconciliation.MapGet("/batches", ListBatchesAsync)
            .WithName("ListReconciliationBatches")
            .WithTags("Reconciliation")
            .WithSummary("List recent settlement reconciliation batches")
            .Produces<IReadOnlyList<ReconciliationBatchView>>(StatusCodes.Status200OK);
        reconciliation.MapGet("/batches/{id}", GetBatchAsync)
            .WithName("GetReconciliationBatch")
            .WithTags("Reconciliation")
            .WithSummary("Get batch findings and audit history")
            .Produces<ReconciliationBatchDetails>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);
        reconciliation.MapGet("/reports/{id}", GetBatchAsync)
            .WithName("GetReconciliationReport")
            .WithTags("Reconciliation")
            .WithSummary("Get the generated reconciliation report for a batch")
            .Produces<ReconciliationBatchDetails>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);
        reconciliation.MapPost("/batches/{id}/requeue", RequeueBatchAsync)
            .WithName("RequeueReconciliationBatch")
            .WithTags("Reconciliation")
            .WithSummary("Retry a pending or failed reconciliation batch")
            .Produces<ReconciliationBatchView>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status503ServiceUnavailable);
        reconciliation.MapGet("/audit", ListAuditAsync)
            .WithName("ListReconciliationAudit")
            .WithTags("Reconciliation")
            .WithSummary("List reconciliation audit entries")
            .Produces<IReadOnlyList<ReconciliationAuditView>>(StatusCodes.Status200OK);
        return endpoints;
    }

    private static async Task<IResult> ListTransactionsAsync(
        [FromQuery] int limit,
        ReconciliationService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.ListInternalTransactionsAsync(limit == 0 ? 50 : limit, cancellationToken));

    private static async Task<IResult> CreateTransactionAsync(
        CreateInternalTransactionRequest request,
        ReconciliationService service,
        CancellationToken cancellationToken)
    {
        try
        {
            var transaction = await service.CreateInternalTransactionAsync(request, cancellationToken);
            return Results.Created($"/api/reconciliation/transactions/{transaction.Id}", transaction);
        }
        catch (ReconciliationInputException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
        catch (ReconciliationConflictException exception)
        {
            return Results.Conflict(new { error = exception.Message });
        }
    }

    private static async Task<IResult> ImportSettlementAsync(
        [FromForm] IFormFile? file,
        ReconciliationService service,
        IReconciliationQueue queue,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return Results.BadRequest(new { error = "Choose a non-empty CSV settlement file." });
        }

        if (file.Length > MaxUploadBytes)
        {
            return Results.BadRequest(new { error = "Settlement CSV files must be 5 MB or smaller." });
        }

        string? batchId = null;
        try
        {
            await using var stream = file.OpenReadStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var csv = await reader.ReadToEndAsync(cancellationToken);
            var batch = await service.ImportSettlementCsvAsync(file.FileName, csv, cancellationToken);
            batchId = batch.Id;
            await queue.EnqueueAsync(batch.Id, cancellationToken);
            return Results.Accepted($"/api/reconciliation/batches/{batch.Id}", batch);
        }
        catch (ReconciliationInputException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
        catch (ReconciliationQueueUnavailableException exception)
        {
            return Results.Problem(
                title: "Reconciliation queue unavailable",
                detail: batchId is null ? exception.Message : $"{exception.Message} Batch ID: {batchId}",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> ListBatchesAsync(
        [FromQuery] int limit,
        ReconciliationService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.ListBatchesAsync(limit == 0 ? 50 : limit, cancellationToken));

    private static async Task<IResult> GetBatchAsync(
        string id,
        ReconciliationService service,
        CancellationToken cancellationToken)
    {
        var batch = await service.GetBatchAsync(id, cancellationToken);
        return batch is null ? Results.NotFound() : Results.Ok(batch);
    }

    private static async Task<IResult> RequeueBatchAsync(
        string id,
        ReconciliationService service,
        IReconciliationQueue queue,
        CancellationToken cancellationToken)
    {
        try
        {
            await service.RequeueBatchAsync(id, cancellationToken);
            var batch = await service.GetBatchAsync(id, cancellationToken);
            await queue.EnqueueAsync(id, cancellationToken);
            return Results.Accepted($"/api/reconciliation/batches/{id}", batch?.Batch);
        }
        catch (ReconciliationInputException)
        {
            return Results.NotFound();
        }
        catch (ReconciliationConflictException exception)
        {
            return Results.Conflict(new { error = exception.Message });
        }
        catch (ReconciliationQueueUnavailableException exception)
        {
            return Results.Problem(
                title: "Reconciliation queue unavailable",
                detail: exception.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> ListAuditAsync(
        [FromQuery] int limit,
        ReconciliationService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.ListAuditAsync(limit == 0 ? 100 : limit, cancellationToken));

}
