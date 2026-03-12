// <meshweaver>
// Id: ReportConfig
// DisplayName: Report Configuration
// </meshweaver>

using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.ContentCollections;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

/// <summary>
/// Configures the Report node type: inherits all Markdown features (Edit, Suggest, Notebook, Thumbnail)
/// but overrides the Overview to use Controls.Markdown() which renders @@() layout area references
/// via MarkdownView/PathBasedLayoutArea instead of CollaborativeMarkdownControl.
/// </summary>
public static class ReportConfig
{
    public static MessageHubConfiguration ConfigureReport(this MessageHubConfiguration config)
        => config
            .AddDefaultLayoutAreas()
            .AddMarkdownViews()
            .AddContentCollections()
            .AddApprovals()
            .AddLayout(layout => layout
                .WithView("Overview", ReportOverview.Overview));
}

/// <summary>
/// Overview view for Report nodes. Uses Controls.Markdown() so that @@() references
/// are processed by MarkdownView.RenderNodes() into PathBasedLayoutArea components.
/// </summary>
public static class ReportOverview
{
    public static IObservable<UiControl?> Overview(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();

        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return(Array.Empty<MeshNode>());

        return nodeStream.Select(nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            return (UiControl?)BuildOverview(host, node);
        });
    }

    private static UiControl BuildOverview(LayoutAreaHost host, MeshNode? node)
    {
        var content = GetMarkdownContent(node);

        var container = Controls.Stack.WithWidth("100%")
            .WithStyle("position: relative; max-width: 1280px; margin: 0 auto; padding: 0 24px;");

        // Header with title
        var title = node?.Name ?? host.Hub.Address.ToString();
        container = container.WithView(
            Controls.Html($"<h1 style=\"margin: 0 0 24px 0; padding-bottom: 24px; border-bottom: 1px solid var(--neutral-stroke-rest);\">{System.Web.HttpUtility.HtmlEncode(title)}</h1>"));

        // Markdown content rendered via MarkdownView (processes @@ layout area references)
        if (!string.IsNullOrWhiteSpace(content))
            container = container.WithView(Controls.Markdown(content));

        // Children
        container = container.WithView(LayoutAreaControl.Children(host.Hub).WithShowProgress(false));

        return container;
    }

    private static string GetMarkdownContent(MeshNode? node)
    {
        if (node?.Content == null)
            return string.Empty;

        if (node.Content is MarkdownContent markdownContent)
            return markdownContent.Content;

        if (node.Content is string stringContent)
            return stringContent;

        if (node.Content is JsonElement jsonElement)
        {
            if (jsonElement.ValueKind == JsonValueKind.String)
                return jsonElement.GetString() ?? string.Empty;

            if (jsonElement.TryGetProperty("$type", out var typeProperty))
            {
                var typeName = typeProperty.GetString();
                if (typeName == "MarkdownDocument" && jsonElement.TryGetProperty("content", out var contentProperty))
                    return contentProperty.GetString() ?? string.Empty;
            }
        }

        return string.Empty;
    }
}
