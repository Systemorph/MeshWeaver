namespace MeshWeaver.Articles;

public interface IArticleService
{
    Task<Stream> GetContentAsync(string collection, string path, CancellationToken ct = default);
    IObservable<IEnumerable<Article>> GetArticleCatalog(ArticleCatalogOptions options);
    IObservable<Article> GetArticle(string collection, string article);
}
