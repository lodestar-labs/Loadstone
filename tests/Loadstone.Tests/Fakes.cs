using Loadstone.Files;
using Loadstone.Jobs;
using Loadstone.Lookups;
using Loadstone.Pipeline;
using Loadstone.Records;
using Loadstone.Writers;

namespace Loadstone.Tests;

internal sealed class FakeLookupProvider : ILookupProvider
{
    private readonly Dictionary<string, Dictionary<string, object?>> _lists;

    public List<string> Created { get; } = [];

    public bool SupportsCreate { get; set; }

    public FakeLookupProvider(Dictionary<string, Dictionary<string, object?>> lists) => _lists = lists;

    public string Key => "codelist";

    public ValueTask<LookupResult> ResolveAsync(string list, string value, bool caseInsensitive, CancellationToken cancellationToken = default)
    {
        if (_lists.TryGetValue(list, out var entries))
        {
            var match = entries.FirstOrDefault(e => string.Equals(
                e.Key, value, caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
            if (match.Key is not null)
            {
                return ValueTask.FromResult(LookupResult.Hit(match.Value));
            }
        }

        return ValueTask.FromResult(LookupResult.Miss);
    }

    public ValueTask<object?> CreateAsync(string list, string value, CancellationToken cancellationToken = default)
    {
        if (!SupportsCreate)
        {
            throw new NotSupportedException("Creation disabled.");
        }

        Created.Add(value);
        var id = 1000 + Created.Count;
        _lists.TryAdd(list, []);
        _lists[list][value] = id;
        return ValueTask.FromResult<object?>(id);
    }
}

internal sealed class CollectingWriter : IImportWriter
{
    public List<DataRecord> Roots { get; } = [];

    public Task<WriteResult> WriteAsync(
        ImportContext context,
        IAsyncEnumerable<DataRecord> acceptedRoots,
        CancellationToken cancellationToken = default) =>
        Collect(acceptedRoots, cancellationToken);

    private async Task<WriteResult> Collect(IAsyncEnumerable<DataRecord> roots, CancellationToken cancellationToken)
    {
        long total = 0;
        await foreach (var root in roots.WithCancellation(cancellationToken))
        {
            Roots.Add(root);
            total += root.SelfAndDescendants().Count();
        }

        return new WriteResult(total, 0);
    }
}

internal sealed class InMemoryJobStore : IImportJobStore
{
    public List<JobEvent> Events { get; } = [];

    public List<RejectedRow> Rejections { get; } = [];

    public Task<ImportJob?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult<ImportJob?>(null);

    public Task<IReadOnlyList<ImportJob>> ListAsync(
        string? dataset = null, ImportJobStatus? status = null, int top = 100, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ImportJob>>([]);

    public Task AddEventAsync(JobEvent jobEvent, CancellationToken cancellationToken = default)
    {
        Events.Add(jobEvent);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<JobEvent>> GetEventsAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<JobEvent>>([.. Events.Where(e => e.JobId == jobId)]);

    public Task AddRejectedRowsAsync(IReadOnlyList<RejectedRow> rows, CancellationToken cancellationToken = default)
    {
        Rejections.AddRange(rows);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RejectedRow>> GetRejectedRowsAsync(Guid jobId, int top = 1000, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RejectedRow>>([.. Rejections.Where(r => r.JobId == jobId).Take(top)]);
}

internal sealed class SingleFileStore(byte[] content) : IFileStore
{
    public Task<string> SaveAsync(Stream stream, string fileName, CancellationToken cancellationToken = default) =>
        Task.FromResult("stored");

    public Task<Stream> OpenReadAsync(string fileReference, CancellationToken cancellationToken = default) =>
        Task.FromResult<Stream>(new MemoryStream(content));

    public Task DeleteAsync(string fileReference, CancellationToken cancellationToken = default)
    {
        Deleted = true;
        return Task.CompletedTask;
    }

    public bool Deleted { get; private set; }
}
