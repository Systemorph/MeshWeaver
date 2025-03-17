using System.Reactive.Linq;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Views;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.Articles;

public static class ArticleLayoutArea
{
    private static ArticleControl RenderArticle(this IMessageHub hub, Article article)
    {
        var content = article.PrerenderedHtml;
        if (article.CodeSubmissions is not null && article.CodeSubmissions.Any())
        {
            var kernel = new KernelAddress();
            foreach (var s in article.CodeSubmissions)
                hub.Post(s, o => o.WithTarget(kernel));
            content = article.PrerenderedHtml.Replace(ExecutableCodeBlockRenderer.KernelAddressPlaceholder, kernel.ToString());
        }

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
            Meta = new Dictionary<string, object>()
            {
                ["description"] = article.Abstract, 
                ["keywords"] = string.Join(',',article.Tags), 
                ["abstract"] = article.Abstract
            },
        };
    }

    public static IObservable<object> Article(LayoutAreaHost host, RenderingContext ctx)
    {
        var split = host.Reference.Id?.ToString()!.Split("/");
        if (split is null || split.Length < 2)
            return Observable.Return(new MarkdownControl("Path must be specified in the form of /collection/article"));
        return host.Hub.RenderArticle(split[0], string.Join('/', split.Skip(1)));
    }
 
    public static IObservable<object> RenderArticle(this IMessageHub hub, string collection, string id) =>
        hub.GetArticle(collection, id)
            .Select(a => a is null ? (object)new MarkdownControl($"No article {id} found in collection {collection}") : hub.RenderArticle(a));

    public static IObservable<Article> GetArticle(this IMessageHub hub, string collection, string id)
        => hub.GetArticleService().GetArticle(collection, id);

}
