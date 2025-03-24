using MeshWeaver.Articles;

namespace MeshWeaver.Hosting.PostgreSql;

public class PostgreSqlArticleService(IServiceProvider serviceProvider) : IArticleService
{
    public Task<Stream> GetContentAsync(string collection, string path, CancellationToken ct = default)
    {
        throw new NotImplementedException();
    }

    public IObservable<IEnumerable<Article>> GetArticleCatalog(ArticleCatalogOptions options)
    {
        throw new NotImplementedException();
    }

    public IObservable<Article> GetArticle(string collection, string article)
    {
        throw new NotImplementedException();
    }
}
