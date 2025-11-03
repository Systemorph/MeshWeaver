using System.Collections.Immutable;
using System.Text.Json;

namespace MeshWeaver.ContentCollections;

/// <summary>
/// Stream provider for file system-based content
/// </summary>
public class FileSystemStreamProvider(string basePath) : IStreamProvider
{
    public const string SourceType = "FileSystem";

    private FileSystemWatcher? watcher;

    public string ProviderType => SourceType;

    public Task<Stream?> GetStreamAsync(string reference, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.Combine(basePath, reference.TrimStart('/'));
        if (!File.Exists(fullPath))
        {
            return Task.FromResult<Stream?>(null);
        }

        Stream stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            useAsync: true);

        return Task.FromResult<Stream?>(stream);
    }

    public Task<(Stream? Stream, string Path, DateTime LastModified)> GetStreamWithMetadataAsync(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.Combine(basePath, path.TrimStart('/'));
        if (!File.Exists(fullPath))
        {
            return Task.FromResult<(Stream? Stream, string Path, DateTime LastModified)>(default);
        }
        return Task.FromResult<(Stream? Stream, string Path, DateTime LastModified)>(
            (File.OpenRead(fullPath), path, File.GetLastWriteTime(fullPath)));
    }

    public async Task WriteStreamAsync(string reference, Stream content, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.IsPathRooted(reference) ? reference : Path.Combine(basePath, reference.TrimStart('/'));
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var fileStream = new FileStream(
            fullPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            4096,
            useAsync: true);

        await content.CopyToAsync(fileStream, cancellationToken);
    }

    public IAsyncEnumerable<(Stream? Stream, string Path, DateTime LastModified)> GetStreamsAsync(Func<string, bool> filter, CancellationToken cancellationToken = default)
    {
        var files = filter.Method.Name.Contains("MarkdownFilter")
            ? Directory.GetFiles(basePath, "*.md", SearchOption.AllDirectories)
            : Directory.GetFiles(basePath, "*", SearchOption.AllDirectories).Where(f => filter(f));

        var items = files
            .Where(File.Exists)
            .Select(file =>
                (Stream: (Stream?)File.OpenRead(file), Path: Path.GetRelativePath(basePath, file), LastModified: File.GetLastWriteTime(file)));

        return items.ToAsyncEnumerable();
    }

    public Task<IReadOnlyCollection<FolderItem>> GetFoldersAsync(string path)
    {
        var fullPath = Path.Combine(basePath, path.TrimStart('/'));

        return Task.FromResult<IReadOnlyCollection<FolderItem>>(Directory.GetDirectories(fullPath)
            .Select(dirPath =>
            {
                var itemCount = Directory.GetFileSystemEntries(dirPath).Length;
                return new FolderItem(
                    '/' + Path.GetRelativePath(basePath, dirPath),
                    Path.GetFileName(dirPath),
                    itemCount
                );
            })
            .ToArray());
    }

    public Task<IReadOnlyCollection<FileItem>> GetFilesAsync(string path)
    {
        var fullPath = Path.Combine(basePath, path.TrimStart('/'));

        return Task.FromResult<IReadOnlyCollection<FileItem>>(Directory.GetFiles(fullPath)
            .Select(filePath =>
            {
                var fileInfo = new FileInfo(filePath);

                return new FileItem(
                    '/' + Path.GetRelativePath(basePath, filePath),
                    Path.GetFileName(filePath),
                    fileInfo.LastWriteTime
                );
            })
            .ToArray());
    }

    public async Task SaveFileAsync(string path, string fileName, Stream content, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.Combine(basePath, path.TrimStart('/'), fileName);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        await using var fileStream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        await content.CopyToAsync(fileStream, cancellationToken);
    }

    public Task CreateFolderAsync(string folderPath)
    {
        var fullPath = Path.Combine(basePath, folderPath.TrimStart('/'));
        if (!Directory.Exists(fullPath))
        {
            Directory.CreateDirectory(fullPath);
        }
        return Task.CompletedTask;
    }

    public Task DeleteFolderAsync(string folderPath)
    {
        var fullPath = Path.Combine(basePath, folderPath.TrimStart('/'));

        if (Directory.Exists(fullPath))
        {
            try
            {
                Directory.Delete(fullPath, recursive: true);
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException($"Could not delete folder: {folderPath}. The folder or files within it may be in use.", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new InvalidOperationException($"Permission denied when attempting to delete folder: {folderPath}", ex);
            }
        }
        else
        {
            throw new DirectoryNotFoundException($"Folder not found: {folderPath}");
        }

        return Task.CompletedTask;
    }

    public Task DeleteFileAsync(string filePath)
    {
        var fullPath = Path.Combine(basePath, filePath.TrimStart('/'));

        if (File.Exists(fullPath))
        {
            try
            {
                File.Delete(fullPath);
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException($"Could not delete file: {filePath}. The file may be in use.", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new InvalidOperationException($"Permission denied when attempting to delete file: {filePath}", ex);
            }
        }
        else
        {
            throw new FileNotFoundException($"File not found: {filePath}");
        }

        return Task.CompletedTask;
    }

    public IDisposable? AttachMonitor(Action<string> onChanged)
    {
        watcher = new FileSystemWatcher(basePath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
        };

        watcher.Changed += (sender, e) =>
        {
            if (Path.GetExtension(e.FullPath) == ".md")
            {
                onChanged(Path.GetRelativePath(basePath, e.FullPath));
            }
        };

        watcher.Renamed += (sender, e) =>
        {
            if (Path.GetExtension(e.FullPath) == ".md")
            {
                onChanged(Path.GetRelativePath(basePath, e.FullPath));
            }
        };

        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    public async Task<ImmutableDictionary<string, Author>> LoadAuthorsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var authorsFile = Path.Combine(basePath, "authors.json");
            if (File.Exists(authorsFile))
            {
                var content = await File.ReadAllTextAsync(authorsFile, cancellationToken);
                var authors = JsonSerializer.Deserialize<ImmutableDictionary<string, Author>>(content);
                return authors ?? ImmutableDictionary<string, Author>.Empty;
            }
            return ImmutableDictionary<string, Author>.Empty;
        }
        catch
        {
            return ImmutableDictionary<string, Author>.Empty;
        }
    }
}
