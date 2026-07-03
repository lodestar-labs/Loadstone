using Loadstone.Files;
using Microsoft.Extensions.Options;

namespace Loadstone.Runtime.Files;

/// <summary>
/// Default file store: a local directory (on Azure App Service, point it at the persistent
/// <c>%HOME%\data</c> share or swap in a blob-backed store). References are file names, never
/// full paths, so stored data can move between hosts.
/// </summary>
public sealed class LocalFileStore(IOptions<LoadstoneOptions> options) : IFileStore
{
    private readonly string _root = options.Value.FileStorePath;

    public async Task<string> SaveAsync(Stream content, string fileName, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_root);
        var reference = $"{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():n}{SafeExtension(fileName)}";
        await using var target = File.Create(Path.Combine(_root, reference));
        await content.CopyToAsync(target, cancellationToken);
        return reference;
    }

    public Task<Stream> OpenReadAsync(string fileReference, CancellationToken cancellationToken = default)
    {
        var path = Resolve(fileReference);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Stored file '{fileReference}' was not found.", path);
        }

        return Task.FromResult<Stream>(File.OpenRead(path));
    }

    public Task DeleteAsync(string fileReference, CancellationToken cancellationToken = default)
    {
        var path = Resolve(fileReference);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private string Resolve(string fileReference)
    {
        var path = Path.GetFullPath(Path.Combine(_root, fileReference));
        // Boundary includes the separator so sibling directories with the store's name
        // as a prefix (e.g. "imports-other") never pass the check.
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_root)) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("File reference must not escape the file store.", nameof(fileReference));
        }

        return path;
    }

    private static string SafeExtension(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return extension.Length is > 0 and <= 10 && extension.Skip(1).All(char.IsLetterOrDigit)
            ? extension.ToLowerInvariant()
            : string.Empty;
    }
}
