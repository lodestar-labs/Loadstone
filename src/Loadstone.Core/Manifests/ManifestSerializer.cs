using System.Text.Json;
using System.Text.Json.Serialization;

namespace Loadstone.Manifests;

/// <summary>Canonical JSON (de)serialization for dataset manifests.</summary>
public static class ManifestSerializer
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static DatasetManifest Deserialize(string json)
    {
        var manifest = JsonSerializer.Deserialize<DatasetManifest>(json, Options)
            ?? throw new ManifestException("Manifest document is empty.");
        ThrowIfInvalid(manifest);
        return manifest;
    }

    public static async Task<DatasetManifest> DeserializeAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var manifest = await JsonSerializer.DeserializeAsync<DatasetManifest>(stream, Options, cancellationToken)
            ?? throw new ManifestException("Manifest document is empty.");
        ThrowIfInvalid(manifest);
        return manifest;
    }

    public static string Serialize(DatasetManifest manifest) => JsonSerializer.Serialize(manifest, Options);

    private static void ThrowIfInvalid(DatasetManifest manifest)
    {
        var errors = manifest.Validate();
        if (errors.Count > 0)
        {
            throw new ManifestException(
                $"Manifest '{manifest.Name}' is invalid:{Environment.NewLine}- {string.Join($"{Environment.NewLine}- ", errors)}");
        }
    }
}

public sealed class ManifestException(string message) : Exception(message);
