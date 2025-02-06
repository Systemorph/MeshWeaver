using System.Reactive.Linq;
using MeshWeaver.Data;
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
            Authors = article.Authors,
            Published = article.Published,
            Tags = article.Tags,
            LastUpdated = article.LastUpdated,
            Thumbnail = article.Thumbnail,
            Html = content,
            VideoUrl = article.VideoUrl
        };
    }

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
            .Select(host.Hub.RenderArticle);
    }





}
