using WellWorksIngestion.Api.Models;

namespace WellWorksIngestion.Api.Services;

// ─────────────────────────────────────────────────────────────
// ITransactionIngestionService
// Entry point for the entire pipeline. The controller only
// knows about this interface — no SQL, no validation details.
// ─────────────────────────────────────────────────────────────
public interface ITransactionIngestionService
{
    Task<BatchIngestionResult> IngestBatchAsync(
        IReadOnlyList<TransactionDto> records,
        CancellationToken cancellationToken = default);
}

// ─────────────────────────────────────────────────────────────
// ITransactionValidator
// Pure business rules. No I/O, no DI dependencies.
// This isolation is what makes it trivially unit-testable.
// ─────────────────────────────────────────────────────────────
public interface ITransactionValidator
{
    ValidationResult Validate(TransactionDto record);
}

// ─────────────────────────────────────────────────────────────
// ITransactionRepository
// All database knowledge lives behind this interface.
// Swap the real implementation for a mock in any test.
// ─────────────────────────────────────────────────────────────
public interface ITransactionRepository
{
    /// <summary>
    /// Sends validated records to SQL Server via TVP → stored procedure.
    /// Returns the count of rows the SP actually inserted (inter-batch
    /// duplicates are skipped and logged by the SP itself).
    /// </summary>
    Task<int> BulkInsertAsync(
        Guid batchId,
        IReadOnlyList<TransactionRecord> validRecords,
        CancellationToken cancellationToken);

    /// <summary>
    /// Writes in-process failures (validation errors, intra-batch dupes)
    /// to the log table using a SEPARATE connection so log writes survive
    /// even if the main insert transaction rolled back.
    /// </summary>
    Task LogFailuresAsync(
        Guid batchId,
        IReadOnlyList<IngestionFailure> failures,
        CancellationToken cancellationToken);
}
