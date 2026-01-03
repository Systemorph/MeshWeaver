using System.Collections.Concurrent;
using System.Reactive.Linq;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.ContentCollections;

public class ContentService : IContentService
{
    private readonly IMessageHub hub;
    private readonly AccessService accessService;
    private readonly ConcurrentDictionary<string, ContentCollectionConfig> collectionConfigs;
    private readonly Dictionary<string, Task<ContentCollection?>> collections = new();
    private readonly Lock initializeLock = new();

    // Cache for resolved mapped configs
    private readonly ConcurrentDictionary<string, ContentCollectionConfig> resolvedMappedConfigs = new();

    // Lazy parent lookup to avoid circular dependency during construction
    private IContentService? parentContentService;
    private bool parentResolved;
    private readonly Lock parentLock = new();
    private readonly ILogger<ContentService> logger;
    public ContentService(IMessageHub hub, AccessService accessService)
    {
        this.hub = hub;
        this.accessService = accessService;

        // Add collections from providers
        var providers = hub.ServiceProvider.GetServices<IContentCollectionConfigProvider>();
        collectionConfigs = new(providers.SelectMany(p => p.GetCollections()).ToDictionary(c => c.Name));
        logger = hub.ServiceProvider.GetRequiredService<ILogger<ContentService>>();
    }

    private IContentService? GetParentContentService()
    {
        if (parentResolved)
            return parentContentService;

        lock (parentLock)
        {
            if (parentResolved)
                return parentContentService;

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

            parentResolved = true;
            return parentContentService;
        }
    }

    /// <summary>
    /// Resolves a mapped config by looking up the source collection and applying the subdirectory.
    /// This is called lazily when the collection is accessed, avoiding circular dependencies.
    /// </summary>
    private ContentCollectionConfig? ResolveMappedConfig(ContentCollectionConfig mappedConfig)
    {
        if (mappedConfig.SourceType != MappedContentCollectionConfigProvider.MappedSourceType)
            return mappedConfig;

        // Check cache first
        if (resolvedMappedConfigs.TryGetValue(mappedConfig.Name, out var cached))
            return cached;

        var settings = mappedConfig.Settings;
        if (settings == null ||
            !settings.TryGetValue(MappedContentCollectionConfigProvider.SourceCollectionKey, out var sourceCollectionName) ||
            !settings.TryGetValue(MappedContentCollectionConfigProvider.SubdirectoryKey, out var subdirectory))
        {
            return null;
        }

        // Look up the source collection from parent content service
        var parent = GetParentContentService() ?? this;
        ContentCollectionConfig? sourceConfig = null;

        sourceConfig = parent.GetCollectionConfig(sourceCollectionName);

        if (sourceConfig == null)
        {
            logger.LogDebug(
                "No source collection '{Source}' found for mapped collection '{Mapped}'", sourceCollectionName, mappedConfig.Name);
            return null;
        }

        // Resolve base path to absolute path if it's relative
        var basePath = sourceConfig.BasePath ?? "";
        if (!string.IsNullOrEmpty(basePath) && !Path.IsPathRooted(basePath))
        {
            basePath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), basePath));
        }

        // Create derived config with combined path
        var fullPath = string.IsNullOrEmpty(subdirectory)
            ? basePath
            : Path.Combine(basePath, subdirectory);

        var resolvedConfig = sourceConfig with
        {
            Name = mappedConfig.Name,
            BasePath = fullPath,
            Address = mappedConfig.Address,
            Settings = new Dictionary<string, string>(sourceConfig.Settings ?? new Dictionary<string, string>())
            {
                ["BasePath"] = fullPath
            }
        };

        // Cache the resolved config
        resolvedMappedConfigs[mappedConfig.Name] = resolvedConfig;

        return resolvedConfig;
    }

    private async Task<ContentCollection?> CreateCollectionAsync(ContentCollectionConfig config, CancellationToken cancellationToken)
    {
        var factory = hub.ServiceProvider.GetKeyedService<IStreamProviderFactory>(config.SourceType);
        if (factory is null)
            throw new ArgumentException($"Unknown source type {config.SourceType}");

        // Create provider using the factory (now properly async)
        var provider = await factory.CreateAsync(config, cancellationToken);

        // Create and initialize the collection
        var collection = new ContentCollection(config, provider, hub);
        await collection.InitializeAsync(cancellationToken);

        return collection;
    }


    public async Task<ContentCollection?> GetCollectionAsync(string collection, CancellationToken ct)
    {
        var parent = GetParentContentService();
        if (parent is not null)
        {
            var fromParent = await parent.GetCollectionAsync(collection, ct);
            if (fromParent is not null)
                return fromParent;
        }
        // Try local collections first
        if (collections.TryGetValue(collection, out var localCollection))
            return await localCollection;

        var config = collectionConfigs.GetValueOrDefault(collection);
        if (config is not null)
        {
            // Resolve mapped config lazily before initializing
            if (config.SourceType == MappedContentCollectionConfigProvider.MappedSourceType)
            {
                config = ResolveMappedConfig(config);
                if (config == null)
                    return null;
            }
            return await InitializeCollectionAsync(config, ct);
        }

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

    public ContentCollectionConfig? GetCollectionConfig(string collection)
    {
        // Try local configs first
        if (collectionConfigs.TryGetValue(collection, out var config))
        {
            // Resolve mapped config lazily
            if (config.SourceType == MappedContentCollectionConfigProvider.MappedSourceType)
            {
                return ResolveMappedConfig(config);
            }
            return config;
        }

        // Delegate to parent if not found locally
        var parent = GetParentContentService();
        if (parent is not null)
            return parent.GetCollectionConfig(collection);

        return null;
    }

    public IReadOnlyCollection<ContentCollectionConfig> GetAllCollectionConfigs()
    {
        // Get local configs, resolving any mapped configs
        var configs = collectionConfigs.Values
            .Select(config => config.SourceType == MappedContentCollectionConfigProvider.MappedSourceType
                ? ResolveMappedConfig(config)
                : config)
            .Where(c => c != null)
            .Cast<ContentCollectionConfig>()
            .ToList();

        // Add parent configs that aren't overridden locally
        var parent = GetParentContentService();
        if (parent is not null)
        {
            var parentConfigs = parent.GetAllCollectionConfigs();
            foreach (var parentConfig in parentConfigs)
            {
                if (!collectionConfigs.ContainsKey(parentConfig.Name))
                {
                    configs.Add(parentConfig);
                }
            }
        }

        return configs;
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

        var allCollectionsList = new List<ContentCollection?>();
        foreach (var x in catalogOptions.Collections ?? [])
        {
            var valueOrDefault = collections.GetValueOrDefault(x);
            var result = valueOrDefault is null ? null : await valueOrDefault;
            allCollectionsList.Add(result);
        }
        var allCollections = allCollectionsList.ToArray();

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
        return collections.Values.ToAsyncEnumerable().Select(async x => await x).OfType<ContentCollection>();
    }

}
