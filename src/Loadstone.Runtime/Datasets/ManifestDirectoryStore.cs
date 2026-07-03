using Loadstone.Manifests;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Loadstone.Runtime.Datasets;

/// <summary>Persists dataset manifests so registrations survive restarts.</summary>
public interface IManifestStore
{
    Task<IReadOnlyList<DatasetManifest>> LoadAllAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(DatasetManifest manifest, CancellationToken cancellationToken = default);

    Task DeleteAsync(string name, CancellationToken cancellationToken = default);
}

/// <summary>
/// File-based manifest store: one JSON document per dataset in the configured directory.
/// Manifests are ordinary files, so they can be versioned in git and deployed with the app.
/// </summary>
public sealed class ManifestDirectoryStore(IOptions<LoadstoneOptions> options, ILogger<ManifestDirectoryStore> logger)
    : IManifestStore
{
    private readonly string _directory = options.Value.ManifestDirectory;

    public async Task<IReadOnlyList<DatasetManifest>> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_directory))
        {
            return [];
        }

        var manifests = new List<DatasetManifest>();
        foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = File.OpenRead(file);
                manifests.Add(await ManifestSerializer.DeserializeAsync(stream, cancellationToken));
            }
            catch (ManifestException ex)
            {
                logger.LogError(ex, "Skipping invalid dataset manifest {ManifestFile}", file);
            }
        }

        return manifests;
    }

    public async Task SaveAsync(DatasetManifest manifest, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_directory);
        var path = PathFor(manifest.Name);
        await File.WriteAllTextAsync(path, ManifestSerializer.Serialize(manifest), cancellationToken);
    }

    public Task DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        var path = PathFor(name);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private string PathFor(string name)
    {
        var safe = string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        return Path.Combine(_directory, $"{safe}.json");
    }
}
