using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;

namespace MeshWeaver.Articles;

public static class ArticleLayoutArea
{


    private static ArticleControl RenderArticle(this Article article)
    {
        return new ArticleControl
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
    }

    public static IObservable<object> Article(LayoutAreaHost host, RenderingContext ctx)
    {
        var collection = host.Hub.Address.GetCollectionName();
        var source = host.Hub.GetCollection(collection);
        return source.GetArticle(host.Reference.Id.ToString())
            .Select(RenderArticle);
    }




    public static LayoutAreaReference GetArticleLayoutReference(string path, Func<ArticleOptions, ArticleOptions> options = null)
        => new(nameof(Article)) { Id = path }; // TODO V10: Create sth with options (22.01.2025, Roland Bürgi)


}
