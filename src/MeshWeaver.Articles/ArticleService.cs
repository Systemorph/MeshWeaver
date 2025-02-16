using System.Reactive.Linq;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;

namespace MeshWeaver.Articles;

public class ArticleService : IArticleService
{
    public ArticleService(IMeshCatalog meshCatalog, IMessageHub hub)
    {
        Configuration = meshCatalog.Configuration.GetListOfLambdas().Aggregate(new ArticleConfiguration(hub), (l,c) => c.Invoke(l));
    }

    public ArticleConfiguration Configuration { get; }

    public ArticleCollection GetCollection(string collection)
        => Configuration.Collections.GetValueOrDefault(collection);

    public IObservable<IEnumerable<Article>> GetArticleCatalog(ArticleCatalogOptions catalogOptions)
    {
        return Configuration.Collections.Values.Select(c => c.GetArticles(catalogOptions))
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
