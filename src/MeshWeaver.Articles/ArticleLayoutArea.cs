using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;

namespace MeshWeaver.Articles;

public static class ArticleLayoutArea
{


    private static HtmlControl RenderArticle(this LayoutDefinition host, object obj)
    {
        if (obj is not Article article)
            return null;

        return new HtmlControl(article.PrerenderedHtml).AddSkin(article.MapToSkin());
    }

    private static ArticleSkin MapToSkin(this Article article)
    {
        return new ArticleSkin
        {
            Name = article.Name,
            Collection = article.Collection,
            Title = article.Title,
            Abstract = article.Abstract,
            Authors = article.Authors,
            Published = article.Published,
            Tags = article.Tags,
        };
    }

    public static IObservable<object> Article(LayoutAreaHost host, RenderingContext ctx)
    {
        var collection = host.Hub.Address.GetCollectionName();
        var source = host.Hub.GetCollection(collection);
        return source.GetArticle(host.Reference.Id.ToString());
    }




    public static LayoutAreaReference GetArticleLayoutReference(string path, Func<ArticleOptions, ArticleOptions> options = null)
        => new(nameof(Article)) { Id = path }; // TODO V10: Create sth with options (22.01.2025, Roland Bürgi)


}
