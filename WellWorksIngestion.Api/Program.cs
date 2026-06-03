using WellWorksIngestion.Api.Repositories;
using WellWorksIngestion.Api.Services;
using WellWorksIngestion.Api.Validators;

var builder = WebApplication.CreateBuilder(args);

// ── Services ──────────────────────────────────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new()
    {
        Title       = "WellWorks Transaction Ingestion API",
        Version     = "v1",
        Description = "High-volume transaction ingestion with duplicate handling, partial success, and resiliency."
    });
    // Include XML comments if present (optional)
    var xmlFile = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (File.Exists(xmlPath))
        c.IncludeXmlComments(xmlPath);
});

// ── Application dependencies ──────────────────────────────────────────────────
// Validator is stateless — Singleton is safe and avoids repeated allocations.
builder.Services.AddSingleton<ITransactionValidator, TransactionValidator>();

// Repository and Service are also stateless (no shared mutable fields).
// Singleton is fine; Scoped would also work if you prefer per-request isolation.
builder.Services.AddSingleton<ITransactionRepository, TransactionRepository>();
builder.Services.AddSingleton<ITransactionIngestionService, TransactionIngestionService>();

// ── Logging ───────────────────────────────────────────────────────────────────
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug(); // Visible in Visual Studio Output window during F5

// ── Build ─────────────────────────────────────────────────────────────────────
var app = builder.Build();

// Always show Swagger in development so you can test right away
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "WellWorks Ingestion v1");
        c.RoutePrefix = string.Empty; // Swagger opens at root "/"
    });
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

app.Run();

// Expose Program for integration test host (xUnit WebApplicationFactory)
public partial class Program { }
