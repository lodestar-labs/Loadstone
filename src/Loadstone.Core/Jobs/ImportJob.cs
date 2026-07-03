namespace Loadstone.Jobs;

public enum ImportJobStatus
{
    Pending,
    Processing,
    Succeeded,

    /// <summary>Accepted records were written, but some records were rejected.</summary>
    CompletedWithRejections,

    /// <summary>Attempt failed; will be retried until attempts are exhausted.</summary>
    Failed,

    /// <summary>Attempts exhausted; requires operator intervention.</summary>
    DeadLettered,
}

/// <summary>A queued unit of work: one uploaded file imported into one dataset.</summary>
public sealed class ImportJob
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required string Dataset { get; set; }

    public required string Queue { get; set; }

    /// <summary>Original client file name, for humans.</summary>
    public required string FileName { get; set; }

    /// <summary>Reference into the file store (path, blob name, ...).</summary>
    public required string FileReference { get; set; }

    /// <summary>Source format key: "xml", "json", "csv".</summary>
    public required string Format { get; set; }

    public string? RequestedBy { get; set; }

    /// <summary>Flows through logs, traces, and job events for end-to-end correlation.</summary>
    public string CorrelationId { get; set; } = Guid.NewGuid().ToString("n");

    public ImportJobStatus Status { get; set; } = ImportJobStatus.Pending;

    public int Attempt { get; set; }

    public int MaxAttempts { get; set; } = 3;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? NextAttemptAt { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public string? Error { get; set; }

    public long RecordsRead { get; set; }

    public long RecordsRejected { get; set; }

    public long RowsInserted { get; set; }

    public long RowsUpdated { get; set; }
}
