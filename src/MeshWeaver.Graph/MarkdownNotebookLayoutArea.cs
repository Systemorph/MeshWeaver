using System.ComponentModel;
using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Kernel;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph;

/// <summary>
/// Notebook layout area for Markdown nodes.
/// Renders markdown content as interactive code/markdown cells using NotebookParser.
/// </summary>
public static class MarkdownNotebookLayoutArea
{
    /// <summary>
    /// The notebook's cells, as their own area: the one part of the page that genuinely has to
    /// COMPUTE — the cells are parsed out of the node's markdown — so it renders into a skeleton
    /// slot of the page's template rather than holding the whole page back.
    /// </summary>
    public const string CellsArea = "NotebookCells";

    /// <summary>
    /// Renders the Notebook layout area — markdown content shown as interactive code/markdown cells.
    ///
    /// <para>A TEMPLATE (Doc/GUI/DataBinding → "Templates first, data later"): the header — back
    /// link and the node's name, bound to the node by pointer — and the cells slot are emitted at
    /// once. The page used to wait for the node before it emitted anything, and its title was a
    /// snapshot of the name at that moment.</para>
    /// </summary>
    /// <param name="host">The layout area host rendering the area.</param>
    /// <param name="_">The rendering context for the area.</param>
    /// <returns>An observable stream of the view for the Notebook layout area.</returns>
    public static IObservable<UiControl?> Notebook(LayoutAreaHost host, RenderingContext _)
        => Observable.Return<UiControl?>(BuildTemplate(host.Hub.Address.ToString()));

    /// <summary>
    /// The Notebook page for <paramref name="nodePath"/> — pure, so its shape is assertable without
    /// a hub.
    /// </summary>
    /// <param name="nodePath">The Markdown node shown as a notebook — the hub's own path.</param>
    /// <returns>The complete control tree; it never waits on data.</returns>
    public static UiControl BuildTemplate(string nodePath)
    {
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

        // The title is the node's Name, read by the GUI off the node stream — live, never baked.
        headerStack = headerStack.WithView(
            (Controls.H2(new JsonPointerReference(nameof(MeshNode.Name))) with
            {
                DataContext = LayoutAreaReference.GetMeshNodeDataContext(nodePath, bindContent: false)
            }).WithStyle("margin: 0; font-size: 1.25rem;"));

        container = container.WithView(headerStack);

        var notebookArea = Controls.Stack
            .WithWidth("100%")
            .WithStyle("flex: 1; padding: 16px; overflow: auto; box-sizing: border-box;")
            .WithView(Controls.LayoutArea(new Address(nodePath), CellsArea)
                .WithSpinnerType(SpinnerType.Skeleton));

        return container.WithView(notebookArea);
    }

    /// <summary>
    /// The <see cref="CellsArea"/>: the node's markdown parsed into notebook cells. It follows the
    /// node, so an edit made elsewhere re-parses the cells.
    /// </summary>
    /// <param name="host">The layout area host rendering the area.</param>
    /// <param name="_">The rendering context for the area.</param>
    /// <returns>The notebook control, once the node has been read.</returns>
    [Browsable(false)]
    public static IObservable<UiControl?> Cells(LayoutAreaHost host, RenderingContext _)
        => host.Workspace.GetMeshNodeStream()
            .Select(node => (UiControl?)new NotebookControl()
                .WithCells(NotebookParser.ParseMarkdown(MarkdownOverviewLayoutArea.GetMarkdownContent(node) ?? string.Empty))
                .WithDefaultLanguage("csharp")
                .WithAvailableLanguages("csharp", "python", "javascript", "typescript", "fsharp", "markdown")
                .WithShowLineNumbers(true)
                .WithHeight("100%"));
}
