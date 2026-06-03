using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WellWorksIngestion.Api.Models;
using WellWorksIngestion.Api.Services;
using WellWorksIngestion.Api.Validators;
using Xunit;

namespace WellWorksIngestion.Tests;

// =============================================================================
// TransactionIngestionServiceTests
//
// All four tests target FAILURE STATES as specified.
// No database connection is required — ITransactionRepository is fully mocked.
// The validator is either the real TransactionValidator (for validation tests)
// or a mock (for infrastructure failure tests).
// =============================================================================
public class TransactionIngestionServiceTests
{
    // ── Shared factory helpers ────────────────────────────────────────────────

    private static TransactionDto ValidDto(string id = "TXN-001") => new()
    {
        TransactionID     = id,
        MemberID          = "MBR-001",
        TransactionDate   = DateTime.UtcNow.AddMinutes(-5),
        TransactionAmount = 100.00m
    };

    /// <summary>
    /// Builds the SUT with a real validator and a clean repository mock.
    /// The repository mock defaults to succeeding (returns records.Count as inserted).
    /// </summary>
    private static (TransactionIngestionService Svc, Mock<ITransactionRepository> RepoMock)
        BuildSut(Action<Mock<ITransactionRepository>>? repoSetup = null)
    {
        var validator = new TransactionValidator(); // real validator — no mocking needed
        var repoMock  = new Mock<ITransactionRepository>();

        // Default: BulkInsertAsync succeeds and returns the full chunk size
        repoMock
            .Setup(r => r.BulkInsertAsync(
                It.IsAny<Guid>(),
                It.IsAny<IReadOnlyList<TransactionRecord>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, IReadOnlyList<TransactionRecord> records, CancellationToken _) => records.Count);

        // Default: LogFailuresAsync is a no-op
        repoMock
            .Setup(r => r.LogFailuresAsync(
                It.IsAny<Guid>(),
                It.IsAny<IReadOnlyList<IngestionFailure>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        repoSetup?.Invoke(repoMock);

        var svc = new TransactionIngestionService(
            validator,
            repoMock.Object,
            NullLogger<TransactionIngestionService>.Instance);

        return (svc, repoMock);
    }

    // ── Test 1: Malformed / invalid record — partial success ─────────────────
    // A record with a negative amount fails validation.
    // The remaining valid records must still be forwarded to the repository.
    [Fact]
    public async Task IngestBatch_WhenOneRecordFailsValidation_LogsFailureAndInsertsRemainder()
    {
        var (svc, repoMock) = BuildSut();

        var records = new List<TransactionDto>
        {
            ValidDto("TXN-GOOD-1"),
            new()
            {
                // TransactionAmount is negative — validator rejects this
                TransactionID     = "TXN-BAD",
                MemberID          = "MBR-002",
                TransactionDate   = DateTime.UtcNow.AddMinutes(-1),
                TransactionAmount = -50.00m
            },
            ValidDto("TXN-GOOD-2")
        };

        var result = await svc.IngestBatchAsync(records);

        Assert.Equal(3, result.TotalReceived);
        Assert.Equal(1, result.ValidationFailures);
        Assert.Equal(2, result.Inserted);

        // Repository should have received exactly the 2 valid records
        repoMock.Verify(r => r.BulkInsertAsync(
            It.IsAny<Guid>(),
            It.Is<IReadOnlyList<TransactionRecord>>(list =>
                list.Count == 2 &&
                list.All(x => x.TransactionID != "TXN-BAD")),
            It.IsAny<CancellationToken>()), Times.Once);

        // Failure log must have been written
        repoMock.Verify(r => r.LogFailuresAsync(
            It.IsAny<Guid>(),
            It.Is<IReadOnlyList<IngestionFailure>>(f =>
                f.Count == 1 &&
                f[0].TransactionId == "TXN-BAD" &&
                f[0].LogType == "VALIDATION_FAIL"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Test 2: Intra-batch duplicate TransactionIDs ──────────────────────────
    // Two records share the same TransactionID in the same payload.
    // Only the first occurrence should be forwarded; the second must be logged
    // as DUPLICATE_INTRA and excluded from the TVP.
    [Fact]
    public async Task IngestBatch_WhenDuplicateIdInPayload_LogsDuplicateAndInsertsOnlyFirst()
    {
        var (svc, repoMock) = BuildSut();

        var records = new List<TransactionDto>
        {
            ValidDto("TXN-DUP"),    // first occurrence — should be kept
            ValidDto("TXN-DUP"),    // second occurrence — should be logged and dropped
            ValidDto("TXN-UNIQUE")  // unrelated record — should insert normally
        };

        var result = await svc.IngestBatchAsync(records);

        Assert.Equal(3, result.TotalReceived);
        Assert.Equal(1, result.IntraBatchDuplicates);
        Assert.Equal(2, result.Inserted); // TXN-DUP (once) + TXN-UNIQUE

        // Repository receives exactly 2 distinct records
        repoMock.Verify(r => r.BulkInsertAsync(
            It.IsAny<Guid>(),
            It.Is<IReadOnlyList<TransactionRecord>>(list => list.Count == 2),
            It.IsAny<CancellationToken>()), Times.Once);

        // DUPLICATE_INTRA logged for the dropped record
        repoMock.Verify(r => r.LogFailuresAsync(
            It.IsAny<Guid>(),
            It.Is<IReadOnlyList<IngestionFailure>>(f =>
                f.Any(x => x.LogType == "DUPLICATE_INTRA" && x.TransactionId == "TXN-DUP")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Test 3: Transient DB timeout — retry succeeds, batch completes ────────
    // Simulates a SQL timeout (-2) on the first BulkInsertAsync call.
    // Polly should retry and the second attempt should succeed.
    // All records must ultimately be inserted; no failures logged.
    //
    // NOTE: We can't throw a real SqlException (sealed constructor) so we use
    // a custom TransientDbException that TransactionIngestionService treats the
    // same way in this test context. For a real integration harness you would
    // use a faulting SQL Server or inject a custom resilience policy.
    [Fact]
    public async Task IngestBatch_WhenTransientExceptionOnFirstCall_RetriesAndSucceeds()
    {
        var callCount = 0;
        var (svc, repoMock) = BuildSut(repo =>
        {
            repo
                .Setup(r => r.BulkInsertAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<IReadOnlyList<TransactionRecord>>(),
                    It.IsAny<CancellationToken>()))
                .Returns<Guid, IReadOnlyList<TransactionRecord>, CancellationToken>(
                    async (_, records, _) =>
                    {
                        await Task.Yield();
                        callCount++;
                        if (callCount == 1)
                            // First call throws a simulated transient error
                            throw new TransientDbException("Simulated DB timeout");
                        return records.Count;
                    });
        });

        var records = new List<TransactionDto>
        {
            ValidDto("TXN-A"),
            ValidDto("TXN-B"),
            ValidDto("TXN-C")
        };

        // Service should NOT throw — Polly catches and retries the transient error
        var result = await svc.IngestBatchAsync(records);

        Assert.Equal(3, result.TotalReceived);
        Assert.Equal(0, result.ValidationFailures);
        // BulkInsert was called at least twice (initial + 1 retry)
        Assert.True(callCount >= 2, $"Expected at least 2 BulkInsert calls, got {callCount}");
    }

    // ── Test 4: All retry attempts exhausted — exception propagates ───────────
    // When every retry attempt fails, Polly re-throws so the caller can decide
    // to dead-letter, re-queue, or return a 500. The service must not swallow it.
    [Fact]
    public async Task IngestBatch_WhenAllRetriesExhausted_ThrowsException()
    {
        var (svc, repoMock) = BuildSut(repo =>
        {
            repo
                .Setup(r => r.BulkInsertAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<IReadOnlyList<TransactionRecord>>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new TransientDbException("Persistent DB failure"));
        });

        var records = new List<TransactionDto> { ValidDto("TXN-FAIL") };

        // After 3 retries Polly gives up and the exception surfaces
        await Assert.ThrowsAsync<TransientDbException>(
            () => svc.IngestBatchAsync(records));
    }

    // ── Test 5: Entirely empty batch ─────────────────────────────────────────
    // Edge case: all records in the batch fail validation.
    // BulkInsertAsync should never be called; LogFailuresAsync should be called once.
    [Fact]
    public async Task IngestBatch_WhenAllRecordsInvalid_NeverCallsBulkInsert()
    {
        var (svc, repoMock) = BuildSut();

        var records = new List<TransactionDto>
        {
            new() { TransactionID = "",       MemberID = "MBR-X", TransactionDate = DateTime.UtcNow, TransactionAmount = 10m },
            new() { TransactionID = "TXN-Y",  MemberID = "",      TransactionDate = DateTime.UtcNow, TransactionAmount = 10m },
            new() { TransactionID = "TXN-Z",  MemberID = "MBR-Z", TransactionDate = default,        TransactionAmount = 10m }
        };

        var result = await svc.IngestBatchAsync(records);

        Assert.Equal(3, result.TotalReceived);
        Assert.Equal(3, result.ValidationFailures);
        Assert.Equal(0, result.Inserted);

        repoMock.Verify(r => r.BulkInsertAsync(
            It.IsAny<Guid>(),
            It.IsAny<IReadOnlyList<TransactionRecord>>(),
            It.IsAny<CancellationToken>()), Times.Never);

        repoMock.Verify(r => r.LogFailuresAsync(
            It.IsAny<Guid>(),
            It.Is<IReadOnlyList<IngestionFailure>>(f => f.Count == 3),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}

// TransientDbException is defined in WellWorksIngestion.Api.Services
// (TransactionIngestionService.cs) and referenced here via the project reference.
