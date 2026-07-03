using System.ComponentModel;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.ContentCollections;

/// <summary>
/// Layout area that renders content-collection files (markdown, images, documents, and other
/// text formats) into UI controls, resolving the collection and path from the area's reference.
/// </summary>
public static class ContentLayoutArea
{
    private static UiControl RenderContent(string path, object content, bool isPresentationMode = false)
    {
        return Template.Bind(content, a => GetControl(path, content, isPresentationMode));
    }

    private static UiControl GetControl(string path, object content, bool isPresentationMode = false)
    {
        if (content is MarkdownElement md)
            return new MarkdownControl(md.Content) { Html = md.PrerenderedHtml, CodeSubmissions = md.CodeSubmissions };
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
    /// The genuine IO leaves (collection load, file streams, document transforms) bridge
    /// through the FileSystem <see cref="IIoPool"/>; the layout area itself is fully
    /// reactive — no async, no Task surface (Doc/Architecture/AsynchronousCalls.md,
    /// ControlledIoPooling.md).
    /// </summary>
    internal static IIoPool GetIoPool(IMessageHub hub)
        => hub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.FileSystem)
           ?? IoPool.Unbounded;

    /// <summary>
    /// Renders file content based on mime type.
    /// Handles unified content reference format where the path is passed via $Content area.
    /// Format: collection/path or collection@partition/path
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Content(LayoutAreaHost host, RenderingContext _)
    {
        var idString = host.Reference.Id?.ToString() ?? "";

        // Format: collection/path or collection@partition/path
        return HandleContentReference(host, idString);
    }

    private static IObservable<UiControl?> HandleContentReference(
        LayoutAreaHost host,
        string idString)
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
        return GetIoPool(host.Hub)
            .Invoke(ct => articleService.GetCollectionAsync(split[0], ct))
            .SelectMany(collection =>
            {
                if (collection is null)
                    return Observable.Return<UiControl?>(new MarkdownControl($"Collection {split[0]} not found."));
                var path = string.Join('/', split.Skip(1));

                var contentStream = collection.GetMarkdown(path);
                if (contentStream is null)
                    return Observable.Return<UiControl?>(new MarkdownControl($"{path} not found in collection {collection}."));

                return contentStream.Select(a => (UiControl?)(a is null
                    ? new MarkdownControl($"{path} not found in collection {collection}")
                    : RenderContent(path, a, isPresentationMode)));
            });
    }

    private static bool IsImageFile(string extension) => extension switch
    {
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".ico" or ".bmp" => true,
        ".svg" => true,
        _ => false
    };

    private static bool IsDocumentFile(string extension) => extension is ".docx";

    private static IObservable<UiControl?> RenderDocumentContent(
        IIoPool ioPool,
        ContentCollection collection,
        IMessageHub hub,
        string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        var transformer = hub.ServiceProvider.GetServices<IContentTransformer>()
            .FirstOrDefault(t => t.SupportedExtensions.Contains(ext));

        if (transformer == null)
            return Observable.Return<UiControl?>(new MarkdownControl($"No converter available for {ext} files"));

        // The file stream + document→markdown transform is ONE pooled async leaf —
        // async lives only inside the IIoPool bridge, never on the subscribing thread.
        return ioPool.Invoke<UiControl?>(async ct =>
            {
                await using var stream = await collection.GetContentAsync(filePath, ct);
                if (stream == null)
                    return new MarkdownControl($"Document not found: {filePath}");

                var markdown = await transformer.TransformToMarkdownAsync(stream, ct);
                return new MarkdownControl(markdown);
            })
            .Catch((Exception ex) => Observable.Return<UiControl?>(
                new MarkdownControl($"Error converting document {filePath}: {ex.Message}")));
    }

    private static IObservable<UiControl?> RenderImageContent(
        IIoPool ioPool,
        ContentCollection collection,
        string filePath,
        string extension)
    {
        // The file stream read + base64 conversion is ONE pooled async leaf.
        return ioPool.Invoke<UiControl?>(async ct =>
            {
                await using var stream = await collection.GetContentAsync(filePath, ct);
                if (stream == null)
                    return new MarkdownControl($"Image not found: {filePath}");

                if (extension == ".svg")
                {
                    // SVG can be embedded as text
                    using var reader = new StreamReader(stream);
                    var svgContent = await reader.ReadToEndAsync(ct);
                    return new HtmlControl($"<div class='embedded-svg'>{svgContent}</div>");
                }

                // For binary images, convert to base64 data URI
                using var memoryStream = new MemoryStream();
                await stream.CopyToAsync(memoryStream, ct);
                var base64 = Convert.ToBase64String(memoryStream.ToArray());
                var mimeType = GetMimeType(extension);
                var imgHtml = $"<img src='data:{mimeType};base64,{base64}' alt='{Path.GetFileName(filePath)}' style='max-width: 100%;' />";
                return new HtmlControl(imgHtml);
            })
            .Catch((Exception ex) => Observable.Return<UiControl?>(
                new MarkdownControl($"Error loading image {filePath}: {ex.Message}")));
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
    public static IObservable<UiControl?> UnifiedContent(LayoutAreaHost host, RenderingContext _)
        // Defer so a synchronously-throwing prologue surfaces through the same
        // rendered-error path the old try/catch produced.
        => Observable.Defer(() => UnifiedContentCore(host))
            .Catch((Exception ex) => Observable.Return<UiControl?>(
                new MarkdownControl($"Error loading content: {ex.Message}")));

    private static IObservable<UiControl?> UnifiedContentCore(LayoutAreaHost host)
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
        var ioPool = GetIoPool(host.Hub);
        return ioPool
            .Invoke(ct => articleService.GetCollectionAsync(collectionPart, ct))
            .SelectMany(collection =>
            {
                if (collection is null)
                    return Observable.Return<UiControl?>(new MarkdownControl($"Collection '{collectionPart}' not found. Ensure the collection is mapped using MapContentCollection."));

                return RenderFile(host, collection, collectionPart, filePath);
            });
    }

    /// <summary>
    /// Renders a single file from <paramref name="collection"/> by extension: images inline,
    /// binary documents via <see cref="IContentTransformer"/>, PDFs embedded from the static
    /// endpoint, everything else through the markdown pipeline. Shared by the unified
    /// <c>$Content</c> area and the collection-named area (<see cref="CollectionNamedLayoutArea"/>).
    /// </summary>
    internal static IObservable<UiControl?> RenderFile(
        LayoutAreaHost host, ContentCollection collection, string collectionName, string filePath)
    {
        var ioPool = GetIoPool(host.Hub);
        var extension = Path.GetExtension(filePath).ToLowerInvariant();

        // For images, get the raw content and render appropriately
        if (IsImageFile(extension))
            return RenderImageContent(ioPool, collection, filePath, extension);

        // For binary document formats, convert to markdown via IContentTransformer
        if (IsDocumentFile(extension))
            return RenderDocumentContent(ioPool, collection, host.Hub, filePath);

        // PDFs render embedded from the static endpoint (the markdown pipeline would read binary).
        if (extension == ".pdf")
            return Observable.Return<UiControl?>(RenderPdf(host, collectionName, filePath));

        // For other content types, use the existing GetMarkdown pipeline
        var contentStream = collection.GetMarkdown(filePath);
        if (contentStream is null)
            return Observable.Return<UiControl?>(new MarkdownControl($"File '{filePath}' not found in collection '{collectionName}'."));

        return contentStream.Select(content => (UiControl?)(content is null
            ? new MarkdownControl($"File '{filePath}' not found in collection '{collectionName}'")
            : RenderContent(filePath, content, false)));
    }

    private static UiControl RenderPdf(LayoutAreaHost host, string collectionName, string filePath)
    {
        var contentUrl =
            $"/static/{host.Hub.Address}/{ContentCollectionsExtensions.EncodeCollectionName(collectionName)}/{filePath}";
        var fileName = Path.GetFileName(filePath);
        return new HtmlControl(
            $@"<div style=""width: 100%; min-height: 500px;"">
                <iframe src=""{contentUrl}"" style=""width: 100%; height: 600px; border: 1px solid var(--neutral-stroke-rest); border-radius: 4px;"" title=""{fileName}""></iframe>
                <div style=""margin-top: 8px;"">
                    <a href=""{contentUrl}?download"" download=""{fileName}"">Download PDF</a>
                </div>
            </div>");
    }
}
