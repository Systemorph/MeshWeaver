using System.Reactive.Linq;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;

namespace MeshWeaver.Articles;

public static class ArticleLayoutArea
{
    private static ArticleControl RenderArticle(Article article)
    {
        return Template.Bind(article, a => new ArticleControl(a));
    }

    internal static IObservable<object> Article(LayoutAreaHost host, RenderingContext _)
    {
        var split = host.Reference.Id?.ToString()!.Split("/");
        if (split is null || split.Length < 2)
            return Observable.Return(new MarkdownControl("Path must be specified in the form of /collection/article"));
        return host.Hub.RenderArticle(split[0], string.Join('/', split.Skip(1)));
    }
 
    public static IObservable<object> RenderArticle(this IMessageHub hub, string collection, string id) =>
        hub.GetArticle(collection, id)
            ?.Select(a => a is null ? 
                (object)new MarkdownControl($"No article {id} found in collection {collection}") 
                : RenderArticle(a))
        ?? Observable.Return(new MarkdownControl($":warning: Article {id} not found in collection {collection}."));

    public static IObservable<Article> GetArticle(this IMessageHub hub, string collection, string id)
        => hub.GetArticleService().GetArticle(collection, id);

}
