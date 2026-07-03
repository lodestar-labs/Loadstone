using Loadstone.Records;

namespace Loadstone.Pipeline;

/// <summary>
/// A pluggable processing stage applied to every root record between parsing and writing.
/// Built-in steps validate fields and resolve lookups; register additional implementations
/// in DI to enrich, transform, or veto records for any dataset.
/// Steps mark problems via <see cref="DataRecord.AddError"/> — records whose subtree carries
/// errors are diverted to the rejection store instead of the database.
/// </summary>
public interface IImportStep
{
    string Name { get; }

    /// <summary>Steps run in ascending order. Built-ins: Validate=100, Lookups=200. Custom default: 500.</summary>
    int Order => 500;

    /// <summary>Return false to skip this step for a dataset. Default: applies to all datasets.</summary>
    bool AppliesTo(Manifests.DatasetManifest manifest) => true;

    ValueTask ExecuteAsync(ImportContext context, DataRecord root, CancellationToken cancellationToken = default);
}
