using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using WellWorksIngestion.Api.Models;
using WellWorksIngestion.Api.Services;

namespace WellWorksIngestion.Api.Repositories;

/// <summary>
/// All database I/O lives here. The service layer has zero SQL knowledge.
///
/// TVP PATTERN: We build a DataTable and pass it to the stored procedure
/// as a dbo.TransactionStagingType parameter. SQL Server receives the
/// entire chunk in one network round-trip and runs set-based deduplication
/// inside the SP. No cursors, no row-by-row round-trips.
///
/// LOG CONNECTION: LogFailuresAsync opens its own SqlConnection
/// deliberately — log writes must commit even if the insert TX rolled back.
/// </summary>
public sealed class TransactionRepository : ITransactionRepository
{
    private readonly string _connectionString;
    private readonly ILogger<TransactionRepository> _logger;

    public TransactionRepository(IConfiguration config, ILogger<TransactionRepository> logger)
    {
        _connectionString = config.GetConnectionString("WellWorksDb")
            ?? throw new InvalidOperationException("Connection string 'WellWorksDb' is missing.");
        _logger = logger;
    }

    public async Task<int> BulkInsertAsync(
        Guid batchId,
        IReadOnlyList<TransactionRecord> records,
        CancellationToken cancellationToken)
    {
        var table = BuildDataTable(records);

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = new SqlCommand("dbo.BulkUpsertTransactions", conn)
        {
            CommandType    = CommandType.StoredProcedure,
            CommandTimeout = 120
        };

        cmd.Parameters.AddWithValue("@BatchID", batchId);

        var tvp = cmd.Parameters.AddWithValue("@Staging", table);
        tvp.SqlDbType = SqlDbType.Structured;
        tvp.TypeName  = "dbo.TransactionStagingType";

        // The SP returns the count of rows actually inserted via OUTPUT or
        // a scalar SELECT. We read it with ExecuteScalarAsync.
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is int count ? count : 0;
    }

    public async Task LogFailuresAsync(
        Guid batchId,
        IReadOnlyList<IngestionFailure> failures,
        CancellationToken cancellationToken)
    {
        // Separate connection — intentional (see class summary).
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        const string sql = @"
            INSERT INTO dbo.TransactionIngestionLog
                (BatchID, TransactionID, LogType, Detail)
            VALUES
                (@BatchID, @TransactionID, @LogType, @Detail)";

        foreach (var f in failures)// this could be switched to TVP if the volume of failures to log grows enough to justify the overhead of building the types to switch this to TVP db inserts
        {
            await conn.ExecuteAsync(
                sql,
                new
                {
                    BatchID       = batchId,
                    TransactionID = f.TransactionId,
                    LogType       = f.LogType,
                    Detail        = f.Detail
                });
        }

        _logger.LogDebug("Logged {Count} failure(s) for batch {BatchId}.", failures.Count, batchId);
    }

    // Build a DataTable matching dbo.TransactionStagingType column-for-column.
    // Column names must match the TYPE definition exactly (SQL Server is case-insensitive
    // for column matching within a TVP, but exact names make the code self-documenting).
    private static DataTable BuildDataTable(IReadOnlyList<TransactionRecord> records)
    {
        var dt = new DataTable();
        dt.Columns.Add("TransactionID",      typeof(string));
        dt.Columns.Add("MemberID",           typeof(string));
        dt.Columns.Add("TransactionDate",    typeof(DateTime));
        dt.Columns.Add("TransactionAmount",  typeof(decimal));

        foreach (var r in records)
            dt.Rows.Add(r.TransactionID, r.MemberID, r.TransactionDate, r.TransactionAmount);

        return dt;
    }
}
