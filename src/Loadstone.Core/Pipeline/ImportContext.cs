using Loadstone.Jobs;
using Loadstone.Manifests;

namespace Loadstone.Pipeline;

/// <summary>Per-job state shared by every pipeline step and the writer.</summary>
public sealed class ImportContext
{
    public required ImportJob Job { get; init; }

    public required DatasetManifest Manifest { get; init; }

    private long _recordsRead;
    private long _recordsRejected;

    public long RecordsRead => Interlocked.Read(ref _recordsRead);

    public long RecordsRejected => Interlocked.Read(ref _recordsRejected);

    public void IncrementRecordsRead() => Interlocked.Increment(ref _recordsRead);

    public void AddRecordsRejected(long count) => Interlocked.Add(ref _recordsRejected, count);
}
