using System.Collections.Immutable;
using MeshWeaver.Messaging;

namespace MeshWeaver.ContentCollections;

public class FileSystemContentCollection(ContentSourceConfig config, IMessageHub hub) : ContentCollection(config, hub)
{
    public string BasePath { get; } = config.BasePath;
    private FileSystemWatcher watcher;

    public override Task<Stream> GetContentAsync(string path, CancellationToken ct = default)
    {
        if (path is null)
            return Task.FromResult<Stream>(null);
        var fullPath = Path.Combine(BasePath, path.TrimStart('/'));
        if (!File.Exists(fullPath))
            return Task.FromResult<Stream>(null);
        return Task.FromResult<Stream>(File.OpenRead(fullPath));
    }
    
    protected override Task<(Stream Stream, string Path, DateTime LastModified)> GetStreamAsync(string path, CancellationToken ct)
    {
        if (path is null)
            return Task.FromResult<(Stream Stream, string Path, DateTime LastModified)>(default);
        var fullPath = Path.Combine(BasePath, path);
        if (!File.Exists(fullPath))
            return Task.FromResult<(Stream Stream, string Path, DateTime LastModified)>(default);
        return Task.FromResult<(Stream Stream, string Path, DateTime LastModified)>((File.OpenRead(fullPath), path, File.GetLastAccessTime(path)));
    }

    protected override void AttachMonitor()
    {
        watcher = new FileSystemWatcher(BasePath)
        {
            IncludeSubdirectories = true
        };
        watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite;
        watcher.Changed += OnChanged;
        watcher.Renamed += OnRenamed;
        watcher.EnableRaisingEvents = true;
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (Path.GetExtension(e.FullPath) == ".md")
            UpdateArticle(Path.GetRelativePath(BasePath, e.FullPath));
    }


    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (Path.GetExtension(e.FullPath) == ".md")
            UpdateArticle(Path.GetRelativePath(BasePath, e.FullPath));
    }

    protected override IAsyncEnumerable<(Stream Stream, string Path, DateTime LastModified)> GetStreams(Func<string, bool> filter, CancellationToken ct)
    {
        var files = filter == MarkdownFilter
            ? Directory.GetFiles(BasePath, "*.md", SearchOption.AllDirectories)
            : Directory.GetFiles(BasePath,"*", SearchOption.AllDirectories).Where(f => filter is null || filter.Invoke(f));

        var items = files
            .Where(File.Exists)
            .Select(file =>
                (Stream: (Stream)File.OpenRead(file), Path: Path.GetRelativePath(BasePath, file), LastModified: File.GetLastWriteTime(file)));

        // Convert the synchronous IEnumerable to an IAsyncEnumerable
        return items.ToAsyncEnumerable();
    }

    protected override async Task<ImmutableDictionary<string, Author>> LoadAuthorsAsync(CancellationToken ct)
    {
        var authorsFile = Path.Combine(BasePath, "authors.json");
        if (File.Exists(authorsFile))
            return ParseAuthors(await File.ReadAllTextAsync(authorsFile, ct));
        return ImmutableDictionary<string, Author>.Empty;
    }


    public override void Dispose()
    {
        watcher?.Dispose();
        watcher = null;
        base.Dispose();
    }

    public override Task<IReadOnlyCollection<FolderItem>> GetFoldersAsync(string path)
    {
        var fullPath = Path.Combine(BasePath, path.TrimStart('/'));

        return Task.FromResult<IReadOnlyCollection<FolderItem>>(Directory.GetDirectories(fullPath)
            .Select(dirPath =>
            {
                var itemCount = Directory.GetFileSystemEntries(dirPath).Length;
                return new FolderItem(
                    '/' +Path.GetRelativePath(BasePath,dirPath),
                    Path.GetFileName(dirPath),
                    itemCount
                );
            })
            .ToArray());
    }

    public override Task<IReadOnlyCollection<FileItem>> GetFilesAsync(string path)
    {
        var fullPath = Path.Combine(BasePath, path.TrimStart('/'));

        return Task.FromResult<IReadOnlyCollection<FileItem>>(Directory.GetFiles(fullPath)
            .Select(filePath =>
            {
                var fileInfo = new FileInfo(filePath);

                return new FileItem(
                    '/' + Path.GetRelativePath(BasePath, filePath),
                    Path.GetFileName(filePath),
                    fileInfo.LastWriteTime
                );
            })
            .ToArray());
    }
    public override async Task SaveFileAsync(string path, string fileName, Stream openReadStream)
    {
        var fullPath = Path.Combine(BasePath, path.TrimStart('/'), fileName);
        await using var fileStream =
            new FileStream(fullPath, FileMode.Create, FileAccess.Write);
        await openReadStream.CopyToAsync(fileStream);
    }

    public override Task CreateFolderAsync(string path)
    {
        if(!Directory.Exists(path))
            Directory.CreateDirectory(Path.Combine(BasePath, path!));
        return Task.CompletedTask;
    }

    public override Task DeleteFolderAsync(string folderPath)
    {
        var fullPath = Path.Combine(BasePath, folderPath.TrimStart('/'));

        if (Directory.Exists(fullPath))
        {
            try
            {
                // Delete the folder and all its contents
                Directory.Delete(fullPath, recursive: true);
            }
            catch (IOException ex)
            {
                // Handle the case when files might be in use
                throw new InvalidOperationException($"Could not delete folder: {folderPath}. The folder or files within it may be in use.", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                // Handle permission issues
                throw new InvalidOperationException($"Permission denied when attempting to delete folder: {folderPath}", ex);
            }
        }
        else
        {
            throw new DirectoryNotFoundException($"Folder not found: {folderPath}");
        }

        return Task.CompletedTask;
    }

    public override Task DeleteFileAsync(string filePath)
    {
        var fullPath = Path.Combine(BasePath, filePath.TrimStart('/'));

        if (File.Exists(fullPath))
        {
            try
            {
                File.Delete(fullPath);
            }
            catch (IOException ex)
            {
                // Handle the case when the file might be in use
                throw new InvalidOperationException($"Could not delete file: {filePath}. The file may be in use.", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                // Handle permission issues
                throw new InvalidOperationException($"Permission denied when attempting to delete file: {filePath}", ex);
            }
        }
        else
        {
            throw new FileNotFoundException($"File not found: {filePath}");
        }

        return Task.CompletedTask;
    }
}
