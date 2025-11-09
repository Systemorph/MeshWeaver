using System.Reactive.Linq;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;

namespace MeshWeaver.ContentCollections;

public static class ContentLayoutArea
{
    private static UiControl RenderContent(string path, object content, bool isPresentationMode = false)
    {
        return Template.Bind(content, a => GetControl(path, content, isPresentationMode));
    }

    private static UiControl GetControl(string path, object content, bool isPresentationMode = false)
    {
        if (content is Article article)
        {
            return new ArticleControl(article) { IsPresentationMode = isPresentationMode };
        }
        if (content is MarkdownElement md)
            return new MarkdownControl(md.Content) { Html = md.PrerenderedHtml };
        if (content is string str)
        {
            return Path.GetExtension(path) switch
            {
                ".html" => new HtmlControl(str),
                ".xml" => new MarkdownControl($"```xml\n{str}\n```"),
                ".json" => new MarkdownControl($"```json\n{str}\n```"),
                ".cs" => new MarkdownControl($"```csharp\n{str}\n```"),
                ".js" => new MarkdownControl($"```javascript\n{str}\n```"),
                ".ts" => new MarkdownControl($"```typescript\n{str}\n```"),
                ".css" => new MarkdownControl($"```css\n{str}\n```"),
                ".yaml" => new MarkdownControl($"```yaml\n{str}\n```"),
                ".sql" => new MarkdownControl($"```sql\n{str}\n```"),
                ".py" => new MarkdownControl($"```python\n{str}\n```"),
                ".r" => new MarkdownControl($"```r\n{str}\n```"),
                _ => new MarkdownControl(str)
            };
        }

        return new MarkdownControl($"Unknown content type {content.GetType().Name}");
    }

    public static async Task<IObservable<UiControl?>> Content(LayoutAreaHost host, RenderingContext context, CancellationToken ct)
    {
        // First split by ? to separate path from query parameters, then split path by /
        var idString = host.Reference.Id?.ToString() ?? "";
        var pathPart = idString.Split('?')[0];
        var split = pathPart.Split('/');

        if (split is null || split.Length < 2)
            return Observable.Return(new MarkdownControl("Path must be specified in the form of /collection/article"));

        // Parse query parameters from LayoutAreaReference
        var isPresentationMode = host.Reference.HasParameter("presentation") &&
                                 host.Reference.GetParameterValue("presentation")?.ToLower() == "true";

        var articleService = host.Hub.GetContentService();
        var collection = await articleService.GetCollectionAsync(split[0], ct);
        if (collection is null)
            return Observable.Return(new MarkdownControl($"Collection {split[0]} not found."));
        var path = string.Join('/', split.Skip(1));

        var contentStream = collection?.GetMarkdown(path);
        if (contentStream is null)
            return Observable.Return(new MarkdownControl($"{path} not found in collection {collection}."));

        return contentStream.Select(a => a is null ?
            new MarkdownControl($"{path} found not in collection {collection}")
            : RenderContent(path, a, isPresentationMode));
    }

    public static async Task<IObservable<UiControl?>> RenderArticle(this IMessageHub hub, string collection, string id, CancellationToken ct)
    {
        var article = await hub.GetArticleAsync(collection, id, ct);
        return article.Select(a => a is null
            ? new MarkdownControl($"No article {id} found in collection {collection}")
            : RenderContent(id, a));
    }

    public static Task<IObservable<object?>> GetArticleAsync(this IMessageHub hub, string collection, string id, CancellationToken ct)
        => hub.GetContentService().GetArticleAsync(collection, id, ct);

}
