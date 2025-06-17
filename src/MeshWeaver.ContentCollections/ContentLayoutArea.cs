using System.Reactive.Linq;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;

namespace MeshWeaver.ContentCollections;

public static class ContentLayoutArea
{
    private static UiControl RenderContent(string path, object content)
    {
        return Template.Bind(content, a => GetControl(path, content));
    }

    private static UiControl GetControl(string path, object content)
    {
        if(content is Article)
            return Template.Bind(content, c => new ArticleControl(c));
        if (content is MarkdownElement md)
            return new MarkdownControl(md.Content){Html = md.PrerenderedHtml};
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

    public static IObservable<UiControl> Content(LayoutAreaHost host, RenderingContext _)
    {
        var split = host.Reference.Id?.ToString()!.Split("/");
        if (split is null || split.Length < 2)
            return Observable.Return(new MarkdownControl("Path must be specified in the form of /collection/article"));

        var articleService = host.Hub.GetContentService();
        var collection = articleService.GetCollection(split[0]);
        var path = string.Join('/', split.Skip(1));

        var contentStream = collection?.GetMarkdown(path);
        if (contentStream is null)
            return Observable.Return(new MarkdownControl($"{path} not found in collection {collection}."));

        return contentStream.Select(a => a is null ?
            (UiControl)new MarkdownControl($"{path} found not in collection {collection}")
            : RenderContent(path, a));
    }
 
    public static IObservable<UiControl> RenderArticle(this IMessageHub hub, string collection, string id) =>
        hub.GetArticle(collection, id)
            ?.Select(a => a is null ? 
                new MarkdownControl($"No article {id} found in collection {collection}") 
                : RenderContent(id, a))
        ?? Observable.Return(new MarkdownControl($":warning: Article {id} not found in collection {collection}."));

    public static IObservable<object> GetArticle(this IMessageHub hub, string collection, string id)
        => hub.GetContentService().GetArticle(collection, id);

}
