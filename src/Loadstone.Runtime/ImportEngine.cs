using System.Diagnostics;
using System.Runtime.CompilerServices;
using Loadstone.Files;
using Loadstone.Jobs;
using Loadstone.Pipeline;
using Loadstone.Records;
using Loadstone.Runtime.Datasets;
using Loadstone.Runtime.Diagnostics;
using Loadstone.Sources;
using Loadstone.Writers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Loadstone.Runtime;

/// <summary>
/// Runs a single import job end to end: stream records out of the file, run every
/// applicable pipeline step, divert records with errors to the rejection store, and feed
/// the clean ones to the writer — all as one streaming pass, so files of any size import
/// with bounded memory.
/// </summary>
public sealed class ImportEngine(
    IDatasetRegistry registry,
    IEnumerable<ISourceReader> readers,
    IEnumerable<IImportStep> steps,
    IImportWriter writer,
    IImportJobStore jobStore,
    IFileStore fileStore,
    IOptions<LoadstoneOptions> options,
    ILogger<ImportEngine> logger)
{
    private const int RejectionFlushSize = 200;

    public async Task<ImportOutcome> RunAsync(ImportJob job, CancellationToken cancellationToken = default)
    {
        var manifest = registry.GetRequired(job.Dataset);
        var reader = readers.FirstOrDefault(r => string.Equals(r.Format, job.Format, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No source reader is registered for format '{job.Format}'.");

        if (!manifest.Source.AcceptsFormat(job.Format))
        {
            throw new InvalidOperationException(
                $"Dataset '{manifest.Name}' does not accept '{job.Format}' files (allowed: {string.Join(", ", manifest.Source.Formats)}).");
        }

        var context = new ImportContext { Job = job, Manifest = manifest };
        var orderedSteps = steps
            .Where(s => s.AppliesTo(manifest))
            .OrderBy(s => s.Order)
            .ToArray();

        var stopwatch = Stopwatch.StartNew();
        await jobStore.AddEventAsync(
            new JobEvent(job.Id, DateTimeOffset.UtcNow, "start",
                $"Importing '{job.FileName}' ({job.Format}) with steps: {string.Join(" → ", orderedSteps.Select(s => s.Name))}."),
            cancellationToken);

        WriteResult writeResult;
        var pendingRejections = new List<RejectedRow>();
        await using (var stream = await fileStore.OpenReadAsync(job.FileReference, cancellationToken))
        {
            var accepted = ProcessAsync(reader, stream, context, orderedSteps, pendingRejections, cancellationToken);
            writeResult = await writer.WriteAsync(context, accepted, cancellationToken);
        }

        if (pendingRejections.Count > 0)
        {
            await jobStore.AddRejectedRowsAsync(pendingRejections, cancellationToken);
        }

        stopwatch.Stop();
        var outcome = new ImportOutcome(
            context.RecordsRead,
            context.RecordsRejected,
            writeResult.Inserted,
            writeResult.Updated,
            stopwatch.Elapsed);

        RecordMetrics(job, outcome);
        await jobStore.AddEventAsync(
            new JobEvent(job.Id, DateTimeOffset.UtcNow, "complete",
                $"Read {outcome.RecordsRead} records, rejected {outcome.RecordsRejected}, inserted {outcome.RowsInserted}, updated {outcome.RowsUpdated}.",
                stopwatch.Elapsed.TotalMilliseconds),
            cancellationToken);

        if (options.Value.DeleteFilesAfterImport)
        {
            await fileStore.DeleteAsync(job.FileReference, cancellationToken);
        }

        logger.LogInformation(
            "Import {JobId} for dataset {Dataset} finished in {ElapsedMs:F0} ms: {RecordsRead} read, {RecordsRejected} rejected, {RowsInserted} inserted, {RowsUpdated} updated",
            job.Id, job.Dataset, stopwatch.Elapsed.TotalMilliseconds,
            outcome.RecordsRead, outcome.RecordsRejected, outcome.RowsInserted, outcome.RowsUpdated);

        return outcome;
    }

    private async IAsyncEnumerable<DataRecord> ProcessAsync(
        ISourceReader reader,
        Stream stream,
        ImportContext context,
        IImportStep[] orderedSteps,
        List<RejectedRow> pendingRejections,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var root in reader.ReadAsync(stream, context.Manifest, cancellationToken))
        {
            var treeSize = 0;
            foreach (var _ in root.SelfAndDescendants())
            {
                treeSize++;
                context.IncrementRecordsRead();
            }

            foreach (var step in orderedSteps)
            {
                await step.ExecuteAsync(context, root, cancellationToken);
            }

            if (root.TreeHasErrors)
            {
                context.AddRecordsRejected(treeSize);
                CollectRejections(context.Job.Id, root, pendingRejections);
                if (pendingRejections.Count >= RejectionFlushSize)
                {
                    await jobStore.AddRejectedRowsAsync([.. pendingRejections], cancellationToken);
                    pendingRejections.Clear();
                }

                continue;
            }

            yield return root;
        }
    }

    private static void CollectRejections(Guid jobId, DataRecord root, List<RejectedRow> rejections)
    {
        foreach (var record in root.SelfAndDescendants())
        {
            foreach (var issue in record.Issues.Where(i => i.Severity == IssueSeverity.Error))
            {
                rejections.Add(new RejectedRow(
                    jobId,
                    record.Entity.Name,
                    record.Location.Line,
                    record.Location.Path,
                    issue.Field,
                    issue.Message,
                    issue.RawValue ?? LookupRaw(record, issue.Field)));
            }
        }
    }

    private static string? LookupRaw(DataRecord record, string field) =>
        record.Raw.TryGetValue(field, out var raw) ? raw : null;

    private static void RecordMetrics(ImportJob job, ImportOutcome outcome)
    {
        var dataset = new KeyValuePair<string, object?>("dataset", job.Dataset);
        LoadstoneDiagnostics.RecordsRead.Add(outcome.RecordsRead, dataset);
        LoadstoneDiagnostics.RecordsRejected.Add(outcome.RecordsRejected, dataset);
        LoadstoneDiagnostics.RowsWritten.Add(outcome.RowsInserted, dataset, new("operation", "insert"));
        LoadstoneDiagnostics.RowsWritten.Add(outcome.RowsUpdated, dataset, new("operation", "update"));
    }
}

public sealed record ImportOutcome(
    long RecordsRead,
    long RecordsRejected,
    long RowsInserted,
    long RowsUpdated,
    TimeSpan Elapsed);
