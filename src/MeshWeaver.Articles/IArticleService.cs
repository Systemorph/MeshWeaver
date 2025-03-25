namespace MeshWeaver.Articles;

public interface IArticleService
{
    Task<Stream> GetContentAsync(string collection, string path, CancellationToken ct = default);
    Task<IReadOnlyCollection<Article>> GetArticleCatalog(ArticleCatalogOptions options, CancellationToken ct = default);
    IObservable<Article> GetArticle(string collection, string article);

    Task<IReadOnlyCollection<ArticleCollection>> GetCollectionsAsync(CancellationToken ct = default);
}
