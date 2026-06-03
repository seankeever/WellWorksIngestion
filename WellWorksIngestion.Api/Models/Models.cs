namespace WellWorksIngestion.Api.Models;

// ─────────────────────────────────────────────────────────────
// Inbound DTO — what the API receives in the JSON payload
// ─────────────────────────────────────────────────────────────
public sealed class TransactionDto
{
    public string TransactionID { get; set; } = string.Empty;
    public string MemberID { get; set; } = string.Empty;
    public DateTime TransactionDate { get; set; }
    public decimal TransactionAmount { get; set; }
}

// ─────────────────────────────────────────────────────────────
// Internal domain record — only populated after validation passes
// ─────────────────────────────────────────────────────────────
public sealed class TransactionRecord
{
    public string TransactionID { get; init; } = string.Empty;
    public string MemberID { get; init; } = string.Empty;
    public DateTime TransactionDate { get; init; }
    public decimal TransactionAmount { get; init; }

    public static TransactionRecord FromDto(TransactionDto dto) => new()
    {
        TransactionID     = dto.TransactionID.Trim(),
        MemberID          = dto.MemberID.Trim(),
        TransactionDate   = dto.TransactionDate,
        TransactionAmount = dto.TransactionAmount
    };
}

// ─────────────────────────────────────────────────────────────
// Validation result — keeps validation logic infrastructure-free
// ─────────────────────────────────────────────────────────────
public sealed class ValidationResult
{
    public bool IsValid { get; private init; }
    public string? ErrorMessage { get; private init; }

    public static ValidationResult Ok() => new() { IsValid = true };
    public static ValidationResult Fail(string message) => new() { IsValid = false, ErrorMessage = message };
}

// ─────────────────────────────────────────────────────────────
// Failure record written to the log table
// LogType values: VALIDATION_FAIL | DUPLICATE_INTRA | DUPLICATE_INTER | INSERT_FAIL
// ─────────────────────────────────────────────────────────────
public sealed class IngestionFailure
{
    public string? TransactionId { get; init; }
    public string LogType { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;

    public IngestionFailure(string? transactionId, string logType, string detail)
    {
        TransactionId = transactionId;
        LogType       = logType;
        Detail        = detail;
    }
}

// ─────────────────────────────────────────────────────────────
// Response returned to the API caller after a batch completes
// ─────────────────────────────────────────────────────────────
public sealed class BatchIngestionResult
{
    public Guid BatchId { get; init; }
    public int TotalReceived { get; init; }
    public int Inserted { get; init; }
    public int Skipped { get; init; }
    public int ValidationFailures { get; init; }
    public int IntraBatchDuplicates { get; init; }
    public string Message => $"Batch {BatchId}: {Inserted} inserted, {Skipped} skipped (inter-batch duplicates), "
                           + $"{IntraBatchDuplicates} intra-batch duplicates, {ValidationFailures} validation failures.";
}
