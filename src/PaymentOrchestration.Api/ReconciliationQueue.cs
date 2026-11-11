using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace PaymentOrchestration.Api;

internal sealed record ReconciliationJob(string BatchId);

public interface IReconciliationQueue
{
    Task EnqueueAsync(string batchId, CancellationToken cancellationToken);
}

public sealed class RabbitMqReconciliationQueue(
    IOptions<RabbitMqOptions> options,
    ILogger<RabbitMqReconciliationQueue> logger) : IReconciliationQueue
{
    public Task EnqueueAsync(string batchId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var factory = CreateFactory(options.Value);
            using var connection = factory.CreateConnection();
            using var channel = connection.CreateModel();
            DeclareQueue(channel, options.Value.QueueName);
            channel.ConfirmSelect();
            Publish(channel, options.Value.QueueName, batchId);
            channel.WaitForConfirmsOrDie(TimeSpan.FromSeconds(5));
            return Task.CompletedTask;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Could not enqueue reconciliation batch {BatchId}.", batchId);
            throw new ReconciliationQueueUnavailableException(
                "The settlement file was saved, but the reconciliation queue is unavailable. Retry the batch when RabbitMQ is healthy.",
                exception);
        }
    }

    internal static ConnectionFactory CreateFactory(RabbitMqOptions options) =>
        new()
        {
            HostName = options.HostName,
            Port = options.Port,
            UserName = options.UserName,
            Password = options.Password ?? string.Empty,
            VirtualHost = options.VirtualHost,
            DispatchConsumersAsync = true,
            AutomaticRecoveryEnabled = true,
            NetworkRecoveryInterval = TimeSpan.FromSeconds(Math.Max(1, options.RetryDelaySeconds))
        };

    internal static void DeclareQueue(IModel channel, string queueName) =>
        channel.QueueDeclare(queueName, durable: true, exclusive: false, autoDelete: false);

    internal static void Publish(IModel channel, string queueName, string batchId)
    {
        var properties = channel.CreateBasicProperties();
        properties.Persistent = true;
        properties.ContentType = "application/json";
        var payload = JsonSerializer.SerializeToUtf8Bytes(new ReconciliationJob(batchId));
        channel.BasicPublish(string.Empty, queueName, mandatory: true, properties, payload);
    }
}

public sealed class ReconciliationWorker(
    IOptions<RabbitMqOptions> options,
    IServiceScopeFactory scopeFactory,
    ILogger<ReconciliationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConsumeAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "RabbitMQ reconciliation worker is unavailable; retrying in {RetrySeconds} seconds.",
                    options.Value.RetryDelaySeconds);
                await Task.Delay(
                    TimeSpan.FromSeconds(Math.Max(1, options.Value.RetryDelaySeconds)),
                    stoppingToken);
            }
        }
    }

    private async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        using var connection = RabbitMqReconciliationQueue.CreateFactory(options.Value).CreateConnection();
        using var channel = connection.CreateModel();
        RabbitMqReconciliationQueue.DeclareQueue(channel, options.Value.QueueName);
        channel.ConfirmSelect();
        channel.BasicQos(0, prefetchCount: 1, global: false);
        await RecoverPendingJobsAsync(channel, stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.Received += async (_, delivery) =>
        {
            ReconciliationJob? job;
            try
            {
                job = JsonSerializer.Deserialize<ReconciliationJob>(delivery.Body.ToArray());
            }
            catch (JsonException exception)
            {
                logger.LogError(exception, "Discarding malformed reconciliation queue message.");
                channel.BasicNack(delivery.DeliveryTag, multiple: false, requeue: false);
                return;
            }

            if (job is null || string.IsNullOrWhiteSpace(job.BatchId))
            {
                logger.LogError("Discarding reconciliation queue message with no batch identifier.");
                channel.BasicNack(delivery.DeliveryTag, multiple: false, requeue: false);
                return;
            }

            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<ReconciliationService>();
                await service.ProcessBatchAsync(job.BatchId, stoppingToken);
                channel.BasicAck(delivery.DeliveryTag, multiple: false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                channel.BasicNack(delivery.DeliveryTag, multiple: false, requeue: true);
                throw;
            }
            catch (ReconciliationInputException exception)
            {
                logger.LogWarning(exception, "Discarding reconciliation job for an unavailable batch {BatchId}.", job.BatchId);
                channel.BasicAck(delivery.DeliveryTag, multiple: false);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Failed to process reconciliation batch {BatchId}.", job.BatchId);
                channel.BasicNack(delivery.DeliveryTag, multiple: false, requeue: true);
            }
        };

        channel.BasicConsume(options.Value.QueueName, autoAck: false, consumer);
        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
    }

    private async Task RecoverPendingJobsAsync(IModel channel, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ReconciliationService>();
        var batchIds = await service.RecoverUnfinishedBatchesAsync(cancellationToken);
        foreach (var batchId in batchIds)
        {
            RabbitMqReconciliationQueue.Publish(channel, options.Value.QueueName, batchId);
        }

        if (batchIds.Count > 0)
        {
            channel.WaitForConfirmsOrDie(TimeSpan.FromSeconds(5));
            logger.LogInformation("Recovered {BatchCount} unfinished reconciliation batches.", batchIds.Count);
        }
    }
}

public static class ReconciliationSchema
{
    public static async Task EnsureCreatedAsync(PaymentDbContext database, CancellationToken cancellationToken)
    {
        await database.Database.EnsureCreatedAsync(cancellationToken);
        await database.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "InternalTransactions" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_InternalTransactions" PRIMARY KEY,
                "ExternalReference" TEXT NOT NULL COLLATE NOCASE,
                "AmountMinor" INTEGER NOT NULL,
                "Currency" TEXT NOT NULL,
                "OccurredAt" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_InternalTransactions_ExternalReference"
                ON "InternalTransactions" ("ExternalReference");
            CREATE TABLE IF NOT EXISTS "ReconciliationBatches" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_ReconciliationBatches" PRIMARY KEY,
                "FileName" TEXT NOT NULL,
                "Status" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "ProcessedAt" TEXT NULL,
                "ErrorMessage" TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS "SettlementItems" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_SettlementItems" PRIMARY KEY AUTOINCREMENT,
                "BatchId" TEXT NOT NULL,
                "RowNumber" INTEGER NOT NULL,
                "Source" TEXT NOT NULL,
                "TransactionReference" TEXT NOT NULL,
                "AmountMinor" INTEGER NOT NULL,
                "Currency" TEXT NOT NULL,
                "SettledAt" TEXT NOT NULL,
                "Status" TEXT NOT NULL,
                "InternalTransactionId" TEXT NULL,
                "Detail" TEXT NULL,
                CONSTRAINT "FK_SettlementItems_ReconciliationBatches_BatchId"
                    FOREIGN KEY ("BatchId") REFERENCES "ReconciliationBatches" ("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_SettlementItems_InternalTransactions_InternalTransactionId"
                    FOREIGN KEY ("InternalTransactionId") REFERENCES "InternalTransactions" ("Id") ON DELETE SET NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_SettlementItems_BatchId_RowNumber"
                ON "SettlementItems" ("BatchId", "RowNumber");
            CREATE INDEX IF NOT EXISTS "IX_SettlementItems_InternalTransactionId"
                ON "SettlementItems" ("InternalTransactionId");
            CREATE TABLE IF NOT EXISTS "ReconciliationAuditEntries" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_ReconciliationAuditEntries" PRIMARY KEY AUTOINCREMENT,
                "BatchId" TEXT NULL,
                "InternalTransactionId" TEXT NULL,
                "Event" TEXT NOT NULL,
                "Actor" TEXT NOT NULL,
                "Detail" TEXT NULL,
                "CreatedAt" TEXT NOT NULL,
                CONSTRAINT "FK_ReconciliationAuditEntries_ReconciliationBatches_BatchId"
                    FOREIGN KEY ("BatchId") REFERENCES "ReconciliationBatches" ("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_ReconciliationAuditEntries_InternalTransactions_InternalTransactionId"
                    FOREIGN KEY ("InternalTransactionId") REFERENCES "InternalTransactions" ("Id") ON DELETE SET NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_ReconciliationAuditEntries_BatchId_CreatedAt"
                ON "ReconciliationAuditEntries" ("BatchId", "CreatedAt");
            CREATE INDEX IF NOT EXISTS "IX_ReconciliationAuditEntries_InternalTransactionId"
                ON "ReconciliationAuditEntries" ("InternalTransactionId");
            """,
            cancellationToken);
    }
}
