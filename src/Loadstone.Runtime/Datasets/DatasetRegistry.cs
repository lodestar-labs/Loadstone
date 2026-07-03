using System.Collections.Concurrent;
using Loadstone.Manifests;

namespace Loadstone.Runtime.Datasets;

/// <summary>Live catalog of registered datasets. Thread-safe; updated at runtime through the API.</summary>
public interface IDatasetRegistry
{
    void Register(DatasetManifest manifest);

    bool Remove(string name);

    DatasetManifest? Find(string name);

    IReadOnlyList<DatasetManifest> All { get; }

    DatasetManifest GetRequired(string name) =>
        Find(name) ?? throw new KeyNotFoundException($"Dataset '{name}' is not registered.");
}

public sealed class DatasetRegistry : IDatasetRegistry
{
    private readonly ConcurrentDictionary<string, DatasetManifest> _datasets = new(StringComparer.OrdinalIgnoreCase);

    public void Register(DatasetManifest manifest)
    {
        var errors = manifest.Validate();
        if (errors.Count > 0)
        {
            throw new ManifestException(
                $"Manifest '{manifest.Name}' is invalid:{Environment.NewLine}- {string.Join($"{Environment.NewLine}- ", errors)}");
        }

        _datasets[manifest.Name] = manifest;
    }

    public bool Remove(string name) => _datasets.TryRemove(name, out _);

    public DatasetManifest? Find(string name) =>
        _datasets.TryGetValue(name, out var manifest) ? manifest : null;

    public IReadOnlyList<DatasetManifest> All => [.. _datasets.Values.OrderBy(m => m.Name)];
}
