-- =============================================================================
-- WellWorks Transaction Ingestion — Full Database Setup
-- Run this script once in SSMS against your local SQL Server instance.
-- It is idempotent: safe to run multiple times.
-- =============================================================================

USE master;
GO

-- ── 1. Create database if it doesn't exist ────────────────────────────────────
IF NOT EXISTS (SELECT name FROM sys.databases WHERE name = N'WellWorksIngestion')
BEGIN
    CREATE DATABASE WellWorksIngestion;
    PRINT 'Database WellWorksIngestion created.';
END
ELSE
    PRINT 'Database WellWorksIngestion already exists — skipping create.';
GO

USE WellWorksIngestion;
GO

-- ── 2. Transactions table ─────────────────────────────────────────────────────
-- Clustered PK on TransactionID:
--   • Guarantees uniqueness at the storage engine level (backstop against app bugs)
--   • Makes the WHERE NOT EXISTS in the SP an index seek, not a scan
--   • NVARCHAR(64) gives us UUID, alphanumeric, or prefixed ID formats
IF NOT EXISTS (
    SELECT 1 FROM sys.objects
    WHERE object_id = OBJECT_ID(N'dbo.Transactions') AND type = N'U'
)
BEGIN
    CREATE TABLE dbo.Transactions (
        TransactionID     NVARCHAR(64)   NOT NULL,
        MemberID          NVARCHAR(64)   NOT NULL,
        TransactionDate   DATETIME2(0)   NOT NULL,
        TransactionAmount DECIMAL(18,2)  NOT NULL,
        InsertedAt        DATETIME2      NOT NULL CONSTRAINT DF_Transactions_InsertedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_Transactions PRIMARY KEY CLUSTERED (TransactionID)
    );
    PRINT 'Table dbo.Transactions created.';
END
ELSE
    PRINT 'Table dbo.Transactions already exists — skipping.';
GO

-- Non-clustered index for MemberID-based queries (reporting, lookups)
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('dbo.Transactions') AND name = 'IX_Transactions_MemberID'
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_Transactions_MemberID
        ON dbo.Transactions (MemberID)
        INCLUDE (TransactionDate, TransactionAmount);
    PRINT 'Index IX_Transactions_MemberID created.';
END
GO

-- ── 3. Ingestion log table ────────────────────────────────────────────────────
-- Single table for ALL failure/skip types — query the full story of any
-- batch with a single WHERE BatchID = @id predicate.
-- LogType values: VALIDATION_FAIL | DUPLICATE_INTRA | DUPLICATE_INTER | INSERT_FAIL
IF NOT EXISTS (
    SELECT 1 FROM sys.objects
    WHERE object_id = OBJECT_ID(N'dbo.TransactionIngestionLog') AND type = N'U'
)
BEGIN
    CREATE TABLE dbo.TransactionIngestionLog (
        LogID         BIGINT IDENTITY(1,1)  NOT NULL CONSTRAINT PK_IngestionLog PRIMARY KEY,
        BatchID       UNIQUEIDENTIFIER      NOT NULL,
        TransactionID NVARCHAR(64)          NULL,   -- NULL when record was so malformed we had no ID
        LogType       NVARCHAR(32)          NOT NULL,
        Detail        NVARCHAR(1000)        NULL,
        LoggedAt      DATETIME2             NOT NULL CONSTRAINT DF_IngestionLog_LoggedAt DEFAULT SYSUTCDATETIME()
    );
    PRINT 'Table dbo.TransactionIngestionLog created.';
END
ELSE
    PRINT 'Table dbo.TransactionIngestionLog already exists — skipping.';
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('dbo.TransactionIngestionLog') AND name = 'IX_Log_BatchID'
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_Log_BatchID
        ON dbo.TransactionIngestionLog (BatchID)
        INCLUDE (TransactionID, LogType, LoggedAt);
    PRINT 'Index IX_Log_BatchID created.';
END
GO

-- ── 4. TVP Type ───────────────────────────────────────────────────────────────
-- The C# DataTable is typed against this definition.
-- Column names and order must match BuildDataTable() in TransactionRepository.cs.
IF NOT EXISTS (
    SELECT 1 FROM sys.types WHERE name = 'TransactionStagingType' AND is_table_type = 1
)
BEGIN
    CREATE TYPE dbo.TransactionStagingType AS TABLE (
        TransactionID     NVARCHAR(64)  NOT NULL,
        MemberID          NVARCHAR(64)  NOT NULL,
        TransactionDate   DATETIME2(0)  NOT NULL,
        TransactionAmount DECIMAL(18,2) NOT NULL
    );
    PRINT 'Type dbo.TransactionStagingType created.';
END
ELSE
    PRINT 'Type dbo.TransactionStagingType already exists — skipping.';
GO

-- ── 5. Stored procedure: BulkUpsertTransactions ───────────────────────────────
-- Accepts a TVP, deduplicates both intra-batch and inter-batch using set-based
-- CTE logic, inserts only genuinely new rows, logs duplicates, and returns the
-- count of rows actually inserted so the service layer can track the delta.
CREATE OR ALTER PROCEDURE dbo.BulkUpsertTransactions
    @BatchID  UNIQUEIDENTIFIER,
    @Staging  dbo.TransactionStagingType READONLY
AS
BEGIN
    SET NOCOUNT ON;

    -- Step 1: Resolve intra-batch duplicates.
    --   ROW_NUMBER() OVER (PARTITION BY TransactionID) keeps the first
    --   occurrence of each ID within the TVP. Remaining rows are logged below.
    ;WITH DeduplicatedStaging AS (
        SELECT
            TransactionID,
            MemberID,
            TransactionDate,
            TransactionAmount,
            ROW_NUMBER() OVER (
                PARTITION BY TransactionID
                ORDER BY (SELECT NULL)  -- no meaningful ordering within a batch
            ) AS rn
        FROM @Staging
    )
    SELECT TransactionID, MemberID, TransactionDate, TransactionAmount
    INTO   #Clean
    FROM   DeduplicatedStaging
    WHERE  rn = 1;

    -- Step 2: Log intra-batch duplicates (rows that lost the ROW_NUMBER race).
    INSERT INTO dbo.TransactionIngestionLog (BatchID, TransactionID, LogType, Detail)
    SELECT
        @BatchID,
        s.TransactionID,
        'DUPLICATE_INTRA',
        'Duplicate TransactionID within same batch payload; first occurrence kept.'
    FROM @Staging s
    WHERE NOT EXISTS (
        SELECT 1 FROM #Clean c WHERE c.TransactionID = s.TransactionID
    );

    -- Step 3: Log inter-batch duplicates (IDs already in the live table).
    INSERT INTO dbo.TransactionIngestionLog (BatchID, TransactionID, LogType, Detail)
    SELECT
        @BatchID,
        c.TransactionID,
        'DUPLICATE_INTER',
        'TransactionID already exists in dbo.Transactions; insert skipped.'
    FROM #Clean c
    WHERE EXISTS (
        SELECT 1 FROM dbo.Transactions t WHERE t.TransactionID = c.TransactionID
    );

    -- Step 4: Bulk insert genuinely new rows.
    INSERT INTO dbo.Transactions (TransactionID, MemberID, TransactionDate, TransactionAmount)
    SELECT c.TransactionID, c.MemberID, c.TransactionDate, c.TransactionAmount
    FROM #Clean c
    WHERE NOT EXISTS (
        SELECT 1 FROM dbo.Transactions t WHERE t.TransactionID = c.TransactionID
    );

    -- Return the count of rows actually inserted so the service layer
    -- can compute the inter-batch duplicate delta.
    SELECT @@ROWCOUNT AS InsertedCount;

    DROP TABLE #Clean;
END;
GO

-- ── 6. Verify setup ───────────────────────────────────────────────────────────
PRINT '';
PRINT '=== Setup complete. Verification:';
SELECT 'dbo.Transactions'         AS ObjectName, 'Table'  AS Type WHERE OBJECT_ID('dbo.Transactions') IS NOT NULL
UNION ALL
SELECT 'dbo.TransactionIngestionLog', 'Table'  WHERE OBJECT_ID('dbo.TransactionIngestionLog') IS NOT NULL
UNION ALL
SELECT 'dbo.TransactionStagingType',  'TVP Type' WHERE EXISTS (SELECT 1 FROM sys.types WHERE name = 'TransactionStagingType')
UNION ALL
SELECT 'dbo.BulkUpsertTransactions',  'Stored Procedure' WHERE OBJECT_ID('dbo.BulkUpsertTransactions') IS NOT NULL;
GO

PRINT 'All objects verified. You are ready to run the API.';
GO
