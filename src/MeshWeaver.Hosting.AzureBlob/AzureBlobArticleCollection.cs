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


    public override Task<IReadOnlyCollection<FolderItem>> GetFoldersAsync(string path)
    {
        throw new NotImplementedException();
    }

    public override Task<IReadOnlyCollection<FileItem>> GetFilesAsync(string path)
    {
        throw new NotImplementedException();
    }

    public override Task SaveFileAsync(string path, string fileName, Stream openReadStream)
    {
        throw new NotImplementedException();
    }

    public override Task CreateFolderAsync(string returnValue)
    {
        throw new NotImplementedException();
    }

    public override Task DeleteFolderAsync(string folderPath)
    {
        throw new NotImplementedException();
    }

    public override Task DeleteFileAsync(string filePath)
    {
        throw new NotImplementedException();
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
