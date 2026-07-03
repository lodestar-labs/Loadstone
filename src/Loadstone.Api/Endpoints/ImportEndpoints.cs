using Loadstone.Files;
using Loadstone.Jobs;
using Loadstone.Runtime.Datasets;
using Loadstone.Sources;

namespace Loadstone.Api.Endpoints;

public static class ImportEndpoints
{
    public static IEndpointRouteBuilder MapImportEndpoints(this IEndpointRouteBuilder app)
    {
        var imports = app.MapGroup("/api/imports").WithTags("Imports");

        app.MapPost("/api/datasets/{name}/imports", async (
                string name,
                IFormFile file,
                string? format,
                string? requestedBy,
                IDatasetRegistry registry,
                IEnumerable<ISourceReader> readers,
                IFileStore fileStore,
                IImportJobQueue queue,
                CancellationToken cancellationToken) =>
            {
                if (registry.Find(name) is not { } manifest)
                {
                    return Results.NotFound(new { error = $"Dataset '{name}' is not registered." });
                }

                var resolvedFormat = format ?? InferFormat(readers, file.FileName);
                if (resolvedFormat is null)
                {
                    return Results.BadRequest(new
                    {
                        error = $"Could not infer the format of '{file.FileName}'. Pass ?format=xml|json|csv explicitly.",
                    });
                }

                if (!readers.Any(r => string.Equals(r.Format, resolvedFormat, StringComparison.OrdinalIgnoreCase)))
                {
                    return Results.BadRequest(new { error = $"No reader is registered for format '{resolvedFormat}'." });
                }

                if (!manifest.Source.AcceptsFormat(resolvedFormat))
                {
                    return Results.BadRequest(new
                    {
                        error = $"Dataset '{manifest.Name}' accepts only: {string.Join(", ", manifest.Source.Formats)}.",
                    });
                }

                await using var content = file.OpenReadStream();
                var fileReference = await fileStore.SaveAsync(content, file.FileName, cancellationToken);

                var job = new ImportJob
                {
                    Dataset = manifest.Name,
                    Queue = manifest.QueueName,
                    FileName = file.FileName,
                    FileReference = fileReference,
                    Format = resolvedFormat.ToLowerInvariant(),
                    RequestedBy = requestedBy,
                    MaxAttempts = manifest.Queue.MaxAttempts,
                };
                await queue.EnqueueAsync(job, cancellationToken);

                return Results.Accepted($"/api/imports/{job.Id}", new
                {
                    jobId = job.Id,
                    job.CorrelationId,
                    queue = job.Queue,
                    status = job.Status.ToString(),
                });
            })
            .WithTags("Imports")
            .DisableAntiforgery();

        imports.MapGet("/{id:guid}", async (Guid id, IImportJobStore store, CancellationToken cancellationToken) =>
            await store.GetAsync(id, cancellationToken) is { } job
                ? Results.Ok(JobView.From(job))
                : Results.NotFound());

        imports.MapGet("/", async (
            string? dataset,
            ImportJobStatus? status,
            int? top,
            IImportJobStore store,
            CancellationToken cancellationToken) =>
        {
            var jobs = await store.ListAsync(dataset, status, top ?? 100, cancellationToken);
            return Results.Ok(jobs.Select(JobView.From));
        });

        imports.MapGet("/{id:guid}/events", async (Guid id, IImportJobStore store, CancellationToken cancellationToken) =>
            Results.Ok(await store.GetEventsAsync(id, cancellationToken)));

        imports.MapGet("/{id:guid}/rejections", async (
            Guid id,
            int? top,
            IImportJobStore store,
            CancellationToken cancellationToken) =>
            Results.Ok(await store.GetRejectedRowsAsync(id, top ?? 1000, cancellationToken)));

        return app;
    }

    private static string? InferFormat(IEnumerable<ISourceReader> readers, string fileName)
    {
        var extension = Path.GetExtension(fileName).TrimStart('.');
        if (extension.Length == 0)
        {
            return null;
        }

        return readers
            .FirstOrDefault(r => r.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            ?.Format;
    }
}

public sealed record JobView(
    Guid Id,
    string Dataset,
    string Queue,
    string FileName,
    string Format,
    string Status,
    string CorrelationId,
    string? RequestedBy,
    int Attempt,
    int MaxAttempts,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? NextAttemptAt,
    string? Error,
    long RecordsRead,
    long RecordsRejected,
    long RowsInserted,
    long RowsUpdated)
{
    public static JobView From(ImportJob job) => new(
        job.Id, job.Dataset, job.Queue, job.FileName, job.Format, job.Status.ToString(),
        job.CorrelationId, job.RequestedBy, job.Attempt, job.MaxAttempts,
        job.CreatedAt, job.StartedAt, job.CompletedAt, job.NextAttemptAt, job.Error,
        job.RecordsRead, job.RecordsRejected, job.RowsInserted, job.RowsUpdated);
}
