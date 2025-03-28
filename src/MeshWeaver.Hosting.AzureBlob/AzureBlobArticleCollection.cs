using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using Azure.Storage.Blobs;
using MeshWeaver.Articles;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
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
        articleStream = CreateStream(containerName);
    }

    public override IObservable<IEnumerable<Article>> GetArticles(ArticleCatalogOptions toOptions) =>
        articleStream.Select(x => x.Value.Instances.Values.Cast<Article>());

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

    private ISynchronizationStream<InstanceCollection> CreateStream(string containerName)
    {
        var ret = new SynchronizationStream<InstanceCollection>(
            new(Collection, containerName),
            Hub,
            new EntityReference(Collection, containerName),
            Hub.CreateReduceManager().ReduceTo<InstanceCollection>(),
            x => x);
        ret.Initialize(InitializeAsync, ex => logger.LogError(ex, "Unable to load collection {Collection}", containerName));
        return ret;
    }

    public override IObservable<Article> GetArticle(string path, ArticleOptions options = null) =>
        articleStream.Reduce(new InstanceReference(path), c => c.ReturnNullWhenNotPresent()).Select(x => (Article)x?.Value);

    public async Task<InstanceCollection> InitializeAsync(CancellationToken ct)
    {
        await containerClient.CreateIfNotExistsAsync(cancellationToken: ct);
        var ret = new InstanceCollection(
            await GetAllFromContainer(ct)
                .ToDictionaryAsync(x => (object)x.Name, x => (object)x, cancellationToken: ct)
        );
        return ret;
    }

    private async IAsyncEnumerable<Article> GetAllFromContainer([EnumeratorCancellation] CancellationToken ct)
    {
        var authorsClient = containerClient.GetBlobClient("authors.json");

        if (await authorsClient.ExistsAsync(ct))
        {
            await using var stream = await authorsClient.OpenReadAsync(cancellationToken: ct);
            using var reader = new StreamReader(stream);
            var content = await reader.ReadToEndAsync(ct);
            Authors = LoadAuthorsAsync(content);

        }

        await foreach (var blobItem in containerClient.GetBlobsAsync(prefix: ""))
        {
            if (blobItem.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                var article = await LoadArticle(blobItem.Name, CancellationToken.None);
                if (article != null)
                {
                    yield return article;
                }
            }
        }
    }

    private async Task<Article> LoadArticle(string blobPath, CancellationToken ct)
    {
        var blobClient = containerClient.GetBlobClient(blobPath);
        if (!await blobClient.ExistsAsync(ct))
            return null;

        using var stream = await blobClient.OpenReadAsync(cancellationToken: ct);
        using var reader = new StreamReader(stream);
        var content = await reader.ReadToEndAsync(ct);
        var properties = await blobClient.GetPropertiesAsync(cancellationToken: ct);


        return ArticleExtensions.ParseArticle(
            Collection,
            blobPath,
            properties.Value.LastModified.DateTime,
            content,
            Authors
        );
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
}
