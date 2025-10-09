using System.Collections.Concurrent;
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
    private readonly IContentService? parentContentService;
    private readonly object lockObject = new();

    public ContentService(IServiceProvider serviceProvider, IMessageHub hub, AccessService accessService)
    {
        this.hub = hub;
        this.accessService = accessService;

        // Get parent content service if available - walk up the parent chain
        try
        {
            var currentParent = hub.Configuration.ParentHub;
            var visited = new HashSet<IMessageHub>();
            while (currentParent != null && currentParent != hub)
            {
                // Prevent infinite loops
                if (!visited.Add(currentParent))
                    break;

                var parentCs = currentParent.ServiceProvider.GetService<IContentService>();
                if (parentCs != null)
                {
                    parentContentService = parentCs;
                    break;
                }
                // Try next parent in chain
                currentParent = currentParent.Configuration.ParentHub;
            }
        }
        catch (Exception ex)
        {
            // Parent may not have content service, that's ok
            System.Diagnostics.Debug.WriteLine($"Error getting parent ContentService for {hub.Address}: {ex.Message}");
        }

        // Add collections from configuration
        var configs = serviceProvider.GetService<IOptions<List<ContentCollectionConfig>>>();
        if (configs?.Value != null)
        {
            foreach (var collection in configs.Value.Select(CreateCollection))
            {
                collections[collection.Collection] = collection;
            }
        }

        // Add collections from providers
        var providers = serviceProvider.GetServices<IContentCollectionProvider>();
        foreach (var provider in providers)
        {
            foreach (var collection in provider.GetCollections())
            {
                collections[collection.Collection] = collection;
            }
        }

    }


    private ContentCollection CreateCollection(ContentCollectionConfig config)
    {
        var factory = hub.ServiceProvider.GetKeyedService<IContentCollectionFactory>(config.SourceType);
        if (factory is null)
            throw new ArgumentException($"Unknown source type {config.SourceType}");
        return factory.Create(config, hub);
    }

    private readonly ConcurrentDictionary<string, ContentCollection> collections = new();
    private readonly SemaphoreSlim initializeLock = new(1, 1);

    public ContentCollection? GetCollection(string collection)
    {
        // Try local collections first
        if (collections.TryGetValue(collection, out var localCollection))
            return localCollection;

        // Delegate to parent if not found locally
        return parentContentService?.GetCollection(collection);
    }

    public async Task<ContentCollection> InitializeCollectionAsync(ContentCollectionConfig config, CancellationToken cancellationToken = default)
    {
        // Check if already exists
        if (collections.TryGetValue(config.Name!, out var existing))
            return existing;

        await initializeLock.WaitAsync(cancellationToken);
        try
        {
            // Double-check after acquiring lock
            if (collections.TryGetValue(config.Name!, out existing))
                return existing;

            // Get the factory for this provider type
            var factory = hub.ServiceProvider.GetKeyedService<IStreamProviderFactory>(config.SourceType);
            if (factory is null)
                throw new InvalidOperationException($"Unknown provider type {config.SourceType}");

            // Build configuration dictionary
            var configuration = config.Settings ?? new Dictionary<string, string>();
            if (config.BasePath != null && !configuration.ContainsKey("BasePath"))
            {
                configuration["BasePath"] = config.BasePath;
            }

            // Create provider using the factory
            var provider = factory.Create(configuration);

            // Create and initialize new collection
            var newCollection = new ContentCollection(config, provider, hub);
            await newCollection.InitializeAsync(cancellationToken);

            // Register it
            collections[config.Name!] = newCollection;

            return newCollection;
        }
        finally
        {
            initializeLock.Release();
        }
    }

    public ContentCollectionConfig? GetOrCreateCollectionConfig(string baseCollectionName, string localizedCollectionName, string? subPath = null)
    {
        // First check if the localized collection already exists
        var existingCollection = GetCollection(localizedCollectionName);
        if (existingCollection != null)
            return existingCollection.Config;

        // Try to get the base configuration from parent hub's registry
        var parentRegistry = hub.Configuration.ParentHub?.ServiceProvider.GetService<IContentCollectionRegistry>();
        var globalRegistration = parentRegistry?.GetCollection(baseCollectionName);

        if (globalRegistration == null)
            return null;

        // Create localized config with subpath
        var basePath = globalRegistration.Config.BasePath ?? "";
        var fullPath = string.IsNullOrEmpty(subPath)
            ? basePath
            : System.IO.Path.Combine(basePath, subPath);

        return globalRegistration.Config with
        {
            Name = localizedCollectionName,
            BasePath = fullPath
        };
    }


    public Task<Stream?> GetContentAsync(string collection, string path, CancellationToken ct = default)
    {
        var coll = GetCollection(collection);
        if (coll == null)
            throw new ArgumentException($"Collection '{collection}' not found");
        return coll.GetContentAsync(path, ct);
    }

    public async Task<IReadOnlyCollection<Article>> GetArticleCatalogAsync(ArticleCatalogOptions catalogOptions,
        CancellationToken ct)
    {

        var allCollections = (catalogOptions.Collections ?? [])
            .Select(x => collections.GetValueOrDefault(x))
            .Where(x => x is not null)
            .ToArray();
        return (await allCollections.Select(c => c!.GetMarkdown(catalogOptions))
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
        var result = collections.Values;
        return result.ToArray();
    }

    public IEnumerable<ContentCollection> GetCollections(string context)
    {
        return collections.Values;
    }


    private readonly ConcurrentDictionary<Address, IReadOnlyCollection<ContentCollection>> collectionsByAddress = new();
    private readonly SemaphoreSlim getCollectionsLock = new(1, 1);
    public async Task<IReadOnlyCollection<ContentCollection>> GetCollectionForAddressAsync(Address address, CancellationToken cancellationToken = default)
    {
        // First check if it's already configured
        if (collectionsByAddress.TryGetValue(address, out var existing))
            return existing;

        // Try to load it dynamically from the remote hub
        try
        {
            await getCollectionsLock.WaitAsync(cancellationToken);
            var ret = await CreateCollections(address, cancellationToken);
            collectionsByAddress[address] = ret;
            return ret;

        }
        catch (TaskCanceledException)
        {
            return [];
        }
        finally
        {
            getCollectionsLock.Release();
        }
    }

    private async Task<IReadOnlyCollection<ContentCollection>> CreateCollections(Address address, CancellationToken cancellationToken)
    {
        var request = new GetContentCollectionRequest();
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token
        );

        var response = await hub.AwaitResponse(request, o => o.WithTarget(address), timeout.Token);

        if (!response.Message.IsFound)
            return [];

        // Create and register all collections from the response
        var createdCollections = await CreateCollectionsFromResponseAsync(response.Message, address);

        lock (lockObject)
        {
            foreach (var collection in createdCollections)
            {
                collections[collection.Collection] = collection;
            }
        }

        return createdCollections;

    }
    private Task<IReadOnlyCollection<ContentCollection>> CreateCollectionsFromResponseAsync(
        GetContentCollectionResponse response,
        Address address)
    {
        if (response.Collections == null || response.Collections.Count == 0)
            return Task.FromResult<IReadOnlyCollection<ContentCollection>>(Array.Empty<ContentCollection>());

        var collections = response.Collections.Select(collectionConfig =>
        {
            // Get the factory for this provider type
            var factory = hub.ServiceProvider.GetKeyedService<IStreamProviderFactory>(collectionConfig.SourceType);
            if (factory == null)
                throw new NotSupportedException($"Unknown provider type: {collectionConfig.SourceType}");

            // Build configuration dictionary
            var configuration = collectionConfig.Settings ?? new Dictionary<string, string>();
            if (collectionConfig.BasePath != null && !configuration.ContainsKey("BasePath"))
            {
                configuration["BasePath"] = collectionConfig.BasePath;
            }

            // Create provider using the factory
            var provider = factory.Create(configuration);

            // Use collectionConfig directly (already has proper Address set)
            collectionConfig.Address = address;

            // Create collection
            return new ContentCollection(collectionConfig, provider, hub);
        }).ToArray();

        return Task.FromResult<IReadOnlyCollection<ContentCollection>>(collections);
    }
}
