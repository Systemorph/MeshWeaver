using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// The hub-side PROJECTIONS that feed the default node page's bound controls — the
/// "rows computed on the hub → <c>/data</c>, bound by pointer" row of Doc/GUI/DataBinding →
/// "Templates first, data later".
///
/// <para>The page itself (<see cref="MeshNodeLayoutAreas.BuildDetailsTemplate"/>, the provenance
/// strip, the Edit page) is a TEMPLATE: it is emitted at once, carrying pointers. What it shows that
/// cannot be bound straight to a node field — the title with its fallbacks, the icon inherited from
/// the partition root, the provenance stamps in the viewer's zone and language, the markdown body
/// rendered to HTML with its duplicate heading stripped — is computed HERE, from the hub's own
/// node, and fed into <c>/data</c> by <see cref="Template"/>'s stream <c>Bind</c>, which subscribes
/// in the control's buildup and is disposed with its area. Nothing the page renders waits on these
/// streams, and every later emission of the node updates the bound values in place.</para>
///
/// <para>Each projection is a pure <c>Select</c> over the node stream. Whatever depends on the
/// VIEWER (the time zone, the language) is captured by the caller on the render turn, where the
/// ambient identity is the viewer's, and passed in.</para>
/// </summary>
internal static class NodePageProjections
{
    /// <summary>What the node page's header shows.</summary>
    /// <param name="Style">The header's style — hidden for a node excluded from the header context.</param>
    /// <param name="Title">The node's name, else its id, else the hub path.</param>
    /// <param name="Icon">The icon to draw: a Fluent name, an image URL, inline svg or a glyph.</param>
    /// <param name="IconWidth">The icon's drawn size.</param>
    /// <param name="TileStyle">The icon tile's style — dashed while the node has no icon.</param>
    /// <param name="MetaHtml">The provenance line.</param>
    internal sealed record HeaderView(
        string Style, string Title, string Icon, string IconWidth, string TileStyle, string MetaHtml);

    /// <summary>The provenance strip a framework-composed landing page carries.</summary>
    /// <param name="Style">The strip's style — hidden for a node excluded from the header context.</param>
    /// <param name="MetaHtml">The provenance line.</param>
    internal sealed record MetaView(string Style, string MetaHtml);

    /// <summary>The node's markdown body.</summary>
    /// <param name="Markdown">The raw markdown (empty when the node carries none).</param>
    /// <param name="Html">The pre-rendered HTML, or null.</param>
    /// <param name="Style">The body's style — hidden when there is no body.</param>
    internal sealed record BodyView(string Markdown, string? Html, string Style);

    /// <summary>A single markdown text.</summary>
    /// <param name="Markdown">The markdown.</param>
    internal sealed record TextView(string Markdown);

    /// <summary>The style that hides a bound section whose node says it has nothing to show.</summary>
    internal const string Hidden = "display: none;";

    internal const string TileStyle =
        "width: 56px; height: 56px; display: flex; align-items: center; justify-content: center; border-radius: 10px; background: var(--neutral-layer-2); flex-shrink: 0;";

    internal const string EmptyTileStyle = TileStyle
        + " border: 2px dashed var(--neutral-stroke-rest); background: transparent; color: var(--neutral-foreground-hint);";

    /// <summary>
    /// The viewer-dependent half of the provenance line, captured on the render turn: the zone the
    /// stamps are shown in and the language the labels are in.
    /// </summary>
    /// <param name="ZoneId">The viewer's IANA zone, or null for UTC.</param>
    /// <param name="Locale">The viewer's language tag.</param>
    internal sealed record Viewer(string? ZoneId, string? Locale)
    {
        /// <summary>Reads the viewer off <paramref name="host"/>. Call it on the render turn.</summary>
        public static Viewer Of(LayoutAreaHost host)
        {
            var access = host.Hub.ServiceProvider.GetService<AccessService>();
            return new(access?.Context?.TimeZoneId ?? access?.CircuitContext?.TimeZoneId, host.ViewerLocale());
        }
    }

    /// <summary>The header of the hub's own node.</summary>
    internal static IObservable<HeaderView> Header(LayoutAreaHost host, string visibleStyle, Viewer viewer)
    {
        var hubPath = host.Hub.Address.ToString();
        return host.Workspace.GetMeshNodeStream()
            .CombineLatest(host.Workspace.ObservePartitionRoot(host.Hub.Address.Path),
                (node, partitionRoot) => ProjectHeader(node, partitionRoot, hubPath, visibleStyle, viewer))
            .DistinctUntilChanged();
    }

    /// <summary>The provenance strip of the hub's own node.</summary>
    internal static IObservable<MetaView> Meta(LayoutAreaHost host, string visibleStyle, Viewer viewer)
        => host.Workspace.GetMeshNodeStream()
            .Select(node => new MetaView(
                IsHeaderless(node) ? Hidden : visibleStyle,
                MeshNodeLayoutAreas.MetaRowHtml(
                    MeshNodeLayoutAreas.BuildMetaEntries(node, viewer.ZoneId), viewer.Locale)))
            .DistinctUntilChanged();

    /// <summary>The markdown body of the hub's own node.</summary>
    internal static IObservable<BodyView> Body(LayoutAreaHost host, string visibleStyle)
        => host.Workspace.GetMeshNodeStream()
            .Select(node => OverviewLayoutArea.MarkdownBodyText(node) is { } body
                ? new BodyView(body.Markdown ?? string.Empty, body.Html, visibleStyle)
                : new BodyView(string.Empty, null, Hidden))
            .DistinctUntilChanged();

    /// <summary>The description a NodeType DEFINITION's page shows in place of a property form.</summary>
    internal static IObservable<TextView> TypeInfo(LayoutAreaHost host)
        => host.Workspace.GetMeshNodeStream()
            .Select(node => new TextView(node?.Content is NodeTypeDefinition definition && definition.Description is { } description
                ? description
                : $"Built-in type for managing {node?.Name ?? node?.NodeType ?? "content"} nodes."))
            .DistinctUntilChanged();

    /// <summary>
    /// Keeps <c>/data/{dataId}</c> a ONE-WAY live mirror of the node's Content for as long as
    /// <paramref name="control"/>'s area lives — the read-only labels a property form derives from
    /// <c>/data</c> (dimension, options, formatted date) read it there. Writes never go through it:
    /// the form binds its fields to the node itself.
    ///
    /// <para>Registered in the control's BUILDUP and disposed with its area, exactly as
    /// <see cref="Template"/>'s stream <c>Bind</c> does — so it opens once per rendered area, never
    /// once per render (the #606 accumulation the previous <c>ReplaceDisposable</c> key guarded
    /// against cannot arise).</para>
    /// </summary>
    internal static TControl MirrorContent<TControl>(this TControl control, string nodePath, string dataId)
        where TControl : UiControl
        => (TControl)control.WithBuildup((host, context, store) =>
        {
            var subscription = host.Workspace.GetMeshNodeStream(nodePath)
                .Select(n => n?.Content)
                .Where(c => c is not null)
                .Subscribe(content => host.UpdateData(dataId, content!));
            host.RegisterForDisposal(context.Area, subscription);
            return new(store, [], null);
        });

    /// <summary>
    /// Opens the icon picker for the hub's own node. A CLICK, not a render: it reads the node once,
    /// when the viewer asks for the dialog, and the dialog binds from there.
    /// </summary>
    internal static void OpenIconPicker(LayoutAreaHost host, UiActionContext ctx)
        => host.Workspace.GetMeshNodeStream()
            .Where(node => node is not null)
            .Take(1)
            .Subscribe(
                node => ctx.Host.UpdateArea(DialogControl.DialogArea, NodeIconPickerDialog.Build(host, node!)),
                ex => host.Hub.ServiceProvider.GetService<ILoggerFactory>()
                    ?.CreateLogger(typeof(NodePageProjections))
                    .LogWarning(ex, "Could not open the icon picker for {Path}: its node could not be read.",
                        host.Hub.Address));

    private static bool IsHeaderless(MeshNode? node)
        => node?.IsExcludedFromContext(MeshNodeVisibility.HeaderContext) == true;

    private static HeaderView ProjectHeader(
        MeshNode? node, MeshNode? partitionRoot, string hubPath, string visibleStyle, Viewer viewer)
    {
        var (icon, width, tile) = IconOf(node, partitionRoot);
        return new HeaderView(
            IsHeaderless(node) ? Hidden : visibleStyle,
            node?.Name ?? node?.Id ?? hubPath,
            icon, width, tile,
            MeshNodeLayoutAreas.MetaRowHtml(MeshNodeLayoutAreas.BuildMetaEntries(node, viewer.ZoneId), viewer.Locale));
    }

    /// <summary>
    /// The icon tile's content, in the shapes <see cref="MeshNodeLayoutAreas.BuildClickableIcon"/>
    /// draws: an image or inline svg at 48px, a Fluent icon (the node's OWN name for it) at 36px, a
    /// glyph at 30px, and a dashed "+" placeholder while the node has none.
    /// </summary>
    private static (string Icon, string Width, string Tile) IconOf(MeshNode? node, MeshNode? partitionRoot)
    {
        var iconValue = MeshNodeImageHelper.ResolveNodeIcon(node, partitionRoot);
        if (string.IsNullOrEmpty(iconValue))
            return ("+", "20px", EmptyTileStyle);
        if (iconValue.StartsWith("data:") || iconValue.StartsWith("http") || iconValue.StartsWith("/")
            || iconValue.TrimStart().StartsWith("<svg", StringComparison.OrdinalIgnoreCase))
            return (iconValue, "48px", TileStyle);
        if (node?.Icon is { } raw && MeshNodeImageHelper.IsFluentIconName(raw))
            return (raw, "36px", TileStyle);
        return (iconValue, "34px", TileStyle);
    }
}
