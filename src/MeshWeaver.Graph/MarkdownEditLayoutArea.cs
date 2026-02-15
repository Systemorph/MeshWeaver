using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Domain;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph;

/// <summary>
/// Edit layout area for Markdown nodes.
/// Uses MarkdownEditorControl with auto-save and track changes support.
/// </summary>
public static class MarkdownEditLayoutArea
{
    public static UiControl Edit(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var hubAddress = host.Hub.Address;

        var nodeStream = host.Workspace.GetStream<MeshNode>()
            ?? Observable.Return<MeshNode[]?>(null);

        return Controls.Stack
            .WithWidth("100%")
            .WithHeight("100%")
            .WithView((h, ctx) => nodeStream.Take(1).Select(nodes =>
            {
                var node = nodes?.FirstOrDefault(n => n.Path == hubPath);
                var content = MarkdownOverviewLayoutArea.GetMarkdownContent(node);
                return BuildEditContent(node, hubPath, hubAddress, content);
            }));
    }

    private static UiControl BuildEditContent(
        MeshNode? node,
        string hubPath,
        Address hubAddress,
        string initialContent)
    {
        var nodeName = node?.Name ?? hubPath.Split('/').LastOrDefault() ?? "Document";
        var backHref = $"/{hubPath}";

        var container = Controls.Stack
            .WithWidth("100%")
            .WithHeight("100%");

        // Header row with back button and node name
        var headerRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithWidth("100%")
            .WithHeight("48px")
            .WithVerticalAlignment(VerticalAlignment.Center)
            .WithHorizontalGap(12)
            .WithStyle("padding: 0 8px; border-bottom: 1px solid var(--neutral-stroke-rest); flex-shrink: 0;");

        headerRow = headerRow.WithView(Controls.Button("")
            .WithIconStart(FluentIcons.ArrowLeft(IconSize.Size16))
            .WithAppearance(Appearance.Stealth)
            .WithNavigateToHref(backHref));

        headerRow = headerRow.WithView(
            Controls.Html($"<h2 style=\"margin: 0; font-size: 1.1rem; font-weight: 600;\">{System.Web.HttpUtility.HtmlEncode(nodeName)}</h2>"));

        headerRow = headerRow.WithView(Controls.Html("<div style=\"flex: 1;\"></div>"));

        headerRow = headerRow.WithView(
            Controls.Html("<span style=\"color: var(--neutral-foreground-hint); font-size: 0.85rem;\">Changes are saved automatically</span>"));

        container = container.WithView(headerRow);

        // MarkdownEditorControl with auto-save
        var editor = new MarkdownEditorControl()
            .WithDocumentId(hubPath)
            .WithValue(initialContent)
            .WithHeight("calc(100vh - 150px)")
            .WithMaxHeight("none")
            .WithTrackChanges(true)
            .WithPlaceholder("Start writing your markdown content...")
            .WithAutoSave(hubAddress.ToString(), hubPath);

        var editorWrapper = Controls.Stack
            .WithWidth("100%")
            .WithStyle("flex: 1; width: 100%; padding: 0;")
            .WithView(editor);

        container = container.WithView(editorWrapper);

        return container;
    }
}
