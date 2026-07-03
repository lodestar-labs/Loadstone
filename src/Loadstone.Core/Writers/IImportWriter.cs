using Loadstone.Manifests;
using Loadstone.Pipeline;
using Loadstone.Records;

namespace Loadstone.Writers;

/// <summary>
/// Persists accepted record trees into the target relational database. The SQL Server
/// implementation stages batches with bulk copy and merges set-based, level by level;
/// other engines implement the same contract.
/// </summary>
public interface IImportWriter
{
    Task<WriteResult> WriteAsync(
        ImportContext context,
        IAsyncEnumerable<DataRecord> acceptedRoots,
        CancellationToken cancellationToken = default);
}

public sealed record WriteResult(long Inserted, long Updated);

/// <summary>Creates target tables and Loadstone's own system objects.</summary>
public interface ISchemaManager
{
    /// <summary>Creates Loadstone's job/lookup system tables when missing. Idempotent.</summary>
    Task EnsureSystemObjectsAsync(CancellationToken cancellationToken = default);

    /// <summary>DDL for the dataset's target tables, derived from the manifest.</summary>
    string GenerateTargetSchemaScript(DatasetManifest manifest);

    /// <summary>Executes <see cref="GenerateTargetSchemaScript"/> (skipping tables that exist).</summary>
    Task ApplyTargetSchemaAsync(DatasetManifest manifest, CancellationToken cancellationToken = default);
}
