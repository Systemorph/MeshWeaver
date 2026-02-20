using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph;

/// <summary>
/// Edit and Suggest layout areas for Markdown nodes.
/// Edit: full-page Monaco editor without track changes.
/// Suggest: full-page Monaco editor with track changes enabled.
/// Both include a title text box with auto-save and a back button.
/// </summary>
public static class MarkdownEditLayoutArea
{
    public static UiControl Edit(LayoutAreaHost host, RenderingContext _)
        => BuildArea(host, trackChanges: false);

    public static UiControl Suggest(LayoutAreaHost host, RenderingContext _)
        => BuildArea(host, trackChanges: true);

    private static UiControl BuildArea(LayoutAreaHost host, bool trackChanges)
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
                return BuildEditContent(host, node, hubPath, hubAddress, content, trackChanges);
            }));
    }

    private static UiControl BuildEditContent(
        LayoutAreaHost host,
        MeshNode? node,
        string hubPath,
        Address hubAddress,
        string initialContent,
        bool trackChanges)
    {
        var backHref = $"/{hubPath}";

        var container = Controls.Stack
            .WithWidth("100%")
            .WithHeight("100%");

        // Header row with back button
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

        headerRow = headerRow.WithView(Controls.Html("<div style=\"flex: 1;\"></div>"));

        headerRow = headerRow.WithView(
            Controls.Html("<span style=\"color: var(--neutral-foreground-hint); font-size: 0.85rem;\">Changes are saved automatically</span>"));

        container = container.WithView(headerRow);

        // Title text box with auto-save
        if (node != null)
        {
            var dataId = $"editTitle_{hubPath.Replace("/", "_")}";
            var props = MeshNodeProperties.FromNode(node);
            host.UpdateData(dataId, props);
            SetupNodePropertiesAutoSave(host, dataId, props, node);

            var titleField = new TextFieldControl(new JsonPointerReference("Name"))
            {
                Immediate = true,
                Label = "Title",
                DataContext = LayoutAreaReference.GetDataPointer(dataId)
            }.WithStyle("margin: 8px 8px 0 8px;");

            container = container.WithView(titleField);
        }

        // MarkdownEditorControl with auto-save
        var lineCount = string.IsNullOrEmpty(initialContent) ? 15 : initialContent.Split('\n').Length;
        var editorHeight = Math.Clamp(lineCount * 22 + 60, 300, 2000);
        var editor = new MarkdownEditorControl()
            .WithDocumentId(hubPath)
            .WithValue(initialContent)
            .WithHeight($"{editorHeight}px")
            .WithMaxHeight("none")
            .WithTrackChanges(trackChanges)
            .WithPlaceholder("Start writing your markdown content...")
            .WithAutoSave(hubAddress.ToString(), hubPath);

        var editorWrapper = Controls.Stack
            .WithWidth("100%")
            .WithStyle("flex: 1; width: 100%; padding: 0;")
            .WithView(editor);

        container = container.WithView(editorWrapper);

        return container;
    }

    private static void SetupNodePropertiesAutoSave(
        LayoutAreaHost host,
        string dataId,
        MeshNodeProperties initial,
        MeshNode node)
    {
        var current = (object)initial;

        host.RegisterForDisposal($"autosave_{dataId}",
            host.Stream.GetDataStream<object>(dataId)
                .Throttle(TimeSpan.FromMilliseconds(300))
                .Subscribe(updated =>
                {
                    if (object.Equals(current, updated))
                        return;

                    current = updated;

                    if (updated is not MeshNodeProperties updatedProps)
                        return;

                    var updatedNode = updatedProps.ApplyTo(node);

                    host.Hub.Post(
                        new DataChangeRequest { ChangedBy = host.Stream.ClientId }.WithUpdates(updatedNode),
                        o => o.WithTarget(host.Hub.Address));
                }));
    }
}
