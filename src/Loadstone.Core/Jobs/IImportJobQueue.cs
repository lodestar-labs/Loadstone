namespace Loadstone.Jobs;

/// <summary>
/// Durable, named queues of import jobs — one queue per dataset by default. The default
/// implementation is database-backed (no extra infrastructure, survives restarts); cloud
/// transports (Azure Storage Queues, Service Bus) implement the same contract.
/// </summary>
public interface IImportJobQueue
{
    Task EnqueueAsync(ImportJob job, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically claims the next due job on <paramref name="queue"/>, marking it Processing.
    /// Returns null when the queue is empty. Safe to call from competing workers.
    /// </summary>
    Task<ImportJob?> TryClaimAsync(string queue, CancellationToken cancellationToken = default);

    /// <summary>Persists the terminal state of a finished attempt (Succeeded/CompletedWithRejections).</summary>
    Task CompleteAsync(ImportJob job, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a failed attempt: schedules a retry with exponential backoff, or dead-letters
    /// the job when attempts are exhausted.
    /// </summary>
    Task FailAsync(ImportJob job, string error, CancellationToken cancellationToken = default);

    /// <summary>Requeues jobs stuck in Processing longer than <paramref name="olderThan"/> (crashed workers).</summary>
    Task<int> ReclaimAbandonedAsync(TimeSpan olderThan, CancellationToken cancellationToken = default);
}
