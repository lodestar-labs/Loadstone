namespace Loadstone.Jobs;

/// <summary>Read model and audit trail for import jobs: status, stage timeline, rejected rows.</summary>
public interface IImportJobStore
{
    Task<ImportJob?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ImportJob>> ListAsync(
        string? dataset = null,
        ImportJobStatus? status = null,
        int top = 100,
        CancellationToken cancellationToken = default);

    /// <summary>Appends a stage event to the job timeline (parse started, batch written, ...).</summary>
    Task AddEventAsync(JobEvent jobEvent, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<JobEvent>> GetEventsAsync(Guid jobId, CancellationToken cancellationToken = default);

    Task AddRejectedRowsAsync(IReadOnlyList<RejectedRow> rows, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RejectedRow>> GetRejectedRowsAsync(Guid jobId, int top = 1000, CancellationToken cancellationToken = default);
}

public sealed record JobEvent(
    Guid JobId,
    DateTimeOffset At,
    string Stage,
    string Message,
    double? ElapsedMs = null);

/// <summary>
/// One rejected source record field, with everything needed to fix the source data:
/// entity, source line/path, field, raw value, and the reason.
/// </summary>
public sealed record RejectedRow(
    Guid JobId,
    string Entity,
    long? SourceLine,
    string? SourcePath,
    string Field,
    string Reason,
    string? RawValue);
