namespace Loadstone.Files;

/// <summary>
/// Abstraction over uploaded-file storage. The default implementation is a local
/// directory; an Azure Blob implementation swaps in behind the same contract without
/// touching the pipeline.
/// </summary>
public interface IFileStore
{
    /// <summary>Persists content and returns an opaque reference used to read it back.</summary>
    Task<string> SaveAsync(Stream content, string fileName, CancellationToken cancellationToken = default);

    Task<Stream> OpenReadAsync(string fileReference, CancellationToken cancellationToken = default);

    Task DeleteAsync(string fileReference, CancellationToken cancellationToken = default);
}
