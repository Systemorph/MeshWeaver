using System.Reactive.Linq;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;

namespace MeshWeaver.Articles;

public static class ContentLayoutArea
{
    private static ArticleControl RenderArticle(Article article)
    {
        return Template.Bind(article, a => new ArticleControl(a));
    }

    public static IObservable<UiControl> Content(LayoutAreaHost host, RenderingContext _)
    {
        var split = host.Reference.Id?.ToString()!.Split("/");
        if (split is null || split.Length < 2)
            return Observable.Return(new MarkdownControl("Path must be specified in the form of /collection/article"));

        var articleService = host.Hub.GetArticleService();
        var collection = articleService.GetCollection(split[0]);
        var id = string.Join('/', split.Skip(1));

        var articleStream = collection?.GetArticle(id);
        if (articleStream is null)
            return Observable.Return(new MarkdownControl($":warning: Article {id} not found in collection {collection}."));

        return articleStream.Select(a => a is null ?
            (UiControl)new MarkdownControl($"No article {id} found in collection {collection}")
            : RenderArticle(a));
    }
 
    public static IObservable<UiControl> RenderArticle(this IMessageHub hub, string collection, string id) =>
        hub.GetArticle(collection, id)
            ?.Select(a => a is null ? 
                (UiControl)new MarkdownControl($"No article {id} found in collection {collection}") 
                : RenderArticle(a))
        ?? Observable.Return(new MarkdownControl($":warning: Article {id} not found in collection {collection}."));

    public static IObservable<Article> GetArticle(this IMessageHub hub, string collection, string id)
        => hub.GetArticleService().GetArticle(collection, id);

}
