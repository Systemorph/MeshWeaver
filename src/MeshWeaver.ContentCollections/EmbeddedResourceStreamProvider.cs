using System.Collections.Immutable;
using System.Reflection;

namespace MeshWeaver.ContentCollections;

public class EmbeddedResourceStreamProvider(Assembly assembly, string basePath) : IStreamProvider
{
    private readonly string basePath = basePath.EndsWith('.') ? basePath : basePath + '.';
    private ImmutableDictionary<string, string> resourcePaths = ImmutableDictionary<string, string>.Empty;
    private bool initialized;

    public string ProviderType => "EmbeddedResource";

    private void EnsureInitialized()
    {
        if (initialized) return;

        resourcePaths = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
            .ToImmutableDictionary(
                ExtractPath,
                name => name
            );
        initialized = true;
    }

    private string ExtractPath(string resourceName)
    {
        var withoutPrefix = resourceName[basePath.Length..];
        var lastDotIndex = withoutPrefix.LastIndexOf('.');
        if (lastDotIndex > 0)
        {
            var nameWithoutExtension = withoutPrefix[..lastDotIndex].Replace('.', '/');
            var extension = withoutPrefix[lastDotIndex..];
            return nameWithoutExtension + extension;
        }
        return withoutPrefix.Replace('.', '/');
    }

    public string? GetResourceName(string path)
    {
        EnsureInitialized();
        var normalizedPath = path.TrimStart('/');

        // Try direct lookup first
        if (resourcePaths.TryGetValue(normalizedPath, out var resourceName))
        {
            return resourceName;
        }

        // Try with .md extension if not found
        if (!normalizedPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            var pathWithMd = normalizedPath;
            if (resourcePaths.TryGetValue(pathWithMd, out resourceName))
            {
                return resourceName;
            }
        }

        return null;
    }

    public Task<Stream?> GetStreamAsync(string reference, CancellationToken cancellationToken = default)
    {
        // Convert path (with /) to resource name (with .)
        var resourceName = PathToResourceName(reference);
        var stream = assembly.GetManifestResourceStream(resourceName);
        return Task.FromResult<Stream?>(stream);
    }

    private string PathToResourceName(string path)
    {
        // Remove leading slash and convert / to .
        var normalizedPath = path.TrimStart('/').Replace('/', '.');
        // Concatenate with base path
        return basePath + normalizedPath;
    }

    public Task<(Stream? Stream, string Path, DateTime LastModified)> GetStreamWithMetadataAsync(string path, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        var normalizedPath = path.TrimStart('/');

        // Try direct lookup first
        if (resourcePaths.TryGetValue(normalizedPath, out var resourceName))
        {
            var stream = assembly.GetManifestResourceStream(resourceName);
            return Task.FromResult<(Stream? Stream, string Path, DateTime LastModified)>(
                (stream, path, DateTime.UtcNow));
        }

        // Try with .md extension if not found
        if (!normalizedPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            var pathWithMd = normalizedPath;
            if (resourcePaths.TryGetValue(pathWithMd, out resourceName))
            {
                var stream = assembly.GetManifestResourceStream(resourceName);
                return Task.FromResult<(Stream? Stream, string Path, DateTime LastModified)>(
                    (stream, path, DateTime.UtcNow));
            }
        }

        return Task.FromResult<(Stream? Stream, string Path, DateTime LastModified)>(default);
    }

    public Task WriteStreamAsync(string reference, Stream content, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Embedded resource collections are read-only");
    }

    public IAsyncEnumerable<(Stream? Stream, string Path, DateTime LastModified)> GetStreamsAsync(Func<string, bool> filter, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        return resourcePaths.Keys
            .Where(filter)
            .Select(path =>
            {
                if (resourcePaths.TryGetValue(path, out var resourceName))
                {
                    var stream = assembly.GetManifestResourceStream(resourceName);
                    return (stream, path, DateTime.UtcNow);
                }
                return default;
            })
            .ToAsyncEnumerable();
    }

    public Task<IReadOnlyCollection<FolderItem>> GetFoldersAsync(string path)
    {
        EnsureInitialized();
        var normalizedPath = path.TrimStart('/').Replace('/', '.');
        var folders = resourcePaths.Keys
            .Where(p => p.StartsWith(normalizedPath, StringComparison.OrdinalIgnoreCase) && p != normalizedPath)
            .Select(p => p[(normalizedPath.Length == 0 ? 0 : normalizedPath.Length + 1)..])
            .Where(p => p.Contains('.'))
            .Select(p => p[..p.IndexOf('.')])
            .Distinct()
            .Select(name => new FolderItem($"{path.TrimEnd('/')}/{name}", name, 0))
            .ToArray();

        return Task.FromResult<IReadOnlyCollection<FolderItem>>(folders);
    }

    public Task<IReadOnlyCollection<FileItem>> GetFilesAsync(string path)
    {
        EnsureInitialized();
        var normalizedPath = path.TrimStart('/').Replace('/', '.');
        var files = resourcePaths.Keys
            .Where(p => p.StartsWith(normalizedPath, StringComparison.OrdinalIgnoreCase))
            .Where(p =>
            {
                var relativePath = p[(normalizedPath.Length == 0 ? 0 : normalizedPath.Length + 1)..];
                return !relativePath.Contains('.') || relativePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
            })
            .Select(p =>
            {
                var fileName = p.Split('.').Last();
                if (p.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                {
                    fileName += ".md";
                }
                return new FileItem($"{path.TrimEnd('/')}/{fileName}", fileName, DateTime.UtcNow);
            })
            .ToArray();

        return Task.FromResult<IReadOnlyCollection<FileItem>>(files);
    }

    public Task SaveFileAsync(string path, string fileName, Stream content, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Embedded resource collections are read-only");
    }

    public Task CreateFolderAsync(string folderPath)
    {
        throw new NotSupportedException("Embedded resource collections are read-only");
    }

    public Task DeleteFolderAsync(string folderPath)
    {
        throw new NotSupportedException("Embedded resource collections are read-only");
    }

    public Task DeleteFileAsync(string filePath)
    {
        throw new NotSupportedException("Embedded resource collections are read-only");
    }

    public IDisposable? AttachMonitor(Action<string> onChanged)
    {
        // Embedded resources don't change, so no monitoring needed
        return null;
    }

    public async Task<ImmutableDictionary<string, Author>> LoadAuthorsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            EnsureInitialized();
            if (resourcePaths.TryGetValue("authors.json", out var resourceName))
            {
                var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream != null)
                {
                    using var reader = new StreamReader(stream);
                    var content = await reader.ReadToEndAsync(cancellationToken);
                    var authors = System.Text.Json.JsonSerializer.Deserialize<ImmutableDictionary<string, Author>>(content);
                    return authors ?? ImmutableDictionary<string, Author>.Empty;
                }
            }
        }
        catch
        {
            // If authors.json doesn't exist or can't be parsed, return empty
        }
        return ImmutableDictionary<string, Author>.Empty;
    }
}
