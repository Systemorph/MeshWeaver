namespace MeshWeaver.Articles;

public interface IArticleService
{
    ArticleCollection GetCollection(string collection);
    IObservable<IEnumerable<Article>> GetArticleCatalog(ArticleCatalogOptions options);
    IObservable<Article> GetArticle(string collection, string article);
}
