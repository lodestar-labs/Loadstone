using System.Runtime.CompilerServices;
using System.Xml;
using Loadstone.Manifests;
using Loadstone.Records;
using Loadstone.Sources;

namespace Loadstone.Readers.Xml;

/// <summary>
/// Streaming XML reader: walks the document with <see cref="XmlReader"/>, materializing one
/// root-entity subtree at a time. Elements matching child entities recurse; elements matching
/// fields are captured as raw values; anything else is skipped, so documents may carry extra
/// content without breaking the import. Line numbers are preserved for diagnostics.
/// </summary>
public sealed class XmlSourceReader : ISourceReader
{
    public string Format => "xml";

    public IReadOnlyList<string> Extensions { get; } = ["xml"];

    public async IAsyncEnumerable<DataRecord> ReadAsync(
        Stream stream,
        DatasetManifest manifest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var settings = new XmlReaderSettings
        {
            Async = true,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = true,
            DtdProcessing = DtdProcessing.Prohibit,
            CloseInput = false,
        };

        using var reader = XmlReader.Create(stream, settings);
        var root = manifest.Root;
        var wrapper = manifest.Source.Xml.RootElement;
        var insideWrapper = wrapper is null;
        var wrapperDepth = -1;
        bool readNext = true;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (readNext && !await ReadOrThrowAsync(reader))
            {
                break;
            }

            readNext = true;
            if (wrapper is not null)
            {
                if (!insideWrapper
                    && reader.NodeType == XmlNodeType.Element
                    && string.Equals(reader.LocalName, wrapper, StringComparison.OrdinalIgnoreCase))
                {
                    insideWrapper = true;
                    wrapperDepth = reader.Depth;
                    continue;
                }

                if (insideWrapper
                    && reader.NodeType == XmlNodeType.EndElement
                    && reader.Depth == wrapperDepth
                    && string.Equals(reader.LocalName, wrapper, StringComparison.OrdinalIgnoreCase))
                {
                    insideWrapper = false;
                    continue;
                }
            }

            if (insideWrapper
                && reader.NodeType == XmlNodeType.Element
                && string.Equals(reader.LocalName, root.Name, StringComparison.OrdinalIgnoreCase))
            {
                var record = await ReadEntityAsync(reader, root, parentPath: null, cancellationToken);
                // ReadEntityAsync leaves the reader on the node after the entity's subtree.
                readNext = false;
                yield return record;
            }
        }
    }

    private static async Task<DataRecord> ReadEntityAsync(
        XmlReader reader,
        EntityDefinition entity,
        string? parentPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lineInfo = reader as IXmlLineInfo;
        long? line = lineInfo?.HasLineInfo() == true ? lineInfo.LineNumber : null;
        var path = parentPath is null ? entity.Name : $"{parentPath}/{entity.Name}";
        var record = new DataRecord
        {
            Entity = entity,
            Location = new SourceLocation(line, path),
        };

        if (reader.HasAttributes)
        {
            while (reader.MoveToNextAttribute())
            {
                if (entity.FindField(reader.LocalName) is { Source: FieldSource.Attribute } field)
                {
                    record.Raw[field.Name] = reader.Value;
                }
            }

            reader.MoveToElement();
        }

        if (reader.IsEmptyElement)
        {
            await ReadOrThrowAsync(reader);
            return record;
        }

        var entityDepth = reader.Depth;
        await ReadOrThrowAsync(reader);
        while (!(reader.NodeType == XmlNodeType.EndElement && reader.Depth == entityDepth))
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                var name = reader.LocalName;
                if (entity.FindChild(name) is { } childEntity)
                {
                    record.Children.Add(await ReadEntityAsync(reader, childEntity, path, cancellationToken));
                }
                else if (entity.FindField(name) is { Source: FieldSource.Element } field)
                {
                    try
                    {
                        record.Raw[field.Name] = await reader.ReadElementContentAsStringAsync();
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or XmlException)
                    {
                        throw new SourceFormatException(
                            $"Element '{name}' at {record.Location} maps to field '{field.Name}' but does not contain simple text content.",
                            ex);
                    }
                }
                else
                {
                    await reader.SkipAsync();
                }
            }
            else if (!await ReadOrThrowAsync(reader))
            {
                throw new SourceFormatException($"Unexpected end of document inside element '{entity.Name}'.");
            }
        }

        await ReadOrThrowAsync(reader);
        return record;
    }

    private static async Task<bool> ReadOrThrowAsync(XmlReader reader)
    {
        try
        {
            return await reader.ReadAsync();
        }
        catch (XmlException ex)
        {
            throw new SourceFormatException($"Malformed XML at line {ex.LineNumber}: {ex.Message}", ex);
        }
    }
}
