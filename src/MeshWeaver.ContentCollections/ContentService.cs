using System.Collections.Concurrent;
using System.Reactive.Linq;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.ContentCollections;

public class ContentService : IContentService
{
    private readonly IMessageHub hub;
    private readonly AccessService accessService;
    private readonly IContentService? parentContentService;
    private readonly ConcurrentDictionary<string, ContentCollectionConfig> collectionConfigs;
    private readonly Dictionary<string, Task<ContentCollection?>> collections = new();
    private readonly Lock initializeLock = new();
    public ContentService(IMessageHub hub, AccessService accessService)
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


        // Add collections from providers
        var providers = hub.ServiceProvider.GetServices<IContentCollectionConfigProvider>();
        collectionConfigs = new(providers.SelectMany(p => p.GetCollections()).ToDictionary(c => c.Name));

    }


    private async Task<ContentCollection?> CreateCollectionAsync(ContentCollectionConfig config, CancellationToken cancellationToken)
    {
        var factory = hub.ServiceProvider.GetKeyedService<IContentCollectionFactory>(config.SourceType);
        if (factory is null)
            throw new ArgumentException($"Unknown source type {config.SourceType}");
        return await factory.CreateAsync(config, hub, cancellationToken);
    }


    public async Task<ContentCollection?> GetCollectionAsync(string collection, CancellationToken ct)
    {
        if (parentContentService is not null)
        {
            var fromParent = await parentContentService.GetCollectionAsync(collection, ct);
            if (fromParent is not null)
                return fromParent;
        }
        // Try local collections first
        if (collections.TryGetValue(collection, out var localCollection))
            return await localCollection;

        var config = collectionConfigs.GetValueOrDefault(collection);
        if (config is not null)
            return await InitializeCollectionAsync(config, ct);

        // Delegate to parent if not found locally
        return null;
    }


    private Task<ContentCollection?> InitializeCollectionAsync(ContentCollectionConfig config, CancellationToken cancellationToken = default)
    {

        Task<ContentCollection?>? initTask;
        lock (initializeLock)
        {
            if (collections.TryGetValue(config.Name, out var localCollection))
                return localCollection;
            collectionConfigs[config.Name] = config;
            // Check again inside lock
            if (collections.TryGetValue(config.Name, out var existing))
                return existing;

            else
            {
                lock (initializeLock)
                {
                    if (collections.TryGetValue(config.Name, out existing))
                        return existing;

                    // Create a new initialization task
                    initTask = InstantiateCollectionAsync(config, cancellationToken);
                    collections[config.Name] = initTask;
                    return initTask;
                }
            }
        }

    }

    public ContentCollectionConfig? GetCollectionConfig(string collection)
    {
        // Try local configs first
        if (collectionConfigs.TryGetValue(collection, out var config))
            return config;

        // Delegate to parent if not found locally
        if (parentContentService is not null)
            return parentContentService.GetCollectionConfig(collection);

        return null;
    }

    public void AddConfiguration(ContentCollectionConfig contentCollectionConfig)
    {
        this.collectionConfigs[contentCollectionConfig.Name] = contentCollectionConfig;
    }

    private Task<ContentCollection?> InstantiateCollectionAsync(ContentCollectionConfig config, CancellationToken cancellationToken)
    {
        try
        {
            // Use the async factory to create the collection
            var newCollection = CreateCollectionAsync(config, cancellationToken);

            // Register it
            collections[config.Name] = newCollection;

            return newCollection;
        }
        catch
        {
            return Task.FromResult<ContentCollection?>(null);
        }
        finally
        {
            // Remove from initialization tasks when complete
            lock (initializeLock)
            {
                collections.Remove(config.Name);
            }
        }
    }



    public async Task<Stream?> GetContentAsync(string collection, string path, CancellationToken ct = default)
    {
        var coll = await GetCollectionAsync(collection, ct);
        if (coll == null)
            throw new ArgumentException($"Collection '{collection}' not found");
        return await coll.GetContentAsync(path, ct);
    }

    public async Task<IReadOnlyCollection<Article>> GetArticleCatalogAsync(ArticleCatalogOptions catalogOptions,
        CancellationToken ct)
    {

        var allCollections = await (catalogOptions.Collections ?? [])
            .ToAsyncEnumerable()
            .SelectAwait(async x =>
            {
                var valueOrDefault = collections.GetValueOrDefault(x);
                return valueOrDefault is null ? null : await valueOrDefault;
            })
            .ToArrayAsync(ct);
        return (await allCollections
                .Where(x => x is not null)
                .Select(c => c!.GetMarkdown(catalogOptions))
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

    public async Task<IObservable<object?>> GetArticleAsync(string collection, string article, CancellationToken ct)
    {
        var coll = await GetCollectionAsync(collection, ct);
        return coll?.GetMarkdown(article) ?? Observable.Return(Controls.Markdown($"No article {article} found in collection {collection}"));
    }

    public IAsyncEnumerable<ContentCollection> GetCollectionsAsync()
    {
        return collections.Values.ToAsyncEnumerable().SelectAwait(async x => await x).OfType<ContentCollection>();
    }

}
