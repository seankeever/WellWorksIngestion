-- =============================================================================
-- WellWorks — Sample Test Data & Debug Queries
-- Run these in SSMS after the API has processed at least one batch.
-- =============================================================================

USE WellWorksIngestion;
GO

-- ─────────────────────────────────────────────────────────────────────────────
-- SECTION A: Manual test data — call the SP directly without the API
-- Useful for verifying the stored procedure in isolation.
-- ─────────────────────────────────────────────────────────────────────────────

-- Declare a TVP variable and load test rows
DECLARE @TestBatch dbo.TransactionStagingType;
DECLARE @BatchID UNIQUEIDENTIFIER = NEWID();

INSERT INTO @TestBatch (TransactionID, MemberID, TransactionDate, TransactionAmount)
VALUES
    ('TXN-001', 'MBR-100', '2026-01-15 10:00:00', 149.99),
    ('TXN-002', 'MBR-101', '2026-01-15 10:05:00',  75.00),
    ('TXN-003', 'MBR-100', '2026-01-15 10:10:00', 299.00),
    ('TXN-001', 'MBR-100', '2026-01-15 10:00:00', 149.99),  -- INTRA-BATCH DUPLICATE
    ('TXN-004', 'MBR-102', '2026-01-15 10:15:00',  12.50);

PRINT 'Running test batch ID: ' + CAST(@BatchID AS NVARCHAR(64));

EXEC dbo.BulkUpsertTransactions
    @BatchID = @BatchID,
    @Staging = @TestBatch;

-- Show what was inserted
SELECT * FROM dbo.Transactions ORDER BY InsertedAt DESC;

-- Show what was logged
SELECT * FROM dbo.TransactionIngestionLog
WHERE BatchID = @BatchID
ORDER BY LoggedAt;
GO

-- ─────────────────────────────────────────────────────────────────────────────
-- SECTION B: Run the same batch again to test INTER-batch duplicate detection
-- ─────────────────────────────────────────────────────────────────────────────
DECLARE @TestBatch2 dbo.TransactionStagingType;
DECLARE @BatchID2 UNIQUEIDENTIFIER = NEWID();

INSERT INTO @TestBatch2 (TransactionID, MemberID, TransactionDate, TransactionAmount)
VALUES
    ('TXN-001', 'MBR-100', '2026-01-15 10:00:00', 149.99),  -- already exists → INTER-batch dupe
    ('TXN-002', 'MBR-101', '2026-01-15 10:05:00',  75.00),  -- already exists → INTER-batch dupe
    ('TXN-005', 'MBR-103', '2026-06-01 09:00:00',  55.00);  -- NEW → should insert

PRINT 'Running inter-batch test, BatchID: ' + CAST(@BatchID2 AS NVARCHAR(64));

EXEC dbo.BulkUpsertTransactions
    @BatchID = @BatchID2,
    @Staging = @TestBatch2;

-- Should show 2 DUPLICATE_INTER rows in the log
SELECT * FROM dbo.TransactionIngestionLog
WHERE BatchID = @BatchID2
ORDER BY LoggedAt;
GO

-- ─────────────────────────────────────────────────────────────────────────────
-- SECTION C: Useful monitoring queries while debugging with the API
-- ─────────────────────────────────────────────────────────────────────────────

-- All transactions ordered by most recent
SELECT TOP 50 * FROM dbo.Transactions ORDER BY InsertedAt DESC;

-- Full log for the most recent batch
SELECT TOP 100 *
FROM dbo.TransactionIngestionLog
ORDER BY LoggedAt DESC;

-- Summary by batch: how many of each log type per batch
SELECT
    BatchID,
    LogType,
    COUNT(*) AS Occurrences,
    MIN(LoggedAt) AS FirstOccurrence
FROM dbo.TransactionIngestionLog
GROUP BY BatchID, LogType
ORDER BY MIN(LoggedAt) DESC;

-- Count of transactions per member
SELECT MemberID, COUNT(*) AS TransactionCount, SUM(TransactionAmount) AS TotalAmount
FROM dbo.Transactions
GROUP BY MemberID
ORDER BY TransactionCount DESC;

-- ─────────────────────────────────────────────────────────────────────────────
-- SECTION D: Reset (wipe all data so you can start fresh)
-- ─────────────────────────────────────────────────────────────────────────────
-- Uncomment and run to wipe all rows without dropping objects:
/*
DELETE FROM dbo.TransactionIngestionLog;
DELETE FROM dbo.Transactions;
PRINT 'All data cleared. Objects preserved.';
*/
GO
