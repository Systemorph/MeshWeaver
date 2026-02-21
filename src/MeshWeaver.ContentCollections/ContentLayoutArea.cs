using System.ComponentModel;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
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

    /// <summary>
    /// Renders file content based on mime type.
    /// Handles unified content reference format where the path is passed via $Content area.
    /// Format: collection/path or collection@partition/path
    /// </summary>
    [Browsable(false)]
    public static async Task<IObservable<UiControl?>> Content(LayoutAreaHost host, RenderingContext _, CancellationToken ct)
    {
        var idString = host.Reference.Id?.ToString() ?? "";

        // Format: collection/path or collection@partition/path
        return await HandleContentReference(host, idString, ct);
    }

    private static async Task<IObservable<UiControl?>> HandleContentReference(
        LayoutAreaHost host,
        string idString,
        CancellationToken ct)
    {
        // First split by ? to separate path from query parameters, then split path by /
        var pathPart = idString.Split('?')[0];
        var split = pathPart.Split('/');

        if (split is null || split.Length < 2)
            return Observable.Return<UiControl?>(new MarkdownControl("Path must be specified in the form of /collection/article"));

        // Parse query parameters from LayoutAreaReference
        var isPresentationMode = host.Reference.HasParameter("presentation") &&
                                 host.Reference.GetParameterValue("presentation")?.ToLower() == "true";

        var articleService = host.Hub.GetContentService();
        var collection = await articleService.GetCollectionAsync(split[0], ct);
        if (collection is null)
            return Observable.Return<UiControl?>(new MarkdownControl($"Collection {split[0]} not found."));
        var path = string.Join('/', split.Skip(1));

        var contentStream = collection.GetMarkdown(path);
        if (contentStream is null)
            return Observable.Return<UiControl?>(new MarkdownControl($"{path} not found in collection {collection}."));

        return contentStream.Select(a => a is null ?
            new MarkdownControl($"{path} not found in collection {collection}")
            : RenderContent(path, a, isPresentationMode));
    }

    private static bool IsImageFile(string extension) => extension switch
    {
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".ico" or ".bmp" => true,
        ".svg" => true,
        _ => false
    };

    private static async Task<IObservable<UiControl?>> RenderImageContent(
        ContentCollection collection,
        string filePath,
        string extension,
        CancellationToken ct)
    {
        try
        {
            await using var stream = await collection.GetContentAsync(filePath, ct);
            if (stream == null)
                return Observable.Return<UiControl?>(new MarkdownControl($"Image not found: {filePath}"));

            if (extension == ".svg")
            {
                // SVG can be embedded as text
                using var reader = new StreamReader(stream);
                var svgContent = await reader.ReadToEndAsync(ct);
                return Observable.Return<UiControl?>(new HtmlControl($"<div class='embedded-svg'>{svgContent}</div>"));
            }

            // For binary images, convert to base64 data URI
            using var memoryStream = new MemoryStream();
            await stream.CopyToAsync(memoryStream, ct);
            var base64 = Convert.ToBase64String(memoryStream.ToArray());
            var mimeType = GetMimeType(extension);
            var imgHtml = $"<img src='data:{mimeType};base64,{base64}' alt='{Path.GetFileName(filePath)}' style='max-width: 100%;' />";
            return Observable.Return<UiControl?>(new HtmlControl(imgHtml));
        }
        catch (Exception ex)
        {
            return Observable.Return<UiControl?>(new MarkdownControl($"Error loading image {filePath}: {ex.Message}"));
        }
    }

    private static string GetMimeType(string extension) => extension switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".ico" => "image/x-icon",
        ".bmp" => "image/bmp",
        ".svg" => "image/svg+xml",
        _ => "application/octet-stream"
    };

    /// <summary>
    /// Renders the MeshNode's own content from the Content property.
    /// Used when $Content area is accessed without a path.
    /// </summary>
    private static IObservable<UiControl?> RenderNodeContent(LayoutAreaHost host)
    {
        var hubPath = host.Hub.Address.ToString();

        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return<MeshNode[]>(Array.Empty<MeshNode>());

        return nodeStream.Select(nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            if (node == null)
                return new MarkdownControl($"*Node not found: {hubPath}*");

            var content = GetMarkdownContent(node);
            if (string.IsNullOrWhiteSpace(content))
                return new MarkdownControl("*No content available.*");

            return new MarkdownControl(content);
        });
    }

    /// <summary>
    /// Extracts markdown content from a MeshNode's Content property.
    /// Handles MarkdownDocument JSON format with $type and content fields.
    /// </summary>
    private static string GetMarkdownContent(MeshNode node)
    {
        if (node.Content == null)
            return string.Empty;

        // Handle MarkdownContent (from MarkdownFileParser)
        if (node.Content is MarkdownContent markdownContent)
            return markdownContent.Content;

        // Handle MarkdownDocument content (JSON with $type and content fields)
        if (node.Content is JsonElement jsonElement)
        {
            if (jsonElement.TryGetProperty("$type", out var typeProperty))
            {
                var typeName = typeProperty.GetString();
                if (typeName == "MarkdownDocument" && jsonElement.TryGetProperty("content", out var contentProperty))
                {
                    return contentProperty.GetString() ?? string.Empty;
                }
            }

            // Try to get content directly if it's just a string JSON value
            if (jsonElement.ValueKind == JsonValueKind.String)
            {
                return jsonElement.GetString() ?? string.Empty;
            }
        }

        // Handle string content directly
        if (node.Content is string strContent)
            return strContent;

        return string.Empty;
    }

    /// <summary>
    /// Handles unified content references ($Content area).
    /// The host.Reference.Id contains the path like "Markdown/images/meshbros.png"
    /// Format:
    ///   - (empty) - renders the MeshNode's own content from the Content property
    ///   - path (uses default collection)
    ///   - collection/path
    ///   - collection@partition/path
    /// </summary>
    [Browsable(false)]
    public static async Task<IObservable<UiControl?>> UnifiedContent(LayoutAreaHost host, RenderingContext _, CancellationToken ct)
    {
        try
        {
            var contentPath = host.Reference.Id?.ToString() ?? "";

            // If no path specified, render the node's own content from the MeshNode.Content property
            if (string.IsNullOrEmpty(contentPath))
            {
                return RenderNodeContent(host);
            }

            // Split collection from file path
            // If no slash, use the default collection name
            var firstSlash = contentPath.IndexOf('/');
            string collectionPart;
            string filePath;

            if (firstSlash < 0)
            {
                // No slash - use default collection
                collectionPart = ContentCollectionsExtensions.DefaultCollectionName;
                filePath = contentPath;
            }
            else
            {
                collectionPart = contentPath[..firstSlash];
                filePath = contentPath[(firstSlash + 1)..];
            }

            if (string.IsNullOrEmpty(filePath))
                return Observable.Return<UiControl?>(new MarkdownControl($"Empty file path in: {contentPath}"));

            // Get the collection (collectionPart may include @partition)
            var articleService = host.Hub.GetContentService();
            var collection = await articleService.GetCollectionAsync(collectionPart, ct);
            if (collection is null)
                return Observable.Return<UiControl?>(new MarkdownControl($"Collection '{collectionPart}' not found. Ensure the collection is mapped using MapContentCollection."));

            var extension = Path.GetExtension(filePath).ToLowerInvariant();

            // For images, get the raw content and render appropriately
            if (IsImageFile(extension))
            {
                return await RenderImageContent(collection, filePath, extension, ct);
            }

            // For other content types, use the existing GetMarkdown pipeline
            var contentStream = collection.GetMarkdown(filePath);
            if (contentStream is null)
                return Observable.Return<UiControl?>(new MarkdownControl($"File '{filePath}' not found in collection '{collectionPart}'."));

            return contentStream.Select(content => content is null
                ? new MarkdownControl($"File '{filePath}' not found in collection '{collectionPart}'")
                : RenderContent(filePath, content, false));
        }
        catch (Exception ex)
        {
            return Observable.Return<UiControl?>(new MarkdownControl($"Error loading content: {ex.Message}"));
        }
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
