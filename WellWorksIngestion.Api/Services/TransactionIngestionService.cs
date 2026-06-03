using Microsoft.Data.SqlClient;
using Polly;
using Polly.Retry;
using WellWorksIngestion.Api.Models;

namespace WellWorksIngestion.Api.Services;

/// <summary>
/// Orchestrates the full ingestion pipeline:
///   1. Validate each record (pure, in-process)
///   2. Deduplicate intra-batch with a HashSet
///   3. Send valid records to SQL Server in chunks via TVP
///   4. Log all failures (validation, dupes) via a separate connection
///
/// DESIGN NOTE: The service is stateless — all state is local to each
/// IngestBatchAsync call, so it's safe to register as Singleton.
/// </summary>
public sealed class TransactionIngestionService : ITransactionIngestionService
{
    // 2,000 rows × ~200 bytes ≈ 400 KB per chunk — stays well under the .NET
    // Large Object Heap threshold (85 KB) per individual object but keeps the
    // total working set manageable even for 100K+ record batches.
    private const int ChunkSize = 2_000;

    private readonly ITransactionValidator _validator;
    private readonly ITransactionRepository _repository;
    private readonly ILogger<TransactionIngestionService> _logger;
    private readonly AsyncRetryPolicy _retryPolicy;

    public TransactionIngestionService(
        ITransactionValidator validator,
        ITransactionRepository repository,
        ILogger<TransactionIngestionService> logger)
    {
        _validator  = validator;
        _repository = repository;
        _logger     = logger;

        // Polly retry: only transient SQL errors get retried.
        // Deliberately NOT catching all SqlExceptions — a PK violation
        // (2627) or schema error is not transient and should not be retried.
        // Error numbers: -2 = timeout, 1205 = deadlock, 233/64/20 = connection drop
        //
        // TransientDbException is also handled so unit tests can simulate
        // transient failures without needing a real SqlException (which has
        // no public constructor and can't be instantiated in test code).
        _retryPolicy = Policy
            .Handle<SqlException>(ex => IsTransient(ex))
            .Or<TransientDbException>()
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)),
                onRetry: (ex, delay, attempt, _) =>
                    _logger.LogWarning(ex,
                        "Transient error on attempt {Attempt}. Retrying in {Delay}s.",
                        attempt, delay.TotalSeconds));
    }

    public async Task<BatchIngestionResult> IngestBatchAsync(
        IReadOnlyList<TransactionDto> records,
        CancellationToken cancellationToken = default)
    {
        var batchId  = Guid.NewGuid();
        var failures = new List<IngestionFailure>();
        var valid    = new List<TransactionRecord>(records.Count);

        _logger.LogInformation("Batch {BatchId} started. Records received: {Count}", batchId, records.Count);

        // ── Phase 1: Validate + intra-batch dedupe ───────────────────────
        // HashSet gives O(1) lookups. We do this in-process BEFORE building
        // the TVP so we don't send rows we already know are redundant.
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dto in records)
        {
            var vr = _validator.Validate(dto);
            if (!vr.IsValid)
            {
                _logger.LogDebug("Batch {BatchId}: VALIDATION_FAIL for {Id} — {Error}",
                    batchId, dto.TransactionID, vr.ErrorMessage);
                failures.Add(new IngestionFailure(dto.TransactionID, "VALIDATION_FAIL", vr.ErrorMessage!));
                continue;
            }

            if (!seenIds.Add(dto.TransactionID.Trim()))
            {
                _logger.LogDebug("Batch {BatchId}: DUPLICATE_INTRA for {Id}", batchId, dto.TransactionID);
                failures.Add(new IngestionFailure(dto.TransactionID, "DUPLICATE_INTRA",
                    "Duplicate TransactionID within this batch payload; first occurrence kept."));
                continue;
            }

            valid.Add(TransactionRecord.FromDto(dto));
        }

        // ── Phase 2: Chunked bulk insert with retry ──────────────────────
        // Each chunk is an independent TVP call. A transient failure only
        // forces a retry of that 2K-row slice, not the entire batch.
        var totalInserted = 0;

        foreach (var chunk in valid.Chunk(ChunkSize))
        {
            var chunkList = (IReadOnlyList<TransactionRecord>)chunk;

            var inserted = await _retryPolicy.ExecuteAsync(async ct =>
                await _repository.BulkInsertAsync(batchId, chunkList, ct),
                cancellationToken);

            totalInserted += inserted;

            // The SP logs inter-batch duplicates directly. The delta between
            // chunk size and inserted count tells us how many were skipped.
            var interDupes = chunk.Length - inserted;
            if (interDupes > 0)
                _logger.LogInformation("Batch {BatchId}: {Count} inter-batch duplicate(s) skipped in this chunk.",
                    batchId, interDupes);
        }

        // ── Phase 3: Persist in-process failures to the log table ────────
        // Separate connection — survives TX rollback on the insert path.
        if (failures.Count > 0)
        {
            await _repository.LogFailuresAsync(batchId, failures, cancellationToken);
            _logger.LogInformation("Batch {BatchId}: {Count} failure(s) logged.", batchId, failures.Count);
        }

        var intraDupes       = failures.Count(f => f.LogType == "DUPLICATE_INTRA");
        var validationFails  = failures.Count(f => f.LogType == "VALIDATION_FAIL");
        var interDupesTotal  = valid.Count - totalInserted;

        var result = new BatchIngestionResult
        {
            BatchId              = batchId,
            TotalReceived        = records.Count,
            Inserted             = totalInserted,
            Skipped              = interDupesTotal,
            ValidationFailures   = validationFails,
            IntraBatchDuplicates = intraDupes
        };

        _logger.LogInformation("Batch {BatchId} complete. {Result}", batchId, result.Message);
        return result;
    }

    private static bool IsTransient(SqlException ex) =>
        ex.Number is -2    // query timeout
                   or 1205 // deadlock victim
                   or 233  // transport-level error (named pipe)
                   or 64   // connection error
                   or 20;  // general network error
}

// Stand-in for transient DB failures — used by unit tests to simulate timeouts
// without needing to construct SqlException (which has no public constructor).
// The Polly retry policy handles both SqlException (production) and this type (tests).
public class TransientDbException : Exception
{
    public TransientDbException(string message) : base(message) { }
}
