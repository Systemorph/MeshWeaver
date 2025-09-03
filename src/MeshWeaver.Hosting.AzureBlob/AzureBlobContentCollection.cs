using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.AzureBlob;

public class AzureBlobContentCollection(
    ContentSourceConfig config,
    IMessageHub hub,
    BlobServiceClient client) : ContentCollection(config, hub)
{
    private BlobContainerClient containerClient = null!;
    private readonly ISynchronizationStream<InstanceCollection>? articleStream;
    private readonly ILogger<AzureBlobContentCollection> logger = hub.ServiceProvider.GetRequiredService<ILogger<AzureBlobContentCollection>>();
    private readonly ContentSourceConfig config = config;

    protected override void InitializeInfrastructure()
    {
        base.InitializeInfrastructure();
        var containerName = config.BasePath;
        containerClient = client.GetBlobContainerClient(containerName);

    }

    public override async Task<Stream?> GetContentAsync(string? path, CancellationToken ct = default)
    {
        if (path is null)
            return null;

        var blobClient = containerClient.GetBlobClient(path);
        if (!await blobClient.ExistsAsync(ct))
            return null;

        return await blobClient.OpenReadAsync(cancellationToken: ct);
    }

    protected override async Task<(Stream? Stream, string Path, DateTime LastModified)> GetStreamAsync(string path, CancellationToken ct)
    {
        var blobClient = containerClient.GetBlobClient(path);
        if (!await blobClient.ExistsAsync(ct))
            return default;
        var properties = await blobClient.GetPropertiesAsync(cancellationToken: ct);

        return (await blobClient.OpenReadAsync(cancellationToken: ct), path, properties.Value.LastModified.DateTime);
    }

    protected override void AttachMonitor()
    {
        // In Azure Blob Storage we don't have file system watcher events
        // We'll trigger update events when operations are performed through our API
        logger.LogInformation("Azure Blob Storage monitor attached - changes will be tracked on operations");
    }

    // File change handlers
    private void OnFileChanged(string path)
    {
        if (Path.GetExtension(path) == ".md")
        {
            logger.LogInformation("File changed: {Path}", path);
            UpdateArticle(path);
        }
    }

    private void OnFileAdded(string path)
    {
        if (Path.GetExtension(path) == ".md")
        {
            logger.LogInformation("File added: {Path}", path);
            UpdateArticle(path);
        }
    }

    private void OnFileDeleted(string path)
    {
        if (Path.GetExtension(path) == ".md")
        {
            logger.LogInformation("File deleted: {Path}", path);
            // The base class doesn't provide a method to remove an article,
            // so we'll just update which should handle the absence of the file
            UpdateArticle(path);
        }
    }

    private void OnFileRenamed(string oldPath, string newPath)
    {
        logger.LogInformation("File renamed: {OldPath} -> {NewPath}", oldPath, newPath);

        if (Path.GetExtension(oldPath) == ".md")
        {
            // Handle old path (will likely remove the article since file doesn't exist)
            UpdateArticle(oldPath);
        }

        if (Path.GetExtension(newPath) == ".md")
        {
            // Handle new path
            UpdateArticle(newPath);
        }
    }

    protected override async Task<ImmutableDictionary<string, Author>> LoadAuthorsAsync(CancellationToken ct)
    {
        try
        {
            var authorsClient = containerClient.GetBlobClient("authors.json");

            if (await authorsClient.ExistsAsync(ct))
            {
                await using var stream = await authorsClient.OpenReadAsync(cancellationToken: ct);
                using var reader = new StreamReader(stream);
                var content = await reader.ReadToEndAsync(ct);
                return ParseAuthors(content);
            }

            return ImmutableDictionary<string, Author>.Empty;

        }
        catch
        {
            logger?.LogWarning("No authors.json file in content collection.");
            return ImmutableDictionary<string, Author>.Empty;
        }
    }

    private async IAsyncEnumerable<(Stream? Stream, string Path, DateTime LastModified)> GetAllFromContainer(Func<string, bool>? filter = null, string? prefix = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var blobItem in containerClient.GetBlobsAsync(prefix: prefix ?? ""))
        {
            if (cancellationToken.IsCancellationRequested)
                yield break;
            if (filter is null || filter.Invoke(blobItem.Name))
            {
                var tuple = await GetStreamAsync(blobItem.Name, CancellationToken.None);
                if (tuple.Stream != null)
                    yield return tuple;
            }
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        articleStream?.Dispose();
    }

    public override async Task<IReadOnlyCollection<FolderItem>> GetFoldersAsync(string path)
    {
        var prefix = path.TrimStart('/');
        if (!string.IsNullOrEmpty(prefix) && !prefix.EndsWith('/'))
            prefix += '/';

        var folders = new HashSet<string>();

        await foreach (var blob in containerClient.GetBlobsAsync(prefix: prefix))
        {
            var relativePath = blob.Name;
            if (relativePath.StartsWith(prefix))
                relativePath = relativePath.Substring(prefix.Length);

            var slashIndex = relativePath.IndexOf('/');
            if (slashIndex > 0)
            {
                var folderName = relativePath.Substring(0, slashIndex);
                folders.Add(folderName);
            }
        }

        return folders.Select(folder => new FolderItem(
            '/' + Path.Combine(prefix, folder),
            folder,
            0 // We don't have a direct way to count items in Azure Blob
        )).ToArray();
    }

    public override async Task<IReadOnlyCollection<FileItem>> GetFilesAsync(string path)
    {
        var prefix = path.TrimStart('/');
        if (!string.IsNullOrEmpty(prefix) && !prefix.EndsWith('/'))
            prefix += '/';

        var files = new List<FileItem>();

        await foreach (var blob in containerClient.GetBlobsAsync(prefix: prefix))
        {
            var relativePath = blob.Name;
            if (relativePath.StartsWith(prefix))
                relativePath = relativePath.Substring(prefix.Length);

            if (!relativePath.Contains('/'))
            {
                files.Add(new FileItem(
                    '/' + blob.Name,
                    relativePath,
                    blob.Properties.LastModified?.DateTime ?? DateTime.MinValue
                ));
            }
        }

        return files;
    }

    public override async Task SaveFileAsync(string path, string fileName, Stream openReadStream)
    {
        var fullPath = Path.Combine(path.TrimStart('/'), fileName);
        var blobClient = containerClient.GetBlobClient(fullPath);

        // Check if file exists to determine if this is an add or update
        bool exists = await blobClient.ExistsAsync();

        // Reset stream position to beginning before uploading
        if (openReadStream.CanSeek)
            openReadStream.Position = 0;

        // Set content type for images based on file extension
        var contentType = GetContentType(fileName);
        var blobUploadOptions = new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders
            {
                ContentType = contentType
            }
        };

        await blobClient.UploadAsync(openReadStream, blobUploadOptions);

        // Trigger appropriate event based on whether this was an add or update
        if (exists)
        {
            OnFileChanged(fullPath);
        }
        else
        {
            OnFileAdded(fullPath);
        }
    }

    public override async Task CreateFolderAsync(string folderPath)
    {
        // In Azure Blob Storage, folders don't technically exist as discrete entities.
        // They are inferred from blob names.
        // To "create" a folder, you'd typically upload a placeholder file

        var fullPath = folderPath.TrimStart('/');
        if (!fullPath.EndsWith('/'))
            fullPath += '/';

        fullPath += ".folder"; // placeholder file

        var blobClient = containerClient.GetBlobClient(fullPath);
        await blobClient.UploadAsync(new MemoryStream(), overwrite: true);
    }

    public override async Task DeleteFolderAsync(string folderPath)
    {
        folderPath = folderPath.TrimStart('/');
        if (!folderPath.EndsWith('/'))
            folderPath += '/';

        // Find all blobs with this prefix and delete them
        await foreach (var blob in containerClient.GetBlobsAsync(prefix: folderPath))
        {
            var blobClient = containerClient.GetBlobClient(blob.Name);
            await blobClient.DeleteIfExistsAsync();

            // Trigger delete event for each file
            OnFileDeleted(blob.Name);
        }
    }

    public override async Task DeleteFileAsync(string filePath)
    {
        filePath = filePath.TrimStart('/');
        var blobClient = containerClient.GetBlobClient(filePath);

        var response = await blobClient.DeleteIfExistsAsync();

        if (!response.Value)
        {
            throw new FileNotFoundException($"File not found: {filePath}");
        }

        // Trigger delete event
        OnFileDeleted(filePath);
    }

    // New method to handle file renaming
    public async Task RenameFileAsync(string oldPath, string newPath)
    {
        oldPath = oldPath.TrimStart('/');
        newPath = newPath.TrimStart('/');

        // Get reference to the source blob
        var sourceBlobClient = containerClient.GetBlobClient(oldPath);

        if (!await sourceBlobClient.ExistsAsync())
        {
            throw new FileNotFoundException($"Source file not found: {oldPath}");
        }

        // Get reference to the destination blob
        var destBlobClient = containerClient.GetBlobClient(newPath);

        // Copy the blob to the new location
        await destBlobClient.StartCopyFromUriAsync(sourceBlobClient.Uri);

        // Wait for the copy to complete
        BlobProperties properties;
        do
        {
            await Task.Delay(100);
            properties = await destBlobClient.GetPropertiesAsync();
        } while (properties.CopyStatus == CopyStatus.Pending);

        if (properties.CopyStatus != CopyStatus.Success)
        {
            throw new IOException($"Copy operation failed: {properties.CopyStatus}");
        }

        // Delete the source blob
        await sourceBlobClient.DeleteAsync();

        // Trigger rename event
        OnFileRenamed(oldPath, newPath);
    }

    protected override async IAsyncEnumerable<(Stream? Stream, string Path, DateTime LastModified)> GetStreams(Func<string, bool> filter, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await containerClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        await foreach (var article in GetAllFromContainer(filter, cancellationToken: cancellationToken))
        {
            if (cancellationToken.IsCancellationRequested)
                yield break;
            yield return article;
        }
    }
}
