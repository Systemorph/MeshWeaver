using System.Collections.Concurrent;
using System.Reactive.Linq;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.ContentCollections;

/// <summary>
/// Default <see cref="IContentService"/> implementation. Aggregates collection configs from all
/// registered providers, lazily instantiates and caches <see cref="ContentCollection"/> instances,
/// resolves mapped collections, and falls back to the parent hub's content service when a
/// collection is not found locally.
/// </summary>
public class ContentService : IContentService
{
    private readonly IMessageHub hub;
    private readonly AccessService accessService;
    private readonly ConcurrentDictionary<string, ContentCollectionConfig> collectionConfigs;
    // Promise cache: each entry is a ReplaySubject-backed one-shot (ContentCollection.Initialize
    // via Pool.Run) — the first subscriber triggers creation, every later one replays it.
    // Values are observables, never resolved single values, so late subscribers and concurrent
    // first-callers share exactly one initialization.
    private readonly ConcurrentDictionary<string, IObservable<ContentCollection?>> collections = new();

    // Cache for resolved mapped configs
    private readonly ConcurrentDictionary<string, ContentCollectionConfig> resolvedMappedConfigs = new();

    // Lazy parent lookup to avoid circular dependency during construction
    private IContentService? parentContentService;
    private bool parentResolved;
    private readonly Lock parentLock = new();
    private readonly ILogger<ContentService> logger;
    /// <summary>
    /// Initializes the content service, collecting collection configurations from every
    /// registered <see cref="IContentCollectionConfigProvider"/> (later registrations win).
    /// </summary>
    /// <param name="hub">The owning message hub.</param>
    /// <param name="accessService">The access service used for permission checks.</param>
    public ContentService(IMessageHub hub, AccessService accessService)
    {
        this.hub = hub;
        this.accessService = accessService;

        // Add collections from providers (later registrations override earlier ones)
        var providers = hub.ServiceProvider.GetServices<IContentCollectionConfigProvider>();
        collectionConfigs = new(providers
            .SelectMany(p => p.GetCollections())
            .GroupBy(c => c.Name)
            .ToDictionary(g => g.Key, g => g.Last()));
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

        // Ensure the directory exists for FileSystem source type
        if (sourceConfig.SourceType == "FileSystem" && !string.IsNullOrEmpty(fullPath))
        {
            try
            {
                Directory.CreateDirectory(fullPath);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to create content directory '{Path}'", fullPath);
            }
        }

        var resolvedConfig = sourceConfig with
        {
            Name = mappedConfig.Name,
            BasePath = fullPath,
            Address = mappedConfig.Address,
            IsEditable = mappedConfig.IsEditable,
            Settings = sourceConfig.Settings is { } src
                ? new Dictionary<string, string>(src) { ["BasePath"] = fullPath }
                : new Dictionary<string, string> { ["BasePath"] = fullPath }
        };

        // Cache the resolved config
        resolvedMappedConfigs[mappedConfig.Name] = resolvedConfig;

        return resolvedConfig;
    }

    /// <summary>
    /// Composes provider creation + collection initialization as a cold observable — no async
    /// bridging. <see cref="ContentCollection.Initialize"/> is itself the ReplaySubject-backed,
    /// pool-run promise cache, so the parse runs only when this pipeline is first subscribed.
    /// </summary>
    private IObservable<ContentCollection?> CreateCollection(ContentCollectionConfig config)
    {
        var factory = hub.ServiceProvider.GetKeyedService<IStreamProviderFactory>(config.SourceType);
        if (factory is null)
            return Observable.Throw<ContentCollection?>(
                new ArgumentException($"Unknown source type {config.SourceType}"));

        return factory.Create(config)
            .Take(1)
            .Select(provider => new ContentCollection(config, provider, hub))
            .SelectMany(collection => collection.Initialize().Select(_ => (ContentCollection?)collection));
    }

    /// <inheritdoc />
    public IObservable<ContentCollection?> GetCollection(string collection)
    {
        // Try local collections first (matches GetCollectionConfig's local-first pattern)
        if (collections.TryGetValue(collection, out var localCollection))
            return localCollection;

        var config = collectionConfigs.GetValueOrDefault(collection);
        if (config is not null)
        {
            // Resolve mapped config lazily before initializing
            if (config.SourceType == MappedContentCollectionConfigProvider.MappedSourceType)
            {
                config = ResolveMappedConfig(config);
                if (config == null)
                    return Observable.Return<ContentCollection?>(null);
            }
            return InitializeCollection(config);
        }

        // Delegate to parent if not found locally
        var parent = GetParentContentService();
        if (parent is not null)
            return parent.GetCollection(collection);

        return Observable.Return<ContentCollection?>(null);
    }

    private IObservable<ContentCollection?> InitializeCollection(ContentCollectionConfig config)
    {
        collectionConfigs[config.Name] = config;
        // Per-name single-flight via GetOrAdd; the entry replays creation to every subscriber.
        // A failed creation logs, evicts its cache entry (so the next access retries with a
        // fresh pipeline) and resolves null — callers see "collection not found", not a fault
        // replayed forever.
        return collections.GetOrAdd(config.Name, name =>
            CreateCollection(config)
                .Catch((Exception ex) =>
                {
                    logger.LogWarning(ex, "Creating content collection '{Collection}' failed", name);
                    collections.TryRemove(name, out _);
                    return Observable.Return<ContentCollection?>(null);
                })
                .Replay(1)
                .AutoConnect(1));
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
    public IReadOnlyCollection<ContentCollectionConfig> GetAllCollectionConfigs()
    {
        // Get local configs, resolving any mapped configs
        // Filter out configs with ExposeInChildren=false (hidden backing stores)
        var configs = collectionConfigs.Values
            .Select(config => config.SourceType == MappedContentCollectionConfigProvider.MappedSourceType
                ? ResolveMappedConfig(config)
                : config)
            .Where(c => c != null && c.ExposeInChildren)
            .Cast<ContentCollectionConfig>()
            .ToList();

        // Add parent configs that aren't overridden locally
        var parent = GetParentContentService();
        if (parent is not null)
        {
            var parentConfigs = parent.GetAllCollectionConfigs();
            foreach (var parentConfig in parentConfigs)
            {
                if (!collectionConfigs.ContainsKey(parentConfig.Name) && parentConfig.ExposeInChildren)
                {
                    configs.Add(parentConfig);
                }
            }
        }

        return configs;
    }

    /// <inheritdoc />
    public void AddConfiguration(ContentCollectionConfig contentCollectionConfig)
    {
        var name = contentCollectionConfig.Name;
        var existing = collectionConfigs.GetValueOrDefault(name);
        this.collectionConfigs[name] = contentCollectionConfig;
        // Invalidate cached collection when BasePath changes (e.g., switching between
        // Articles and Reports). Skip when new config has no BasePath (e.g., ContentPage
        // adds HubStreamProvider configs that shouldn't evict a working FileSystem collection).
        if (existing != null
            && !string.IsNullOrEmpty(contentCollectionConfig.BasePath)
            && existing.BasePath != contentCollectionConfig.BasePath)
            this.collections.TryRemove(name, out _);
    }

    /// <inheritdoc />
    public IObservable<Stream?> GetContent(string collection, string path)
        => GetCollection(collection)
            .SelectMany(coll => coll is null
                ? Observable.Throw<Stream?>(new ArgumentException($"Collection '{collection}' not found"))
                : coll.GetContent(path));

    /// <inheritdoc />
    public IObservable<ContentCollection> GetCollections()
        => collections.Values.ToArray()
            .Merge()
            .Where(c => c is not null)
            .Select(c => c!);

}
