using System.Collections.Concurrent;
using System.Diagnostics;
using Loadstone.Jobs;
using Loadstone.Runtime.Datasets;
using Loadstone.Runtime.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Loadstone.Runtime.Workers;

/// <summary>
/// Hosts one polling loop per queue (datasets get their own queue unless they share one by
/// name). New queues are picked up as datasets are registered at runtime. Failed attempts
/// are retried with backoff by the queue; jobs abandoned by crashed workers are reclaimed
/// periodically.
/// </summary>
public sealed class QueueWorkerService(
    IServiceScopeFactory scopeFactory,
    IDatasetRegistry registry,
    IImportJobQueue queue,
    IOptions<LoadstoneOptions> options,
    ILogger<QueueWorkerService> logger) : BackgroundService
{
    private static readonly TimeSpan QueueScanInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReclaimInterval = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, Task> _queueLoops = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reclaimLoop = ReclaimAbandonedLoopAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var manifest in registry.All)
            {
                var queueName = manifest.QueueName;
                var concurrency = Math.Max(1, manifest.Queue.Concurrency);
                for (var slot = 0; slot < concurrency; slot++)
                {
                    // Only start a loop when the slot has none: passing the call into
                    // TryAdd would start a runaway duplicate loop on every scan.
                    var loopKey = $"{queueName}#{slot}";
                    if (!_queueLoops.ContainsKey(loopKey))
                    {
                        _queueLoops[loopKey] = RunQueueLoopAsync(queueName, stoppingToken);
                    }
                }
            }

            try
            {
                await Task.Delay(QueueScanInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        await Task.WhenAll([.. _queueLoops.Values, reclaimLoop]);
    }

    private async Task RunQueueLoopAsync(string queueName, CancellationToken stoppingToken)
    {
        logger.LogInformation("Queue worker started for queue {Queue}", queueName);
        while (!stoppingToken.IsCancellationRequested)
        {
            ImportJob? job = null;
            try
            {
                job = await queue.TryClaimAsync(queueName, stoppingToken);
                if (job is null)
                {
                    await Task.Delay(options.Value.QueuePollInterval, stoppingToken);
                    continue;
                }

                await ProcessAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Claim or infrastructure failure: log and keep the loop alive.
                logger.LogError(ex, "Queue {Queue} worker error (job {JobId})", queueName, job?.Id);
                try
                {
                    await Task.Delay(options.Value.QueuePollInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task ProcessAsync(ImportJob job, CancellationToken stoppingToken)
    {
        using var activity = LoadstoneDiagnostics.ActivitySource.StartActivity("loadstone.import");
        activity?.SetTag("loadstone.dataset", job.Dataset);
        activity?.SetTag("loadstone.job_id", job.Id);
        activity?.SetTag("loadstone.correlation_id", job.CorrelationId);
        activity?.SetTag("loadstone.format", job.Format);
        activity?.SetTag("loadstone.attempt", job.Attempt);

        using var logScope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["JobId"] = job.Id,
            ["Dataset"] = job.Dataset,
            ["CorrelationId"] = job.CorrelationId,
        });

        var stopwatch = Stopwatch.StartNew();
        string outcomeTag;
        using var heartbeatStopper = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var heartbeat = PulseHeartbeatAsync(job.Id, heartbeatStopper.Token);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var engine = scope.ServiceProvider.GetRequiredService<ImportEngine>();
            var outcome = await engine.RunAsync(job, stoppingToken);

            job.RecordsRead = outcome.RecordsRead;
            job.RecordsRejected = outcome.RecordsRejected;
            job.RowsInserted = outcome.RowsInserted;
            job.RowsUpdated = outcome.RowsUpdated;
            job.Status = outcome.RecordsRejected > 0
                ? ImportJobStatus.CompletedWithRejections
                : ImportJobStatus.Succeeded;
            await queue.CompleteAsync(job, stoppingToken);
            outcomeTag = job.Status.ToString();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown: hand the job back immediately (without consuming an
            // attempt) instead of leaving it Processing until the reclaim timeout.
            try
            {
                await queue.ReleaseAsync(job, CancellationToken.None);
            }
            catch (Exception releaseEx)
            {
                logger.LogWarning(releaseEx, "Could not release job {JobId} during shutdown; reclaim will recover it", job.Id);
            }

            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Import job {JobId} attempt {Attempt} failed", job.Id, job.Attempt);
            await queue.FailAsync(job, ex.Message, CancellationToken.None);
            outcomeTag = job.Status == ImportJobStatus.DeadLettered ? "DeadLettered" : "Failed";
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        }
        finally
        {
            heartbeatStopper.Cancel();
            await heartbeat;
        }

        stopwatch.Stop();
        var tags = new TagList
        {
            { "dataset", job.Dataset },
            { "outcome", outcomeTag },
        };
        LoadstoneDiagnostics.JobsCompleted.Add(1, tags);
        LoadstoneDiagnostics.JobDuration.Record(stopwatch.Elapsed.TotalSeconds, tags);
        activity?.SetTag("loadstone.outcome", outcomeTag);
    }

    private async Task PulseHeartbeatAsync(Guid jobId, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(60), cancellationToken);
                await queue.HeartbeatAsync(jobId, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // A missed pulse is harmless as long as the reclaim timeout is generous.
                logger.LogDebug(ex, "Heartbeat for job {JobId} failed", jobId);
            }
        }
    }

    private async Task ReclaimAbandonedLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var reclaimed = await queue.ReclaimAbandonedAsync(options.Value.AbandonedJobTimeout, stoppingToken);
                if (reclaimed > 0)
                {
                    logger.LogWarning("Requeued {Count} abandoned import jobs", reclaimed);
                }

                await Task.Delay(ReclaimInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to reclaim abandoned jobs");
                try
                {
                    await Task.Delay(ReclaimInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}
