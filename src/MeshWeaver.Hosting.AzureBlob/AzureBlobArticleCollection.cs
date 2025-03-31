using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Azure.Storage.Blobs;
using MeshWeaver.Articles;
using MeshWeaver.Data;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.AzureBlob;

public class AzureBlobArticleCollection : ArticleCollection
{
    private readonly BlobContainerClient containerClient;
    private readonly ISynchronizationStream<InstanceCollection> articleStream;
    private readonly ILogger<AzureBlobArticleCollection> logger;
    public AzureBlobArticleCollection(
        ArticleSourceConfig config,
        IMessageHub hub,
        BlobServiceClient client) : base(config, hub)
    {
        var containerName = config.BasePath;
        logger = hub.ServiceProvider.GetRequiredService<ILogger<AzureBlobArticleCollection>>();
        containerClient = client.GetBlobContainerClient(containerName);
    }


    public override async Task<Stream> GetContentAsync(string path, CancellationToken ct = default)
    {
        if (path is null)
            return null;

        var blobClient = containerClient.GetBlobClient(path);
        if (!await blobClient.ExistsAsync(ct))
            return null;

        var memoryStream = new MemoryStream();
        await blobClient.DownloadToAsync(memoryStream, ct);
        return memoryStream;
    }



    protected override async Task<(Stream Stream, string Path, DateTime LastModified)> GetStreamAsync(string path, CancellationToken ct)
    {
        var blobClient = containerClient.GetBlobClient(path);
        if (!await blobClient.ExistsAsync(ct))
            return default;
        var properties = await blobClient.GetPropertiesAsync(cancellationToken: ct);

        return (await blobClient.OpenReadAsync(cancellationToken: ct), path, properties.Value.LastModified.DateTime);
    }

    protected override void AttachMonitor()
    {
    }

    protected override async Task<ImmutableDictionary<string, Author>> LoadAuthorsAsync(CancellationToken ct)
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


    private async IAsyncEnumerable<(Stream Stream, string Path, DateTime LastModified)> GetAllFromContainer(Func<string, bool> filter = null, string prefix=null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {

        await foreach (var blobItem in containerClient.GetBlobsAsync(prefix: prefix ?? ""))
        {
            if(cancellationToken.IsCancellationRequested)
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
        articleStream.Dispose();
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

        // Reset stream position to beginning before uploading
        if (openReadStream.CanSeek)
            openReadStream.Position = 0;

        await blobClient.UploadAsync(openReadStream, overwrite: true);
    }

    public override async Task CreateFolderAsync(string path)
    {
        // In Azure Blob Storage, folders don't technically exist as discrete entities.
        // They are inferred from blob names.
        // To "create" a folder, you'd typically upload a placeholder file

        var fullPath = path.TrimStart('/');
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
    }
    protected override async IAsyncEnumerable<(Stream Stream, string Path, DateTime LastModified)> GetStreams(Func<string,bool> filter, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await containerClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        await foreach (var article in GetAllFromContainer(filter, cancellationToken:cancellationToken))
        {
            if(cancellationToken.IsCancellationRequested)
                yield break;
            yield return article;
        }

    }
}
