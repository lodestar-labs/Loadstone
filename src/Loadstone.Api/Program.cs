using Loadstone.Api;
using Loadstone.Api.Endpoints;
using Loadstone.Readers.Csv;
using Loadstone.Readers.Json;
using Loadstone.Readers.Xml;
using Loadstone.Runtime;
using Loadstone.Runtime.Diagnostics;
using Loadstone.SqlServer;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Import files are routinely far larger than Kestrel's ~28 MB default request cap;
    // uploads are buffered to disk by the form pipeline, not held in memory.
    builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = null);
    builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
        options.MultipartBodyLengthLimit = long.MaxValue);

    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext());

    builder.Services.Configure<LoadstoneOptions>(builder.Configuration.GetSection(LoadstoneOptions.SectionName));
    builder.Services.PostConfigure<LoadstoneOptions>(options =>
        options.ConnectionString ??= builder.Configuration.GetConnectionString("Loadstone"));

    var loadstone = builder.Services.AddLoadstone()
        .UseSqlServer()
        .AddSourceReader<XmlSourceReader>()
        .AddSourceReader<JsonSourceReader>()
        .AddSourceReader<CsvSourceReader>();

    // Configuration-defined lookup providers (Loadstone:SqlLookups): point imports at an
    // existing lookup database without writing code.
    foreach (var lookup in builder.Configuration.GetSection("Loadstone:SqlLookups").Get<SqlLookupOptions[]>() ?? [])
    {
        loadstone.AddSqlLookup(lookup);
    }

    var otel = builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService("loadstone"))
        .WithTracing(tracing => tracing
            .AddAspNetCoreInstrumentation()
            .AddSource(LoadstoneDiagnostics.ActivitySourceName))
        .WithMetrics(metrics => metrics
            .AddAspNetCoreInstrumentation()
            .AddRuntimeInstrumentation()
            .AddMeter(LoadstoneDiagnostics.MeterName));

    // Any OTLP-compatible backend (Azure Monitor, Grafana, Jaeger, ...) is one env var away:
    // set OTEL_EXPORTER_OTLP_ENDPOINT and both signals start flowing.
    if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
    {
        otel.WithTracing(tracing => tracing.AddOtlpExporter())
            .WithMetrics(metrics => metrics.AddOtlpExporter());
    }

    builder.Services.AddHealthChecks()
        .AddCheck<DatabaseHealthCheck>("database");

    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();

    var app = builder.Build();

    app.UseSerilogRequestLogging();
    app.UseSwagger();
    app.UseSwaggerUI(ui => ui.DocumentTitle = "Loadstone API");

    // The operations dashboard is a static, dependency-free page served at "/".
    app.UseDefaultFiles();
    app.UseStaticFiles();

    app.MapDatasetEndpoints();
    app.MapImportEndpoints();
    app.MapCodeListEndpoints();
    app.MapHealthChecks("/health");

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Loadstone failed to start");
    throw;
}
finally
{
    Log.CloseAndFlush();
}
