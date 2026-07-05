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

    // Import files are routinely far larger than Kestrel's ~28 MB default request cap,
    // but the ceiling must stay finite: an unauthenticated upload endpoint with no limit
    // is a disk-exhaustion hazard. Uploads are buffered to disk, not memory.
    var maxUploadBytes = builder.Configuration
        .GetSection(LoadstoneOptions.SectionName)
        .Get<LoadstoneOptions>()?.MaxUploadBytes ?? new LoadstoneOptions().MaxUploadBytes;
    builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = maxUploadBytes);
    builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
        options.MultipartBodyLengthLimit = maxUploadBytes);

    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext());

    builder.Services.Configure<LoadstoneOptions>(builder.Configuration.GetSection(LoadstoneOptions.SectionName));
    builder.Services.PostConfigure<LoadstoneOptions>(options =>
        options.ConnectionString ??= builder.Configuration.GetConnectionString("Loadstone"));

    // Fail fast: a misconfigured host should refuse to start, not boot green and error-loop.
    builder.Services.AddOptions<LoadstoneOptions>()
        .Validate(o => !string.IsNullOrWhiteSpace(o.ConnectionString),
            "No database configured. Set Loadstone:ConnectionString or ConnectionStrings:Loadstone.")
        .Validate(o => o.QueuePollInterval > TimeSpan.Zero,
            "Loadstone:QueuePollInterval must be positive.")
        .Validate(o => o.AbandonedJobTimeout >= TimeSpan.FromMinutes(2),
            "Loadstone:AbandonedJobTimeout must be at least 2 minutes — workers heartbeat every 60 seconds, and a shorter timeout would steal jobs from live workers.")
        .Validate(o => o.WriterBatchSize >= 1 && o.WriterBatchMaxRecords >= 1,
            "Loadstone:WriterBatchSize and Loadstone:WriterBatchMaxRecords must be at least 1.")
        .Validate(o => o.MaxUploadBytes > 0,
            "Loadstone:MaxUploadBytes must be positive.")
        .ValidateOnStart();

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

    // The DB check is tagged "ready" so it participates in readiness only: a database blip
    // should pull replicas out of rotation, not have the orchestrator restart them all.
    builder.Services.AddHealthChecks()
        .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);

    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();

    var app = builder.Build();

    app.UseSerilogRequestLogging();
    app.UseSwagger();
    app.UseSwaggerUI(ui => ui.DocumentTitle = "Loadstone API");

    // The operations dashboard is a static, dependency-free page served at "/".
    app.UseDefaultFiles();
    app.UseStaticFiles();

    // Opt-in shared-secret gate for the API surface. Header comparison is constant-time.
    var apiKey = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<LoadstoneOptions>>().Value.ApiKey;
    if (!string.IsNullOrEmpty(apiKey))
    {
        var expected = System.Text.Encoding.UTF8.GetBytes(apiKey);
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api")
                && (!context.Request.Headers.TryGetValue("X-Api-Key", out var supplied)
                    || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                        System.Text.Encoding.UTF8.GetBytes(supplied.ToString()), expected)))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { error = "Missing or invalid X-Api-Key header." });
                return;
            }

            await next();
        });
    }

    app.MapDatasetEndpoints();
    app.MapImportEndpoints();
    app.MapCodeListEndpoints();
    app.MapHealthChecks("/health");

    // Probe split: liveness answers "is the process up" and must not depend on the
    // database; readiness gates traffic on the DB being reachable.
    app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
        Predicate = _ => false,
    });
    app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains("ready"),
    });

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
