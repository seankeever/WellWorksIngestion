using Microsoft.AspNetCore.Mvc;
using WellWorksIngestion.Api.Models;
using WellWorksIngestion.Api.Services;

namespace WellWorksIngestion.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public sealed class TransactionsController : ControllerBase
{
    private readonly ITransactionIngestionService _ingestionService;
    private readonly ILogger<TransactionsController> _logger;

    public TransactionsController(
        ITransactionIngestionService ingestionService,
        ILogger<TransactionsController> logger)
    {
        _ingestionService = ingestionService;
        _logger           = logger;
    }

    /// <summary>
    /// Ingest a batch of transactions.
    /// Accepts a JSON array of transaction objects.
    /// Returns a summary of what was inserted, skipped, and rejected.
    /// </summary>
    /// <param name="records">Array of transaction DTOs.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    [HttpPost("ingest")]
    [ProducesResponseType(typeof(BatchIngestionResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> IngestBatch(
        [FromBody] List<TransactionDto> records,
        CancellationToken cancellationToken)
    {
        if (records is null || records.Count == 0)
            return BadRequest("Request body must contain at least one transaction record.");

        _logger.LogInformation("POST /api/transactions/ingest received {Count} record(s).", records.Count);

        try
        {
            var result = await _ingestionService.IngestBatchAsync(records, cancellationToken);
            return Ok(result);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Ingestion request was cancelled by the client.");
            return StatusCode(499, "Request cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception during ingestion.");
            return StatusCode(500, "An unexpected error occurred. Check server logs for batch details.");
        }
    }

    /// <summary>
    /// Health check — confirms the API is reachable.
    /// </summary>
    [HttpGet("health")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Health() => Ok(new { status = "healthy", utc = DateTime.UtcNow });
}
