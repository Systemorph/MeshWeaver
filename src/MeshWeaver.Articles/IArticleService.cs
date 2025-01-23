namespace MeshWeaver.Articles;

public interface IArticleService
{
    //IObservable<MeshArticle> GetArticle(string collection, string path, Func<ArticleOptions, ArticleOptions> options = null);

    ArticleCollection GetCollection(string collection);
}
