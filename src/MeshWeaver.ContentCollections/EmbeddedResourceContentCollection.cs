using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using MeshWeaver.Messaging;

namespace MeshWeaver.ContentCollections;

public class EmbeddedResourceContentCollection : ContentCollection
{
    private readonly Assembly assembly;
    private readonly string resourcePrefix;
    private readonly ImmutableDictionary<string, string> resourcePaths;

    public EmbeddedResourceContentCollection(string collectionName, Assembly assembly, string resourcePrefix, IMessageHub hub, string[]? hiddenFrom = null)
        : base(new ContentSourceConfig
        {
            Name = collectionName,
            SourceType = "EmbeddedResource",
            HiddenFrom = hiddenFrom ?? []
        }, hub)
    {
        this.assembly = assembly;
        this.resourcePrefix = resourcePrefix.EndsWith('.') ? resourcePrefix : resourcePrefix + '.';
        
        resourcePaths = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(this.resourcePrefix, StringComparison.OrdinalIgnoreCase))
            .ToImmutableDictionary(
                name => ExtractPath(name),
                name => name
            );
    }

    private string ExtractPath(string resourceName)
    {
        var withoutPrefix = resourceName[resourcePrefix.Length..];
        var lastDotIndex = withoutPrefix.LastIndexOf('.');
        if (lastDotIndex > 0)
        {
            var nameWithoutExtension = withoutPrefix[..lastDotIndex].Replace('.', '/');
            var extension = withoutPrefix[lastDotIndex..];
            return nameWithoutExtension + extension;
        }
        return withoutPrefix.Replace('.', '/');
    }

    public override Task<Stream?> GetContentAsync(string path, CancellationToken ct = default)
    {
        var normalizedPath = path.TrimStart('/');
        
        // Try direct lookup first
        if (resourcePaths.TryGetValue(normalizedPath, out var resourceName))
        {
            return Task.FromResult<Stream?>(assembly.GetManifestResourceStream(resourceName));
        }
        
        // Try with .md extension if not found
        if (!normalizedPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            var pathWithMd = normalizedPath;
            if (resourcePaths.TryGetValue(pathWithMd, out resourceName))
            {
                return Task.FromResult<Stream?>(assembly.GetManifestResourceStream(resourceName));
            }
        }
        
        return Task.FromResult<Stream?>(null);
    }

    protected override async Task<(Stream? Stream, string Path, DateTime LastModified)> GetStreamAsync(string path, CancellationToken ct)
    {
        var stream = await GetContentAsync(path, ct);
        return (stream, path, DateTime.UtcNow); // Embedded resources don't have meaningful modification dates
    }

    protected override IAsyncEnumerable<(Stream? Stream, string Path, DateTime LastModified)> GetStreams(Func<string, bool> filter, CancellationToken ct)
    {
        return resourcePaths.Keys
            .Where(path => filter(path))
            .ToAsyncEnumerable()
            .SelectAwait(async path =>
            {
                var stream = await GetContentAsync(path, ct);
                return (stream, path, DateTime.UtcNow);
            });
    }

    protected override void AttachMonitor()
    {
        // Embedded resources don't change, so no monitoring needed
    }

    protected override async Task<ImmutableDictionary<string, Author>> LoadAuthorsAsync(CancellationToken ct)
    {
        try
        {
            var authorsStream = await GetContentAsync("authors.json", ct);
            if (authorsStream != null)
            {
                using var reader = new StreamReader(authorsStream);
                var content = await reader.ReadToEndAsync(ct);
                return ParseAuthors(content);
            }
        }
        catch
        {
            // If authors.json doesn't exist or can't be parsed, return empty
        }
        return ImmutableDictionary<string, Author>.Empty;
    }

    public override Task<IReadOnlyCollection<FolderItem>> GetFoldersAsync(string path)
    {
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

    public override Task<IReadOnlyCollection<FileItem>> GetFilesAsync(string path)
    {
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

    public override Task SaveFileAsync(string path, string fileName, Stream openReadStream)
    {
        throw new NotSupportedException("Embedded resource collections are read-only");
    }

    public override Task CreateFolderAsync(string folderPath)
    {
        throw new NotSupportedException("Embedded resource collections are read-only");
    }

    public override Task DeleteFolderAsync(string folderPath)
    {
        throw new NotSupportedException("Embedded resource collections are read-only");
    }

    public override Task DeleteFileAsync(string filePath)
    {
        throw new NotSupportedException("Embedded resource collections are read-only");
    }
}
