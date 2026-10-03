// <meshweaver>
// Id: ArticleLayoutAreas
// DisplayName: Northwind Article Views
// </meshweaver>

using System.Globalization;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Views for Northwind Article nodes — both TEMPLATES (Doc/GUI/DataBinding → "Templates first,
/// data later"): emitted whole on the first render, filled in as the article's node arrives, and
/// live after that.
/// </summary>
public static class ArticleLayoutAreas
{
    /// <summary>The <c>/data</c> id the header feed writes to.</summary>
    public const string HeaderDataId = "articleHeader";

    /// <summary>
    /// Registers article views with the layout definition.
    /// </summary>
    /// <param name="layout">The layout definition.</param>
    /// <returns>The layout definition with the views added.</returns>
    public static LayoutDefinition AddArticleLayoutAreas(this LayoutDefinition layout) =>
        layout
            .WithView("Overview", Overview)
            .WithView("Thumbnail", Thumbnail);

    /// <summary>
    /// Overview view showing article content with metadata header.
    /// </summary>
    /// <param name="host">The area host; the header feed reads its node.</param>
    /// <param name="_">The rendering context.</param>
    /// <returns>The overview template.</returns>
    public static UiControl Overview(LayoutAreaHost host, RenderingContext _)
        => OverviewTemplate(host.Hub.Address.ToString(), HeaderFeed(host));

    /// <summary>
    /// Thumbnail view for catalog display: the PATH alone — the thumbnail view reads the node's
    /// name, abstract and image itself, live.
    /// </summary>
    /// <param name="host">The area host; only its address is read.</param>
    /// <param name="_">The rendering context.</param>
    /// <returns>The thumbnail, bound by path.</returns>
    public static UiControl Thumbnail(LayoutAreaHost host, RenderingContext _)
    {
        var path = host.Hub.Address.ToString();
        return new MeshNodeThumbnailControl(path, path);
    }

    /// <summary>
    /// The article page. The title and the body are pointers into the node (its <c>name</c>, its
    /// <see cref="MarkdownContent"/>'s <c>content</c>), resolved by the GUI; the metadata line —
    /// authors, date, tags, the thumbnail image — has to be COMPOSED, so it is a markdown control
    /// bound to <c>/data/articleHeader</c>, which <paramref name="header"/> fills.
    /// </summary>
    /// <param name="nodePath">The article node.</param>
    /// <param name="header">The composed metadata markdown, as it changes.</param>
    /// <returns>The complete control tree — it never waits on data.</returns>
    public static UiControl OverviewTemplate(string nodePath, IObservable<string> header) =>
        Controls.Stack.WithWidth("100%")
            .WithStyle("max-width: 960px; margin: 0 auto; padding: 0 24px;")
            .WithView(Controls.H1(new JsonPointerReference("name")) with
            {
                DataContext = LayoutAreaReference.GetMeshNodeDataContext(nodePath, bindContent: false)
            })
            .WithView(header.Bind(markdown => Controls.Markdown(markdown), HeaderDataId))
            .WithView(Controls.Markdown(new JsonPointerReference("content")) with
            {
                DataContext = LayoutAreaReference.GetMeshNodeDataContext(nodePath)
            });
        // No children section — children are injected inline with the @@(query) operator, or
        // browsed via the Catalog / Search areas.

    /// <summary>
    /// The FEED half of the overview: the metadata markdown on every emission of the node. Builds
    /// no control. A failure is logged and shown as text — reported, never swallowed.
    /// </summary>
    /// <param name="host">The area host whose node is read.</param>
    /// <returns>The metadata markdown.</returns>
    public static IObservable<string> HeaderFeed(LayoutAreaHost host)
    {
        var culture = CultureInfo.GetCultureInfo(host.ViewerLocale());
        return host.Workspace.GetMeshNodeStream()
            .Select(node => Header(node, node.ContentAs<MarkdownContent>(host.Hub.JsonSerializerOptions), culture))
            .DistinctUntilChanged()
            .Catch<string, Exception>(ex =>
            {
                host.Hub.ServiceProvider.GetRequiredService<ILoggerFactory>()
                    .CreateLogger(nameof(ArticleLayoutAreas))
                    .LogWarning(ex, "The header of article {Path} could not be read", host.Hub.Address);
                return Observable.Return("*The article's details could not be read.*");
            });
    }

    /// <summary>
    /// The metadata markdown — authors · date · tags, then the thumbnail image. Pure.
    /// <c>ContentAs</c>, never <c>as</c>, reads the content: a node stored as bare JSON stays a raw
    /// <c>JsonElement</c>, and <c>as</c> would silently drop the whole header.
    /// </summary>
    /// <param name="node">The article node, or <c>null</c> while there is none.</param>
    /// <param name="content">Its markdown content.</param>
    /// <param name="culture">The viewer's culture, for the date.</param>
    /// <returns>The markdown; empty when there is nothing to show.</returns>
    public static string Header(MeshNode? node, MarkdownContent? content, CultureInfo culture)
    {
        if (node is null)
            return string.Empty;

        var parts = new List<string>();
        if (content?.Authors?.Count > 0)
            parts.Add(string.Join(", ", content.Authors));
        if (node.LastModified != default)
            parts.Add(node.LastModified.ToString("D", culture));
        if (parts.Count > 0 && content?.Tags?.Count > 0)
            parts.Add(string.Join(" ", content.Tags.Select(t => $"`{t}`")));

        var lines = new List<string>();
        if (parts.Count > 0)
            lines.Add(string.Join(" · ", parts));

        if (!string.IsNullOrEmpty(content?.Thumbnail))
        {
            var thumbnail = content.Thumbnail;
            var src = thumbnail.StartsWith("/") || thumbnail.StartsWith("http") || string.IsNullOrEmpty(node.Namespace)
                ? thumbnail
                : $"/api/content/{node.Namespace}/{thumbnail}";
            lines.Add($"![]({src})");
        }
        return string.Join("\n\n", lines);
    }
}
