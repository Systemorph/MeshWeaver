using MeshWeaver.Articles;

namespace MeshWeaver.Hosting.PostgreSql;

public class PostgreSqlArticleService() : IArticleService
{
    public Task<Stream> GetContentAsync(string collection, string path, CancellationToken ct = default)
    {
        throw new NotImplementedException();
    }

    public Task<IReadOnlyCollection<Article>> GetArticleCatalog(ArticleCatalogOptions options, CancellationToken ct)
    {
        throw new NotImplementedException();
    }

    public IObservable<Article> GetArticle(string collection, string article)
    {
        throw new NotImplementedException();
    }

    public Task<IReadOnlyCollection<ArticleCollection>> GetCollectionsAsync(CancellationToken ct = default)
    {
        throw new NotImplementedException();
    }
}
