using System.Reactive.Linq;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MeshWeaver.Articles;

public class ContentService : IContentService
{
    private readonly IMessageHub hub;
    private readonly AccessService accessService;

    public ContentService(IServiceProvider serviceProvider, IMessageHub hub, AccessService accessService)
    {
        this.hub = hub;
        this.accessService = accessService;
        var configs = serviceProvider.GetRequiredService<IOptions<List<ArticleSourceConfig>>>();
        collections = configs.Value.Select(CreateCollection).ToDictionary(x => x.Collection);
    }

    private ContentCollection CreateCollection(ArticleSourceConfig config)
    {
        var factory = hub.ServiceProvider.GetKeyedService<IArticleCollectionFactory>(config.SourceType);
        if(factory is null)
            throw new ArgumentException($"Unknown source type {config.SourceType}");
        return factory.Create(config);
    }

    private readonly IReadOnlyDictionary<string, ContentCollection> collections;

    public ContentCollection GetCollection(string collection)
        => collections.GetValueOrDefault(collection);

    public Task<Stream> GetContentAsync(string collection, string path, CancellationToken ct = default)
    {
        var coll = GetCollection(collection);
        return coll.GetContentAsync(path, ct);
    }

    public async Task<IReadOnlyCollection<Article>> GetArticleCatalog(ArticleCatalogOptions catalogOptions,
        CancellationToken ct)
    {
        var allCollections = 
            string.IsNullOrEmpty(catalogOptions.Collection)
            ? collections.Values
            : [collections[catalogOptions.Collection]];
        return (await allCollections.Select(c => c.GetArticles(catalogOptions))
                .CombineLatest()
                .Select(c => c.SelectMany(articles => articles))
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

    public IObservable<Article> GetArticle(string collection, string article)
    {
        return GetCollection(collection)?.GetArticle(article);
    }

    public Task<IReadOnlyCollection<ContentCollection>> GetCollectionsAsync(CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyCollection<ContentCollection>>(collections.Values.ToArray());
    }
}
