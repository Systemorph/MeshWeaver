using System.Reactive.Linq;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Views;
using MeshWeaver.Messaging;

namespace MeshWeaver.Articles;

public static class ArticleLayoutArea
{
    private static ArticleControl RenderArticle(Article article)
    {
        var content = article.PrerenderedHtml;

        return new ArticleControl
        {
            Name = article.Name,
            Collection = article.Collection,
            Title = article.Title,
            Abstract = article.Abstract,
            Authors = article.AuthorDetails,
            Published = article.Published,
            Tags = article.Tags,
            LastUpdated = article.LastUpdated,
            Thumbnail = article.Thumbnail,
            Html = content,
            VideoUrl = article.VideoUrl,
            PageTitle = article.Title,
            CodeSubmissions = article.CodeSubmissions,
            Meta = new Dictionary<string, object>()
            {
                ["description"] = article.Abstract, 
                ["keywords"] = string.Join(',',article.Tags), 
                ["abstract"] = article.Abstract
            },
        };
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
            .Select(a => a is null ? 
                (object)new MarkdownControl($"No article {id} found in collection {collection}") 
                : RenderArticle(a));

    public static IObservable<Article> GetArticle(this IMessageHub hub, string collection, string id)
        => hub.GetArticleService().GetArticle(collection, id);

}
