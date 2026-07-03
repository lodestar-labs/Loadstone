using Loadstone.Manifests;
using Loadstone.Records;

namespace Loadstone.Sources;

/// <summary>
/// Streams root-entity records (with their full child subtrees) out of a source document.
/// Implementations must stream — memory use is bounded by one root subtree, never the file.
/// </summary>
public interface ISourceReader
{
    /// <summary>Format key, e.g. "xml", "json", "csv".</summary>
    string Format { get; }

    /// <summary>File extensions (without dot) this reader accepts, for format inference.</summary>
    IReadOnlyList<string> Extensions { get; }

    IAsyncEnumerable<DataRecord> ReadAsync(Stream stream, DatasetManifest manifest, CancellationToken cancellationToken = default);
}

/// <summary>Raised when a source document cannot be read at all (as opposed to row-level issues).</summary>
public sealed class SourceFormatException : Exception
{
    public SourceFormatException(string message) : base(message)
    {
    }

    public SourceFormatException(string message, Exception inner) : base(message, inner)
    {
    }
}
