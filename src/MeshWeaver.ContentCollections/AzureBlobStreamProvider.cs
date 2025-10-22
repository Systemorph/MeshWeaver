using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Azure.Storage.Blobs;

namespace MeshWeaver.ContentCollections;

/// <summary>
/// Stream provider for Azure Blob Storage
/// </summary>
public class AzureBlobStreamProvider(BlobServiceClient blobServiceClient, string containerName) : IStreamProvider
{
    public string ProviderType => "AzureBlob";

    public async Task<Stream?> GetStreamAsync(string reference, CancellationToken cancellationToken = default)
    {
        var containerClient = blobServiceClient.GetBlobContainerClient(containerName);
        var blobClient = containerClient.GetBlobClient(reference.TrimStart('/'));

        if (!await blobClient.ExistsAsync(cancellationToken))
        {
            return null;
        }

        return await blobClient.OpenReadAsync(cancellationToken: cancellationToken);
    }

    public async Task<(Stream? Stream, string Path, DateTime LastModified)> GetStreamWithMetadataAsync(string path, CancellationToken cancellationToken = default)
    {
        var containerClient = blobServiceClient.GetBlobContainerClient(containerName);
        var blobClient = containerClient.GetBlobClient(path.TrimStart('/'));

        if (!await blobClient.ExistsAsync(cancellationToken))
        {
            return default;
        }

        var properties = await blobClient.GetPropertiesAsync(cancellationToken: cancellationToken);
        var stream = await blobClient.OpenReadAsync(cancellationToken: cancellationToken);

        return (stream, path, properties.Value.LastModified.DateTime);
    }

    public async Task WriteStreamAsync(string reference, Stream content, CancellationToken cancellationToken = default)
    {
        var containerClient = blobServiceClient.GetBlobContainerClient(containerName);
        await containerClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

        var blobClient = containerClient.GetBlobClient(reference.TrimStart('/'));

        if (content.CanSeek)
        {
            content.Position = 0;
        }

        await blobClient.UploadAsync(content, overwrite: true, cancellationToken: cancellationToken);
    }

    public async IAsyncEnumerable<(Stream? Stream, string Path, DateTime LastModified)> GetStreamsAsync(Func<string, bool> filter, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var containerClient = blobServiceClient.GetBlobContainerClient(containerName);

        await foreach (var blobItem in containerClient.GetBlobsAsync(cancellationToken: cancellationToken))
        {
            if (filter(blobItem.Name))
            {
                var blobClient = containerClient.GetBlobClient(blobItem.Name);
                var stream = await blobClient.OpenReadAsync(cancellationToken: cancellationToken);
                yield return (stream, blobItem.Name, blobItem.Properties.LastModified?.DateTime ?? DateTime.UtcNow);
            }
        }
    }

    public async Task<IReadOnlyCollection<FolderItem>> GetFoldersAsync(string path)
    {
        // Azure Blob Storage doesn't have true folders, but we can simulate them using blob prefixes
        var containerClient = blobServiceClient.GetBlobContainerClient(containerName);
        var prefix = path.TrimStart('/');
        if (!string.IsNullOrEmpty(prefix) && !prefix.EndsWith('/'))
        {
            prefix += '/';
        }

        var folders = new HashSet<string>();
        await foreach (var blobItem in containerClient.GetBlobsByHierarchyAsync(prefix: prefix, delimiter: "/"))
        {
            if (blobItem.IsPrefix)
            {
                var folderName = blobItem.Prefix.TrimEnd('/');
                var lastSegment = folderName.Split('/').Last();
                folders.Add(lastSegment);
            }
        }

        return folders.Select(name => new FolderItem($"{path.TrimEnd('/')}/{name}", name, 0)).ToArray();
    }

    public async Task<IReadOnlyCollection<FileItem>> GetFilesAsync(string path)
    {
        var containerClient = blobServiceClient.GetBlobContainerClient(containerName);
        var prefix = path.TrimStart('/');
        if (!string.IsNullOrEmpty(prefix) && !prefix.EndsWith('/'))
        {
            prefix += '/';
        }

        var files = new List<FileItem>();
        await foreach (var blobItem in containerClient.GetBlobsByHierarchyAsync(prefix: prefix, delimiter: "/"))
        {
            if (blobItem.IsBlob)
            {
                var fileName = blobItem.Blob.Name.Split('/').Last();
                files.Add(new FileItem(
                    '/' + blobItem.Blob.Name,
                    fileName,
                    blobItem.Blob.Properties.LastModified?.DateTime ?? DateTime.UtcNow
                ));
            }
        }

        return files;
    }

    public async Task SaveFileAsync(string path, string fileName, Stream content, CancellationToken cancellationToken = default)
    {
        var blobPath = $"{path.TrimStart('/').TrimEnd('/')}/{fileName}".TrimStart('/');
        await WriteStreamAsync(blobPath, content, cancellationToken);
    }

    public Task CreateFolderAsync(string folderPath)
    {
        // Azure Blob Storage doesn't require explicit folder creation
        return Task.CompletedTask;
    }

    public async Task DeleteFolderAsync(string folderPath)
    {
        var containerClient = blobServiceClient.GetBlobContainerClient(containerName);
        var prefix = folderPath.TrimStart('/');
        if (!string.IsNullOrEmpty(prefix) && !prefix.EndsWith('/'))
        {
            prefix += '/';
        }

        await foreach (var blobItem in containerClient.GetBlobsAsync(prefix: prefix))
        {
            await containerClient.DeleteBlobAsync(blobItem.Name);
        }
    }

    public async Task DeleteFileAsync(string filePath)
    {
        var containerClient = blobServiceClient.GetBlobContainerClient(containerName);
        await containerClient.DeleteBlobAsync(filePath.TrimStart('/'));
    }

    public IDisposable? AttachMonitor(Action<string> onChanged)
    {
        // Azure Blob Storage doesn't support file system watching
        // Would need to implement polling or use Azure Event Grid
        return null;
    }

    public async Task<ImmutableDictionary<string, Author>> LoadAuthorsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var stream = await GetStreamAsync("authors.json", cancellationToken);
            if (stream != null)
            {
                using var reader = new StreamReader(stream);
                var content = await reader.ReadToEndAsync(cancellationToken);
                var authors = System.Text.Json.JsonSerializer.Deserialize<ImmutableDictionary<string, Author>>(content);
                return authors ?? ImmutableDictionary<string, Author>.Empty;
            }
        }
        catch
        {
            // If authors.json doesn't exist or can't be parsed, return empty
        }
        return ImmutableDictionary<string, Author>.Empty;
    }
}
