using System.Reactive.Linq;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MeshWeaver.ContentCollections;

public class ContentService : IContentService
{
    private readonly IMessageHub hub;
    private readonly AccessService accessService;
    private readonly Dictionary<string, ContentCollection> dynamicCollections = new();
    private readonly object lockObject = new();

    public ContentService(IServiceProvider serviceProvider, IMessageHub hub, AccessService accessService)
    {
        this.hub = hub;
        this.accessService = accessService;

        var collectionsDict = new Dictionary<string, ContentCollection>();

        // Add collections from configuration
        var configs = serviceProvider.GetService<IOptions<List<ContentSourceConfig>>>();
        if (configs?.Value != null)
        {
            foreach (var collection in configs.Value.Select(CreateCollection))
            {
                collectionsDict[collection.Collection] = collection;
            }
        }

        // Add collections from providers
        var providers = serviceProvider.GetServices<IContentCollectionProvider>();
        foreach (var provider in providers)
        {
            foreach (var collection in provider.GetCollections())
            {
                collectionsDict[collection.Collection] = collection;
            }
        }

        collections = collectionsDict;
    }


    private ContentCollection CreateCollection(ContentSourceConfig config)
    {
        var factory = hub.ServiceProvider.GetKeyedService<IContentCollectionFactory>(config.SourceType);
        if (factory is null)
            throw new ArgumentException($"Unknown source type {config.SourceType}");
        return factory.Create(config, hub);
    }

    private readonly IReadOnlyDictionary<string, ContentCollection> collections;

    public ContentCollection? GetCollection(string collection)
        => collections.GetValueOrDefault(collection);

    public Task<Stream?> GetContentAsync(string collection, string path, CancellationToken ct = default)
    {
        var coll = GetCollection(collection);
        if (coll == null)
            throw new ArgumentException($"Collection '{collection}' not found");
        return coll.GetContentAsync(path, ct);
    }

    public async Task<IReadOnlyCollection<Article>> GetArticleCatalog(ArticleCatalogOptions catalogOptions,
        CancellationToken ct)
    {
        var allCollections =
            string.IsNullOrEmpty(catalogOptions.Collection)
            ? collections.Values
            : collections.TryGetValue(catalogOptions.Collection, out var collection)
                ? [collection]
                : [];
        return (await allCollections.Select(c => c.GetMarkdown(catalogOptions))
                .CombineLatest()
                .Select(c => c.SelectMany(articles => articles.OfType<Article>()))
                .Select(articles => ApplyOptions(articles, catalogOptions))
                .Skip(catalogOptions.Page * catalogOptions.PageSize)
                .Take(catalogOptions.PageSize)
                .FirstAsync())
            .ToArray()
            ;
    }

    private IEnumerable<Article> ApplyOptions(IEnumerable<Article> articles, ArticleCatalogOptions options)
    {
        if (accessService.Context is null || !accessService.Context.Roles.Contains(Roles.PortalAdmin))
        {
            var now = DateTime.UtcNow;
            articles = articles
                .Where(a => a.Published is not null && a.Published <= now);

        }
        return options.SortOrder switch
        {
            ArticleSortOrder.AscendingPublishDate => articles.OrderBy(a => a.Published),
            ArticleSortOrder.DescendingPublishDate => articles.OrderByDescending(a => a.Published),
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    public IObservable<object?> GetArticle(string collection, string article)
    {
        return GetCollection(collection)?.GetMarkdown(article) ?? Observable.Return(Controls.Markdown($"No article {article} found in collection {collection}"));
    }

    public Task<IReadOnlyCollection<ContentCollection>> GetCollectionsAsync(CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyCollection<ContentCollection>>(collections.Values.ToArray());
    }

    public IReadOnlyCollection<ContentCollection> GetCollections()
    {
        return collections.Values.ToArray();
    }

    public IReadOnlyCollection<ContentCollection> GetCollections(bool includeHidden = false)
    {
        var result = collections.Values;
        if (!includeHidden)
        {
            result = result.Where(c => !c.IsHidden);
        }
        return result.ToArray();
    }

    public IReadOnlyCollection<ContentCollection> GetCollections(string context)
    {
        return collections.Values
            .Where(c => !c.IsHiddenFrom(context))
            .ToArray();
    }

    public ContentCollection? GetCollectionForAddress(Address address)
    {
        // First, try to find a collection with a custom AddressFilter
        var collectionWithFilter = collections.Values
            .FirstOrDefault(c => c.Config.AddressFilter?.Invoke(address) == true);

        if (collectionWithFilter != null)
            return collectionWithFilter;

        // Then, try to find a collection with AddressMappings that match the address ID or Type
        var collectionWithMapping = collections.Values
            .FirstOrDefault(c => c.Config.AddressMappings != null &&
                (c.Config.AddressMappings.Contains(address.Id) ||
                 c.Config.AddressMappings.Contains(address.Type)));

        if (collectionWithMapping != null)
            return collectionWithMapping;

        // Check dynamic collections
        lock (lockObject)
        {
            if (dynamicCollections.TryGetValue(address.Id, out var dynamicCollection))
                return dynamicCollection;
        }

        // Default: try to find a collection by address ID
        return GetCollection(address.Id);
    }

    public async Task<ContentCollection?> GetCollectionForAddressAsync(Address address, CancellationToken cancellationToken = default)
    {
        // First check if it's already configured
        var existing = GetCollectionForAddress(address);
        if (existing != null)
            return existing;

        // Try to load it dynamically from the remote hub
        try
        {
            var request = new GetContentCollectionRequest();
            var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token
            );

            var response = await hub.AwaitResponse(request, o => o.WithTarget(address), timeout.Token);

            if (!response.Message.IsFound)
                return null;

            // Create and register the collection
            var collection = await CreateCollectionFromResponseAsync(response.Message, address);

            lock (lockObject)
            {
                dynamicCollections[address.Id] = collection;
            }

            return collection;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
    }

    private Task<ContentCollection> CreateCollectionFromResponseAsync(
        GetContentCollectionResponse response,
        Address address)
    {
        // Create provider based on type
        IStreamProvider provider = response.ProviderType switch
        {
            "FileSystem" => CreateFileSystemProvider(response.Configuration),
            "EmbeddedResource" => CreateEmbeddedResourceProvider(response.Configuration),
            "AzureBlob" => CreateAzureBlobProvider(response.Configuration),
            _ => throw new NotSupportedException($"Unknown provider type: {response.ProviderType}")
        };

        // Create config
        var config = new ContentSourceConfig
        {
            Name = response.CollectionName ?? address.Id,
            SourceType = response.ProviderType ?? "Unknown",
            AddressMappings = [address.Id],
            Settings = response.Configuration
        };

        // Create collection
        var collection = new ContentCollection(config, provider, hub);
        return Task.FromResult(collection);
    }

    private static IStreamProvider CreateFileSystemProvider(Dictionary<string, string>? config)
    {
        var basePath = config?.GetValueOrDefault("BasePath") ?? "";
        return new FileSystemStreamProvider(basePath);
    }

    private static IStreamProvider CreateEmbeddedResourceProvider(Dictionary<string, string>? config)
    {
        var assemblyName = config?.GetValueOrDefault("AssemblyName")
            ?? throw new ArgumentException("AssemblyName required for EmbeddedResource");
        var resourcePrefix = config?.GetValueOrDefault("ResourcePrefix")
            ?? throw new ArgumentException("ResourcePrefix required for EmbeddedResource");

        var assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == assemblyName)
            ?? throw new InvalidOperationException($"Assembly not found: {assemblyName}");

        return new EmbeddedResourceStreamProvider(assembly, resourcePrefix);
    }

    private IStreamProvider CreateAzureBlobProvider(Dictionary<string, string>? config)
    {
        var containerName = config?.GetValueOrDefault("ContainerName")
            ?? throw new ArgumentException("ContainerName required for AzureBlob");
        var clientName = config?.GetValueOrDefault("ClientName", "default");

        var factory = hub.ServiceProvider.GetService<Microsoft.Extensions.Azure.IAzureClientFactory<Azure.Storage.Blobs.BlobServiceClient>>();
        if (factory == null)
            throw new InvalidOperationException("Azure client factory not configured");

        var blobServiceClient = factory.CreateClient(clientName);
        return new AzureBlobStreamProvider(blobServiceClient, containerName);
    }
}
