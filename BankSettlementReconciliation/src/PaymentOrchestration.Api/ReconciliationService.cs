using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace PaymentOrchestration.Api;

public sealed class ReconciliationService(PaymentDbContext database, TimeProvider timeProvider)
{
    private const int MaxCsvBytes = 5 * 1024 * 1024;
    private const int MaxRows = 50_000;

    public async Task<InternalTransactionView> CreateInternalTransactionAsync(
        CreateInternalTransactionRequest request,
        CancellationToken cancellationToken)
    {
        var reference = request.ExternalReference?.Trim();
        var currency = request.Currency?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(reference) || reference.Length > 200)
        {
            throw new ReconciliationInputException("Transaction reference is required and must be at most 200 characters.");
        }

        if (request.AmountMinor <= 0)
        {
            throw new ReconciliationInputException("Amount must be greater than zero in minor currency units.");
        }

        if (!IsCurrency(currency))
        {
            throw new ReconciliationInputException("Currency must be a three-letter code.");
        }

        var existing = await database.InternalTransactions
            .SingleOrDefaultAsync(transaction => transaction.ExternalReference == reference, cancellationToken);
        if (existing is not null)
        {
            throw new ReconciliationConflictException(
                $"An internal transaction with reference '{reference}' already exists.");
        }

        var transaction = new InternalTransaction
        {
            Id = Guid.NewGuid().ToString("N"),
            ExternalReference = reference,
            AmountMinor = request.AmountMinor,
            Currency = currency!,
            OccurredAt = request.OccurredAt,
            CreatedAt = timeProvider.GetUtcNow()
        };
        database.InternalTransactions.Add(transaction);
        database.ReconciliationAuditEntries.Add(new ReconciliationAuditEntry
        {
            BatchId = null,
            InternalTransactionId = transaction.Id,
            Event = "internal_transaction_created",
            Actor = "api",
            Detail = reference,
            CreatedAt = transaction.CreatedAt
        });

        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueConstraintViolation(exception))
        {
            throw new ReconciliationConflictException(
                $"An internal transaction with reference '{reference}' already exists.");
        }

        return transaction.ToView();
    }

    public async Task<IReadOnlyList<InternalTransactionView>> ListInternalTransactionsAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        var boundedLimit = Math.Clamp(limit, 1, 200);
        var transactions = await database.InternalTransactions
            .FromSqlInterpolated(
                $"SELECT * FROM \"InternalTransactions\" ORDER BY \"CreatedAt\" DESC LIMIT {boundedLimit}")
            .ToArrayAsync(cancellationToken);
        return transactions.Select(transaction => transaction.ToView()).ToArray();
    }

    public async Task<ReconciliationBatchView> ImportSettlementCsvAsync(
        string fileName,
        string csv,
        CancellationToken cancellationToken)
    {
        if (Encoding.UTF8.GetByteCount(csv) > MaxCsvBytes)
        {
            throw new ReconciliationInputException("Settlement CSV files must be 5 MB or smaller.");
        }

        var rows = ParseCsv(csv);
        if (rows.Count < 2)
        {
            throw new ReconciliationInputException("The settlement CSV must contain a header and at least one record.");
        }

        if (rows.Count - 1 > MaxRows)
        {
            throw new ReconciliationInputException($"A settlement file may contain at most {MaxRows} records.");
        }

        var header = rows[0].Select(value => value.Trim()).ToArray();
        var referenceIndex = HeaderIndex(header, "transactionReference");
        var amountIndex = HeaderIndex(header, "amount");
        var currencyIndex = HeaderIndex(header, "currency");
        var settledAtIndex = HeaderIndex(header, "settledAt");
        var batchId = Guid.NewGuid().ToString("N");
        var now = timeProvider.GetUtcNow();
        var items = new List<SettlementItem>(rows.Count - 1);

        for (var index = 1; index < rows.Count; index++)
        {
            var row = rows[index];
            var lineNumber = index + 1;
            if (row.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            if (row.Count <= Math.Max(Math.Max(referenceIndex, amountIndex), Math.Max(currencyIndex, settledAtIndex)))
            {
                throw new ReconciliationInputException($"CSV row {lineNumber} is missing one or more required fields.");
            }

            var reference = row[referenceIndex].Trim();
            var currency = row[currencyIndex].Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(reference) || reference.Length > 200)
            {
                throw new ReconciliationInputException($"CSV row {lineNumber} has an invalid transaction reference.");
            }

            if (!TryParseAmount(row[amountIndex], out var amountMinor))
            {
                throw new ReconciliationInputException($"CSV row {lineNumber} has an invalid amount; use a positive amount with at most two decimals.");
            }

            if (!IsCurrency(currency))
            {
                throw new ReconciliationInputException($"CSV row {lineNumber} has an invalid three-letter currency code.");
            }

            if (!DateTimeOffset.TryParse(
                    row[settledAtIndex],
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var settledAt))
            {
                throw new ReconciliationInputException($"CSV row {lineNumber} has an invalid settledAt timestamp.");
            }

            items.Add(new SettlementItem
            {
                BatchId = batchId,
                RowNumber = lineNumber,
                Source = "Bank",
                TransactionReference = reference,
                AmountMinor = amountMinor,
                Currency = currency!,
                SettledAt = settledAt,
                Status = ReconciliationItemStatus.Pending
            });
        }

        if (items.Count == 0)
        {
            throw new ReconciliationInputException("The settlement CSV does not contain any records.");
        }

        var batch = new ReconciliationBatch
        {
            Id = batchId,
            FileName = NormalizeFileName(fileName),
            Status = ReconciliationBatchStatus.Pending,
            CreatedAt = now,
            Items = items,
            AuditEntries =
            [
                new ReconciliationAuditEntry
                {
                    BatchId = batchId,
                    Event = "settlement_file_imported",
                    Actor = "api",
                    Detail = $"{items.Count} records",
                    CreatedAt = now
                }
            ]
        };
        database.ReconciliationBatches.Add(batch);
        await database.SaveChangesAsync(cancellationToken);
        return batch.ToView();
    }

    public async Task<IReadOnlyList<ReconciliationBatchView>> ListBatchesAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        var boundedLimit = Math.Clamp(limit, 1, 100);
        var batches = await database.ReconciliationBatches
            .FromSqlInterpolated(
                $"SELECT * FROM \"ReconciliationBatches\" ORDER BY \"CreatedAt\" DESC LIMIT {boundedLimit}")
            .ToArrayAsync(cancellationToken);
        if (batches.Length == 0)
        {
            return [];
        }

        var batchIds = batches.Select(batch => batch.Id).ToArray();
        var itemCounts = await database.SettlementItems
            .Where(item => batchIds.Contains(item.BatchId))
            .GroupBy(item => new { item.BatchId, item.Status })
            .Select(group => new
            {
                group.Key.BatchId,
                group.Key.Status,
                Count = group.Count()
            })
            .ToArrayAsync(cancellationToken);
        return batches.Select(batch =>
        {
            var counts = itemCounts
                .Where(item => item.BatchId == batch.Id)
                .ToDictionary(item => item.Status, item => item.Count);
            return batch.ToView(new(
                counts.Values.Sum(),
                counts.GetValueOrDefault(ReconciliationItemStatus.Matched),
                counts.GetValueOrDefault(ReconciliationItemStatus.Duplicate),
                counts.GetValueOrDefault(ReconciliationItemStatus.Mismatched),
                counts.GetValueOrDefault(ReconciliationItemStatus.MissingInternal),
                counts.GetValueOrDefault(ReconciliationItemStatus.MissingSettlement)));
        }).ToArray();
    }

    public async Task<ReconciliationBatchDetails?> GetBatchAsync(
        string id,
        CancellationToken cancellationToken)
    {
        var batch = await database.ReconciliationBatches
            .Include(value => value.Items)
            .Include(value => value.AuditEntries)
            .AsSplitQuery()
            .SingleOrDefaultAsync(value => value.Id == id, cancellationToken);
        return batch?.ToDetails();
    }

    public async Task<IReadOnlyList<ReconciliationAuditView>> ListAuditAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        var boundedLimit = Math.Clamp(limit, 1, 500);
        var entries = await database.ReconciliationAuditEntries
            .FromSqlInterpolated(
                $"SELECT * FROM \"ReconciliationAuditEntries\" ORDER BY \"CreatedAt\" DESC, \"Id\" DESC LIMIT {boundedLimit}")
            .ToArrayAsync(cancellationToken);
        return entries.Select(entry => new ReconciliationAuditView(
            entry.Id,
            entry.BatchId,
            entry.InternalTransactionId,
            entry.Event,
            entry.Actor,
            entry.Detail,
            entry.CreatedAt)).ToArray();
    }

    public async Task<IReadOnlyList<string>> RecoverUnfinishedBatchesAsync(CancellationToken cancellationToken)
    {
        var batches = await database.ReconciliationBatches
            .Where(batch => batch.Status == ReconciliationBatchStatus.Pending
                || batch.Status == ReconciliationBatchStatus.Processing)
            .ToArrayAsync(cancellationToken);
        foreach (var batch in batches)
        {
            if (batch.Status == ReconciliationBatchStatus.Processing)
            {
                batch.Status = ReconciliationBatchStatus.Pending;
                batch.ErrorMessage = null;
                database.ReconciliationAuditEntries.Add(new ReconciliationAuditEntry
                {
                    BatchId = batch.Id,
                    Event = "reconciliation_recovered",
                    Actor = "worker",
                    Detail = "Requeued after an interrupted processing attempt.",
                    CreatedAt = timeProvider.GetUtcNow()
                });
            }
        }

        await database.SaveChangesAsync(cancellationToken);
        return batches.Select(batch => batch.Id).ToArray();
    }

    public async Task ProcessBatchAsync(string batchId, CancellationToken cancellationToken)
    {
        var batch = await database.ReconciliationBatches
            .Include(value => value.Items)
            .Include(value => value.AuditEntries)
            .SingleOrDefaultAsync(value => value.Id == batchId, cancellationToken)
            ?? throw new ReconciliationInputException($"Settlement batch '{batchId}' was not found.");

        if (batch.Status is ReconciliationBatchStatus.Completed or ReconciliationBatchStatus.Processing)
        {
            return;
        }

        batch.Status = ReconciliationBatchStatus.Processing;
        batch.ErrorMessage = null;
        var startedAt = timeProvider.GetUtcNow();
        batch.AuditEntries.Add(new ReconciliationAuditEntry
        {
            BatchId = batchId,
            Event = "reconciliation_started",
            Actor = "worker",
            CreatedAt = startedAt
        });
        await database.SaveChangesAsync(cancellationToken);

        try
        {
            MatchBatch(batch, startedAt, cancellationToken);
            batch.Status = ReconciliationBatchStatus.Completed;
            batch.ProcessedAt = timeProvider.GetUtcNow();
            batch.AuditEntries.Add(new ReconciliationAuditEntry
            {
                BatchId = batchId,
                Event = "reconciliation_completed",
                Actor = "worker",
                Detail = $"{batch.Items.Count} findings",
                CreatedAt = batch.ProcessedAt.Value
            });
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            batch.Status = ReconciliationBatchStatus.Failed;
            batch.ErrorMessage = "The reconciliation could not be completed. Review the audit trail and retry.";
            batch.ProcessedAt = timeProvider.GetUtcNow();
            batch.AuditEntries.Add(new ReconciliationAuditEntry
            {
                BatchId = batchId,
                Event = "reconciliation_failed",
                Actor = "worker",
                Detail = exception.GetType().Name,
                CreatedAt = batch.ProcessedAt.Value
            });
            await database.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task RequeueBatchAsync(string batchId, CancellationToken cancellationToken)
    {
        var batch = await database.ReconciliationBatches
            .SingleOrDefaultAsync(value => value.Id == batchId, cancellationToken)
            ?? throw new ReconciliationInputException($"Settlement batch '{batchId}' was not found.");
        if (batch.Status is not (ReconciliationBatchStatus.Failed or ReconciliationBatchStatus.Pending))
        {
            throw new ReconciliationConflictException("Only pending or failed batches can be requeued.");
        }

        if (batch.Status == ReconciliationBatchStatus.Failed)
        {
            batch.Status = ReconciliationBatchStatus.Pending;
            batch.ProcessedAt = null;
            batch.ErrorMessage = null;
            var items = await database.SettlementItems
                         .Where(item => item.BatchId == batchId)
                         .ToArrayAsync(cancellationToken);
            foreach (var item in items.Where(item => item.Source == "Internal"))
            {
                database.SettlementItems.Remove(item);
            }

            foreach (var item in items.Where(item => item.Source == "Bank"))
            {
                item.Status = ReconciliationItemStatus.Pending;
                item.InternalTransactionId = null;
                item.Detail = null;
            }

            database.ReconciliationAuditEntries.Add(new ReconciliationAuditEntry
            {
                BatchId = batchId,
                Event = "reconciliation_requeued",
                Actor = "api",
                CreatedAt = timeProvider.GetUtcNow()
            });
            await database.SaveChangesAsync(cancellationToken);
        }
    }

    private void MatchBatch(
        ReconciliationBatch batch,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        var bankItems = batch.Items
            .Where(item => item.Source == "Bank")
            .OrderBy(item => item.RowNumber)
            .ToArray();
        var bankReferences = bankItems
            .Select(item => item.TransactionReference)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var firstDate = bankItems.Min(item => item.SettledAt.UtcDateTime.Date);
        var lastDate = bankItems.Max(item => item.SettledAt.UtcDateTime.Date);
        var start = new DateTimeOffset(firstDate, TimeSpan.Zero);
        var end = new DateTimeOffset(lastDate.AddDays(1), TimeSpan.Zero);
        var allInternalTransactions = database.InternalTransactions.ToArray();
        var internalTransactions = allInternalTransactions
            .ToDictionary(transaction => transaction.ExternalReference, StringComparer.OrdinalIgnoreCase);
        var seenReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var auditEntries = new List<ReconciliationAuditEntry>();

        foreach (var item in bankItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!seenReferences.Add(item.TransactionReference))
            {
                SetOutcome(item, ReconciliationItemStatus.Duplicate, null, "Duplicate settlement reference in this file.");
            }
            else if (!internalTransactions.TryGetValue(item.TransactionReference, out var internalTransaction))
            {
                SetOutcome(item, ReconciliationItemStatus.MissingInternal, null, "No internal transaction exists for this reference.");
            }
            else
            {
                item.InternalTransactionId = internalTransaction.Id;
                if (item.AmountMinor != internalTransaction.AmountMinor
                    || !string.Equals(item.Currency, internalTransaction.Currency, StringComparison.OrdinalIgnoreCase))
                {
                    var differences = new List<string>();
                    if (item.AmountMinor != internalTransaction.AmountMinor)
                    {
                        differences.Add($"amount bank={item.AmountMinor}, internal={internalTransaction.AmountMinor}");
                    }

                    if (!string.Equals(item.Currency, internalTransaction.Currency, StringComparison.OrdinalIgnoreCase))
                    {
                        differences.Add($"currency bank={item.Currency}, internal={internalTransaction.Currency}");
                    }

                    SetOutcome(item, ReconciliationItemStatus.Mismatched, internalTransaction.Id, string.Join("; ", differences));
                }
                else
                {
                    SetOutcome(item, ReconciliationItemStatus.Matched, internalTransaction.Id, "Reference, amount, and currency matched.");
                }
            }

            auditEntries.Add(CreateOutcomeAudit(batch.Id, item, startedAt));
        }

        var missingRowNumber = bankItems.Max(item => item.RowNumber);
        foreach (var internalTransaction in allInternalTransactions
                     .Where(transaction => transaction.OccurredAt >= start && transaction.OccurredAt < end)
                     .Where(transaction => !bankReferences.Contains(transaction.ExternalReference))
                     .OrderBy(transaction => transaction.ExternalReference, StringComparer.OrdinalIgnoreCase))
        {
            missingRowNumber++;
            var item = new SettlementItem
            {
                BatchId = batch.Id,
                RowNumber = missingRowNumber,
                Source = "Internal",
                TransactionReference = internalTransaction.ExternalReference,
                AmountMinor = internalTransaction.AmountMinor,
                Currency = internalTransaction.Currency,
                SettledAt = internalTransaction.OccurredAt,
                Status = ReconciliationItemStatus.MissingSettlement,
                InternalTransactionId = internalTransaction.Id,
                Detail = "Internal transaction is absent from the bank settlement file."
            };
            batch.Items.Add(item);
            auditEntries.Add(new ReconciliationAuditEntry
            {
                BatchId = batch.Id,
                InternalTransactionId = internalTransaction.Id,
                Event = "missing_settlement",
                Actor = "worker",
                Detail = internalTransaction.ExternalReference,
                CreatedAt = startedAt
            });
        }

        batch.AuditEntries.AddRange(auditEntries);
    }

    private static void SetOutcome(
        SettlementItem item,
        ReconciliationItemStatus status,
        string? internalTransactionId,
        string detail)
    {
        item.Status = status;
        item.InternalTransactionId = internalTransactionId;
        item.Detail = detail;
    }

    private static ReconciliationAuditEntry CreateOutcomeAudit(
        string batchId,
        SettlementItem item,
        DateTimeOffset timestamp) =>
        new()
        {
            BatchId = batchId,
            InternalTransactionId = item.InternalTransactionId,
            Event = $"reconciliation_{ToEventName(item.Status)}",
            Actor = "worker",
            Detail = $"{item.TransactionReference}: {item.Detail}",
            CreatedAt = timestamp
        };

    private static string ToEventName(ReconciliationItemStatus status) => status switch
    {
        ReconciliationItemStatus.MissingInternal => "missing_internal",
        ReconciliationItemStatus.MissingSettlement => "missing_settlement",
        _ => status.ToString().ToLowerInvariant()
    };

    private static bool IsCurrency(string? value) =>
        value is { Length: 3 } && value.All(char.IsAsciiLetter);

    private static bool TryParseAmount(string value, out long amountMinor)
    {
        amountMinor = 0;
        if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount)
            || amount <= 0
            || amount * 100 != decimal.Truncate(amount * 100))
        {
            return false;
        }

        try
        {
            amountMinor = checked((long)(amount * 100));
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static string NormalizeFileName(string? fileName)
    {
        var normalized = Path.GetFileName(fileName ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(normalized) ? "settlement.csv" : normalized[..Math.Min(normalized.Length, 255)];
    }

    private static int HeaderIndex(IReadOnlyList<string> headers, string requiredName)
    {
        var index = -1;
        for (var current = 0; current < headers.Count; current++)
        {
            if (string.Equals(headers[current], requiredName, StringComparison.OrdinalIgnoreCase))
            {
                index = current;
                break;
            }
        }

        return index >= 0
            ? index
            : throw new ReconciliationInputException(
                $"The settlement CSV is missing the required '{requiredName}' column.");
    }

    private static List<List<string>> ParseCsv(string csv)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var fieldClosed = false;

        for (var index = 0; index < csv.Length; index++)
        {
            var current = csv[index];
            if (inQuotes)
            {
                if (current == '"' && index + 1 < csv.Length && csv[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                }
                else if (current == '"')
                {
                    inQuotes = false;
                    fieldClosed = true;
                }
                else
                {
                    field.Append(current);
                }

                continue;
            }

            if (current == ',')
            {
                row.Add(field.ToString());
                field.Clear();
                fieldClosed = false;
                continue;
            }

            if (current is '\r' or '\n')
            {
                if (current == '\r' && index + 1 < csv.Length && csv[index + 1] == '\n')
                {
                    index++;
                }

                row.Add(field.ToString());
                if (row.Any(value => value.Length > 0) || field.Length > 0)
                {
                    rows.Add(row);
                }

                row = [];
                field.Clear();
                fieldClosed = false;
                continue;
            }

            if (current == '"' && field.Length == 0 && !fieldClosed)
            {
                inQuotes = true;
            }
            else if (fieldClosed)
            {
                throw new ReconciliationInputException("The settlement CSV contains unexpected characters after a quoted field.");
            }
            else
            {
                field.Append(current);
            }
        }

        if (inQuotes)
        {
            throw new ReconciliationInputException("The settlement CSV contains an unterminated quoted field.");
        }

        if (field.Length > 0 || row.Count > 0 || fieldClosed)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        return rows;
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException exception) =>
        exception.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteExtendedErrorCode: 2067 };
}
