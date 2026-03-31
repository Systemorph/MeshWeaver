using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Domain;
using MeshWeaver.Kernel;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph;

/// <summary>
/// Notebook layout area for Markdown nodes.
/// Renders markdown content as interactive code/markdown cells using NotebookParser.
/// </summary>
public static class MarkdownNotebookLayoutArea
{
    public static IObservable<UiControl?> Notebook(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();

        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return(Array.Empty<MeshNode>());

        return nodeStream.Select(nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            return BuildNotebookView(host, node);
        });
    }

    private static UiControl BuildNotebookView(LayoutAreaHost host, MeshNode? node)
    {
        var nodePath = node?.Path ?? host.Hub.Address.ToString();
        var title = node?.Name ?? "Notebook";

        var container = Controls.Stack
            .WithWidth("100%")
            .WithStyle("height: 100%; display: flex; flex-direction: column;");

        // Header with back button and title
        var headerStack = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithWidth("100%")
            .WithStyle("align-items: center; gap: 16px; padding: 16px 24px; border-bottom: 1px solid var(--neutral-stroke-rest); flex-shrink: 0;");

        var readHref = $"/{nodePath}/{MarkdownLayoutAreas.OverviewArea}";
        headerStack = headerStack.WithView(
            Controls.Button("")
                .WithIconStart(FluentIcons.ArrowLeft(IconSize.Size16))
                .WithAppearance(Appearance.Stealth)
                .WithNavigateToHref(readHref));

        headerStack = headerStack.WithView(
            Controls.Html($"<h2 style=\"margin: 0; font-size: 1.25rem;\">{System.Web.HttpUtility.HtmlEncode(title)}</h2>"));

        container = container.WithView(headerStack);

        // Parse markdown content into cells
        var content = MarkdownOverviewLayoutArea.GetMarkdownContent(node);
        var cells = NotebookParser.ParseMarkdown(content ?? string.Empty);

        var notebook = new NotebookControl()
            .WithCells(cells)
            .WithDefaultLanguage("csharp")
            .WithAvailableLanguages("csharp", "python", "javascript", "typescript", "fsharp", "markdown")
            .WithShowLineNumbers(true)
            .WithHeight("100%");

        var notebookArea = Controls.Stack
            .WithWidth("100%")
            .WithStyle("flex: 1; padding: 16px; overflow: auto; box-sizing: border-box;")
            .WithView(notebook);

        container = container.WithView(notebookArea);

        return container;
    }
}
