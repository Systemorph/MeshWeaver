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

        // Default: try to find a collection by address ID
        return GetCollection(address.Id);
    }
}
