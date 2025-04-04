using System.Reactive.Linq;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MeshWeaver.Articles;

public class ArticleService : IArticleService
{
    private readonly IMessageHub hub;
    private readonly UserService userService;

    public ArticleService(IServiceProvider serviceProvider, IMessageHub hub, UserService userService)
    {
        this.hub = hub;
        this.userService = userService;
        var configs = serviceProvider.GetRequiredService<IOptions<List<ArticleSourceConfig>>>();
        collections = configs.Value.Select(CreateCollection).ToDictionary(x => x.Collection);
    }

    private ArticleCollection CreateCollection(ArticleSourceConfig config)
    {
        var factory = hub.ServiceProvider.GetKeyedService<IArticleCollectionFactory>(config.SourceType);
        if(factory is null)
            throw new ArgumentException($"Unknown source type {config.SourceType}");
        return factory.Create(config);
    }

    private readonly IReadOnlyDictionary<string, ArticleCollection> collections;

    public ArticleCollection GetCollection(string collection)
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
                .SelectMany(x => x)
                .Select(FilterArticles)
                .Skip(catalogOptions.Page * catalogOptions.PageSize)
                .Take(catalogOptions.PageSize)
                .FirstAsync())
            .ToArray()
            ;
    }

    private IEnumerable<Article> FilterArticles(IEnumerable<Article> articles)
    {
        if (userService.Context is not null && userService.Context.Roles.Contains(Roles.PortalAdmin))
            return articles;
        var now = DateTime.UtcNow;
        return articles
            .Where(a => a.Published is not null && a.Published <= now)
;
    }

    public IObservable<Article> GetArticle(string collection, string article)
    {
        return GetCollection(collection)?.GetArticle(article);
    }

    public Task<IReadOnlyCollection<ArticleCollection>> GetCollectionsAsync(CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyCollection<ArticleCollection>>(collections.Values.ToArray());
    }
}
