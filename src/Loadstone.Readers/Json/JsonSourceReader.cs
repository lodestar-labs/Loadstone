using System.Runtime.CompilerServices;
using System.Text.Json;
using Loadstone.Manifests;
using Loadstone.Records;
using Loadstone.Sources;

namespace Loadstone.Readers.Json;

/// <summary>
/// JSON reader. A top-level array on a seekable stream is streamed element by element, so
/// arbitrarily large array files import with bounded memory. A top-level object is also
/// accepted when it wraps the record array in a property (source.json.rootProperty, or a
/// property named after the root entity) — but that path parses the whole document, so
/// very large exports should prefer a top-level array. Nested arrays/objects matching
/// child entity names become children; properties matching fields become raw values;
/// everything else is ignored.
/// </summary>
public sealed class JsonSourceReader : ISourceReader
{
    public string Format => "json";

    public IReadOnlyList<string> Extensions { get; } = ["json"];

    public async IAsyncEnumerable<DataRecord> ReadAsync(
        Stream stream,
        DatasetManifest manifest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!stream.CanSeek)
        {
            var buffered = new MemoryStream();
            await stream.CopyToAsync(buffered, cancellationToken);
            buffered.Position = 0;
            stream = buffered;
        }

        var firstByte = await PeekFirstTokenByteAsync(stream, cancellationToken);
        var root = manifest.Root;
        var index = 0;

        if (firstByte == (byte)'[')
        {
            // Deferred enumerable: malformed JSON surfaces during iteration, which Guard converts.
            var elements = JsonSerializer.DeserializeAsyncEnumerable<JsonElement?>(
                stream, cancellationToken: cancellationToken);

            await foreach (var element in Guard(elements, cancellationToken))
            {
                if (element is { ValueKind: JsonValueKind.Object } item)
                {
                    yield return BuildRecord(item, root, $"$[{index}]");
                }

                index++;
            }

            yield break;
        }

        if (firstByte != (byte)'{')
        {
            throw new SourceFormatException("JSON document must be an array of records or an object wrapping one.");
        }

        using var document = await ParseDocumentAsync(stream, cancellationToken);
        var records = LocateRecordArray(document.RootElement, manifest);
        foreach (var item in records.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.ValueKind == JsonValueKind.Object)
            {
                yield return BuildRecord(item, root, $"$[{index}]");
            }

            index++;
        }
    }

    private static DataRecord BuildRecord(JsonElement element, EntityDefinition entity, string path)
    {
        var record = new DataRecord
        {
            Entity = entity,
            Location = new SourceLocation(null, path),
        };

        foreach (var property in element.EnumerateObject())
        {
            if (entity.FindChild(property.Name) is { } childEntity)
            {
                AddChildren(record, property.Value, childEntity, $"{path}.{property.Name}");
            }
            else if (entity.FindField(property.Name) is { Source: not FieldSource.Constant } field)
            {
                record.Raw[field.Name] = ReadScalar(property.Value);
            }
        }

        return record;
    }

    private static void AddChildren(DataRecord parent, JsonElement value, EntityDefinition childEntity, string path)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object)
                    {
                        parent.Children.Add(BuildRecord(item, childEntity, $"{path}[{index}]"));
                    }

                    index++;
                }

                break;

            case JsonValueKind.Object:
                parent.Children.Add(BuildRecord(value, childEntity, path));
                break;
        }
    }

    private static string? ReadScalar(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => value.GetRawText(),
        };

    private static JsonElement LocateRecordArray(JsonElement rootObject, DatasetManifest manifest)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(manifest.Source.Json.RootProperty))
        {
            candidates.Add(manifest.Source.Json.RootProperty!);
        }

        candidates.Add(manifest.Root.Name);

        foreach (var name in candidates)
        {
            foreach (var property in rootObject.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.Array)
                {
                    return property.Value;
                }
            }
        }

        throw new SourceFormatException(
            $"Could not find the record array: expected a '{string.Join("' or '", candidates)}' array property on the top-level object.");
    }

    private static async Task<byte> PeekFirstTokenByteAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        while (await stream.ReadAsync(buffer, cancellationToken) == 1)
        {
            var b = buffer[0];
            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            {
                continue;
            }

            // Skip a UTF-8 BOM if present.
            if (b == 0xEF)
            {
                await stream.ReadExactlyAsync(new byte[2], cancellationToken);
                continue;
            }

            stream.Seek(-1, SeekOrigin.Current);
            return b;
        }

        throw new SourceFormatException("JSON document is empty.");
    }

    private static async Task<JsonDocument> ParseDocumentAsync(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new SourceFormatException($"Malformed JSON: {ex.Message}", ex);
        }
    }

    private static async IAsyncEnumerable<JsonElement?> Guard(
        IAsyncEnumerable<JsonElement?> source,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var enumerator = source.GetAsyncEnumerator(cancellationToken);
        await using (enumerator.ConfigureAwait(false))
        {
            while (true)
            {
                JsonElement? current;
                try
                {
                    if (!await enumerator.MoveNextAsync())
                    {
                        yield break;
                    }

                    current = enumerator.Current;
                }
                catch (JsonException ex)
                {
                    throw new SourceFormatException($"Malformed JSON: {ex.Message}", ex);
                }

                yield return current;
            }
        }
    }
}
