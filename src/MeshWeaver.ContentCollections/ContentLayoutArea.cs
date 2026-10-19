using System.ComponentModel;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Domain;
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
        return articleService.GetCollection(split[0])
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

        // The file read + document→markdown transform run inside the collection's pooled leaf.
        return collection.GetContentAsText(filePath, [transformer])
            .Select(markdown => (UiControl?)(markdown is null
                ? new MarkdownControl($"Document not found: {filePath}")
                : new MarkdownControl(markdown)))
            .Catch((Exception ex) => Observable.Return<UiControl?>(
                new MarkdownControl($"Error converting document {filePath}: {ex.Message}")));
    }

    private static IObservable<UiControl?> RenderImageContent(
        IIoPool ioPool,
        ContentCollection collection,
        string filePath,
        string extension)
    {
        // The full file read happens inside the collection's pooled leaf; only the (CPU-cheap)
        // base64/text conversion runs on the emission.
        return collection.GetContentBytes(filePath)
            .Select(bytes =>
            {
                if (bytes is null)
                    return (UiControl?)new MarkdownControl($"Image not found: {filePath}");

                // A platform image, bounded to the content column with its natural aspect. Every
                // format — SVG included — travels as a data URI and is drawn as an <img>, so markup in
                // an uploaded file runs no script, and a content picture is never put on the icon
                // backplate that an inline-svg ICON gets.
                return Controls.Image(new Icon(Icon.UrlProvider, $"data:{GetMimeType(extension)};base64,{Convert.ToBase64String(bytes)}"),
                        Path.GetFileName(filePath))
                    .WithMaxWidth("100%");
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
    ///
    /// <para>A TEMPLATE (Doc/GUI/DataBinding → "Templates first, data later"): the
    /// <see cref="MarkdownControl"/> is returned at once, bound to a projection of the node's
    /// markdown, and follows every later edit. It used to be built only once the node had been
    /// read, out of the text of that moment.</para>
    /// </summary>
    private static IObservable<UiControl?> RenderNodeContent(LayoutAreaHost host)
        => Observable.Return<UiControl?>(
            NodeMarkdown(host.Workspace, host.Hub.Address.ToString()).BoundMarkdown(NodeContentDataId));

    /// <summary>The data id the node's own markdown is projected under.</summary>
    internal const string NodeContentDataId = "nodeContent";

    /// <summary>The live markdown of the hub's own node, or a one-line notice when there is none.</summary>
    private static IObservable<string> NodeMarkdown(IWorkspace workspace, string hubPath)
        => (workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
                ?? Observable.Return<MeshNode[]>(Array.Empty<MeshNode>()))
            .Select(nodes => nodes.FirstOrDefault(n => n.Path == hubPath) is not { } node
                ? $"*Node not found: {hubPath}*"
                : GetMarkdownContent(node) is { } content && !string.IsNullOrWhiteSpace(content)
                    ? content
                    : "*No content available.*");

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

        // The URL id is percent-encoded by the browser; decode each segment of the file path
        // before it is matched against the collection's stored item names (RenderFile re-encodes
        // it with Uri.EscapeDataString when building the static URL — this is its inverse).
        filePath = ContentCollectionsExtensions.DecodeCollectionPath(filePath);

        if (string.IsNullOrEmpty(filePath))
            return Observable.Return<UiControl?>(new MarkdownControl($"Empty file path in: {contentPath}"));

        // Get the collection (collectionPart may include @partition)
        var articleService = host.Hub.GetContentService();
        return articleService.GetCollection(collectionPart)
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
        // The file name/path is USER content (uploads): URL-encode each path segment so a crafted
        // name cannot alter the address. The frame and the link are platform controls, which encode
        // every value they draw.
        var encodedPath = string.Join('/',
            filePath.Split('/').Select(Uri.EscapeDataString));
        // Access-controlled content route — NEVER /static (issue #587): a PDF in a private Space
        // must not be readable by URL alone.
        var contentUrl = ContentCollectionsExtensions.GetContentFileUrl(
            host.Hub.Address.ToString()!, collectionName, encodedPath);
        var fileName = Path.GetFileName(filePath);
        return Controls.Stack
            .WithWidth("100%")
            .WithVerticalGap(8)
            .WithView(Controls.Embed(contentUrl, fileName))
            .WithView(Controls.Link(host.Localize("content.downloadPdf"), contentUrl + "?download").WithDownload(fileName));
    }
}
