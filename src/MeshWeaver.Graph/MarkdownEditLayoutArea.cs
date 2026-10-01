using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// Edit and Suggest layout areas for Markdown nodes.
/// Edit: full-page Monaco editor without track changes.
/// Suggest: full-page Monaco editor with track changes enabled.
/// Both include a title text box with auto-save and a back button.
/// </summary>
public static class MarkdownEditLayoutArea
{
    /// <summary>
    /// Renders the Edit layout area — a full-page Markdown editor without track changes.
    /// </summary>
    /// <param name="host">The layout area host rendering the area.</param>
    /// <param name="_">The rendering context for the area.</param>
    /// <returns>The view for the Edit layout area.</returns>
    public static UiControl Edit(LayoutAreaHost host, RenderingContext _)
        => BuildArea(host, trackChanges: false);

    /// <summary>
    /// Renders the Suggest layout area — a full-page Markdown editor with track changes enabled.
    /// </summary>
    /// <param name="host">The layout area host rendering the area.</param>
    /// <param name="_">The rendering context for the area.</param>
    /// <returns>The view for the Suggest layout area.</returns>
    public static UiControl Suggest(LayoutAreaHost host, RenderingContext _)
        => BuildArea(host, trackChanges: true);

    private static UiControl BuildArea(LayoutAreaHost host, bool trackChanges)
        => BuildTemplate(host.Hub.Address.ToString(), trackChanges, host.ViewerLocale());

    /// <summary>
    /// The Edit / Suggest page as a TEMPLATE: every control is declared up front and bound to the
    /// node by PATH, so the area emits its whole tree on the first render and the GUI fills it in
    /// through <c>IMeshNodeStreamCache</c> (Doc/GUI/DataBinding → "Templates first, data later").
    ///
    /// <para>🚨 <b>Nothing here reads the node.</b> The area used to wait for
    /// <c>GetMeshNodeStream().Take(1)</c> before it emitted the editor and then baked the markdown
    /// into it with <c>WithValue(initialContent)</c>: the page stayed on the loading placeholder
    /// until the node's owning hub answered, and the editor was frozen on that one snapshot — an edit
    /// made elsewhere while it was open never reached it. Now the body is a node-bound
    /// <see cref="MarkdownEditorControl.Value"/> pointer (<c>content</c> on the node's
    /// <c>MarkdownContent</c>) and the title a node-bound <c>Name</c> field, both resolved and kept
    /// live on the GUI side.</para>
    ///
    /// <para>Writes are unchanged: <see cref="MarkdownEditorControl.WithAutoSave"/> still replaces
    /// the content with a fresh <c>MarkdownContent</c> (dropping a stale <c>PrerenderedHtml</c>), and
    /// the title writes its one field through the node-bound DataContext.</para>
    /// </summary>
    /// <param name="nodePath">The Markdown node being edited — the hub's own path.</param>
    /// <param name="trackChanges">Whether the editor runs in Suggest (track changes) mode.</param>
    /// <param name="locale">The viewer's locale, for the chrome's strings.</param>
    /// <returns>The complete control tree — it never waits on data.</returns>
    public static UiControl BuildTemplate(string nodePath, bool trackChanges, string? locale)
    {
        // Back to the node's default area (no hardcoded "/Overview")
        var backHref = $"/{nodePath}";

        var container = Controls.Stack
            .WithWidth("100%")
            .WithStyle("flex: 1; display: flex; flex-direction: column; min-height: 0;");

        // Header row with back button and inline title editor
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

        // Title — bound DIRECTLY to the node's Name field (node-bound DataContext, fields-mode). The
        // GUI reads it off the node stream and writes the one field back; while the node is not
        // there yet the binding draws the field empty and stays subscribed.
        headerRow = headerRow.WithView(new TextFieldControl(new JsonPointerReference(nameof(MeshNode.Name)))
            {
                Immediate = true,
                Placeholder = LocalizationCatalog.Get("ui.markdownTitlePlaceholder", locale),
                DataContext = LayoutAreaReference.GetMeshNodeDataContext(nodePath, bindContent: false)
            }.WithStyle("flex: 1; font-size: 1.25rem; font-weight: 600;")
            .WithClass("title-bar-field"));

        headerRow = headerRow.WithView(Controls.Body(LocalizationCatalog.Get("ui.autoSaved", locale))
            .WithStyle("color: var(--neutral-foreground-hint); font-size: 0.85rem;"));

        container = container.WithView(headerRow);

        // Body — the Value is a POINTER into the node's MarkdownContent, not the text: the editor view
        // resolves it through MeshNodeBindingExtensions.Bind (IMeshNodeStreamCache) and follows the
        // node live. Auto-save keeps writing the whole MarkdownContent, exactly as before.
        var editor = new MarkdownEditorControl
            {
                Value = new JsonPointerReference(MarkdownBodyPointer),
                DataContext = LayoutAreaReference.GetMeshNodeDataContext(nodePath)
            }
            .WithDocumentId(nodePath)
            .WithHeight("100%")
            .WithMaxHeight("none")
            .WithTrackChanges(trackChanges)
            .WithPlaceholder(LocalizationCatalog.Get("ui.markdownBodyPlaceholder", locale))
            .WithAutoSave(nodePath, nodePath);

        var editorWrapper = Controls.Stack
            .WithWidth("100%")
            .WithStyle("flex: 1; width: 100%; padding: 0; margin-top: 8px; min-height: 0; overflow: hidden;")
            .WithView(editor);

        container = container.WithView(editorWrapper);

        return Controls.Stack
            .WithWidth("100%")
            .WithStyle("height: calc(100vh - 100px); display: flex; flex-direction: column;")
            .WithView(container);
    }

    /// <summary>
    /// The pointer of the markdown body inside the node's content — <c>MarkdownContent.Content</c>
    /// as the hub serializes it (camelCase). Field resolution against the node is case-insensitive,
    /// so the spelling is not load-bearing; the member is.
    /// </summary>
    public const string MarkdownBodyPointer = "content";
}
