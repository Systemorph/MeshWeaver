using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Articles;

public static class ArticleLayoutArea
{
    private static ArticleControl RenderArticle(this Article article) =>
        new()
        {
            Name = article.Name,
            Collection = article.Collection,
            Title = article.Title,
            Abstract = article.Abstract,
            Authors = article.Authors,
            Published = article.Published,
            Tags = article.Tags,
            LastUpdated = article.LastUpdated,
            Thumbnail = article.Thumbnail,
            Content = article.PrerenderedHtml
        };

    public static IObservable<object> Article(LayoutAreaHost host, RenderingContext ctx)
    {
        var collectionName = host.Hub.Address.GetCollectionName();
        var collection = host.Hub.GetCollection(collectionName);
        if (collection is null)
            return Observable.Return(new MarkdownControl($"No collection {collectionName} is configured. "));
        var stream = collection.GetArticle(host.Reference.Id.ToString());
        if(stream is null)
            return Observable.Return(new MarkdownControl($"No article {host.Reference.Id} found in collection {collectionName}"));
        return stream
            .Select(RenderArticle);
    }




    public static LayoutAreaReference GetArticleLayoutReference(string path, Func<ArticleOptions, ArticleOptions> options = null)
        => new(nameof(Article)) { Id = path }; // TODO V10: Create sth with options (22.01.2025, Roland Bürgi)


}
