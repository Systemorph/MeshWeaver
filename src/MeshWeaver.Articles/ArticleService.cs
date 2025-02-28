using System.Reactive.Linq;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MeshWeaver.Articles;

public class ArticleService : IArticleService
{
    private readonly IMessageHub hub;

    public ArticleService(IServiceProvider serviceProvider, IMessageHub hub)
    {
        this.hub = hub;
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

    public IObservable<IEnumerable<Article>> GetArticleCatalog(ArticleCatalogOptions catalogOptions)
    {
        return collections.Values.Select(c => c.GetArticles(catalogOptions))
            .CombineLatest()
            .Select(x => x
                .SelectMany(y => y)
                .OrderByDescending(a => a.Published))
                ;
    }

    public IObservable<Article> GetArticle(string collection, string article)
    {
        return GetCollection(collection)?.GetArticle(article);
    }
}
