using System.Collections.Immutable;
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
    /// <param name="Icon">The icon to draw (an <see cref="ImageControl"/> source): a Fluent name, an image URL, inline svg or a glyph.</param>
    /// <param name="IconSize">The icon's box side, a CSS length.</param>
    /// <param name="IconFit">How a picture fills that box — one of <see cref="ImageFit"/>.</param>
    /// <param name="TileStyle">The icon tile's style — dashed while the node has no icon.</param>
    /// <param name="Meta">The provenance line.</param>
    internal sealed record HeaderView(
        string Style, string Title, string Icon, string IconSize, string IconFit, string TileStyle, MetaLine Meta);

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

    /// <summary>The tile of a Fluent icon: the icon takes the accent colour, as it always has.</summary>
    internal const string FluentTileStyle = TileStyle + " color: var(--accent-fill-rest);";

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
                .OfType<object>()
                .Subscribe(content => host.UpdateData(dataId, content));
            host.RegisterForDisposal(context.Area, subscription);
            return new(store, [], null);
        });

    /// <summary>
    /// Opens the icon picker for the hub's own node. A CLICK, not a render: it reads the node once,
    /// when the viewer asks for the dialog, and the dialog binds from there.
    /// </summary>
    internal static void OpenIconPicker(LayoutAreaHost host, UiActionContext ctx)
        => host.Workspace.GetMeshNodeStream()
            .OfType<MeshNode>()
            .Take(1)
            .Subscribe(
                node => ctx.Host.UpdateArea(DialogControl.DialogArea, NodeIconPickerDialog.Build(host, node)),
                ex => host.Hub.ServiceProvider.GetService<ILoggerFactory>()
                    ?.CreateLogger(typeof(NodePageProjections))
                    .LogWarning(ex, "Could not open the icon picker for {Path}: its node could not be read.",
                        host.Hub.Address));

    private static bool IsHeaderless(MeshNode? node)
        => node?.IsExcludedFromContext(MeshNodeVisibility.HeaderContext) == true;

    private static HeaderView ProjectHeader(
        MeshNode? node, MeshNode? partitionRoot, string hubPath, string visibleStyle, Viewer viewer)
    {
        var tile = IconTile.Of(MeshNodeImageHelper.ResolveNodeIcon(node, partitionRoot), node?.Icon);
        return new HeaderView(
            IsHeaderless(node) ? Hidden : visibleStyle,
            node?.Name ?? node?.Id ?? hubPath,
            tile.Icon, tile.Size, tile.Fit, tile.Style,
            MetaLine.Of(MeshNodeLayoutAreas.BuildMetaEntries(node, viewer.ZoneId), viewer.Locale));
    }

}

/// <summary>
/// The node icon tile as DATA: the <see cref="ImageControl"/> source, its box, its fit and the
/// tile's style. ONE decision, shared by the hub-built header
/// (<see cref="MeshNodeLayoutAreas.BuildClickableIcon"/>) and the bound header template, so the two
/// cannot drift: an image or inline svg at 48px (a raster picture cropped to fill, a vector
/// contained), a Fluent icon (the node's OWN name for it) at 36px in the accent colour, a glyph in a
/// 38px box (an <see cref="ImageControl"/> draws a glyph at 0.8 of its box — the 30px text glyph the
/// tile always showed), and a dashed "+" placeholder while the node has none.
/// </summary>
/// <param name="Icon">The image source — the resolved icon, the node's own Fluent name, or "+" for none.</param>
/// <param name="Size">The image box side, a CSS length.</param>
/// <param name="Fit">One of <see cref="ImageFit"/>.</param>
/// <param name="Style">The tile's style.</param>
internal sealed record IconTile(string Icon, string Size, string Fit, string Style)
{
    /// <summary>Decides the tile for a resolved icon value and the node's raw one.</summary>
    /// <param name="iconValue">The resolved icon (<see cref="MeshNodeImageHelper.ResolveNodeIcon(MeshNode?, MeshNode?)"/>).</param>
    /// <param name="rawIcon">The node's own <c>Icon</c> field.</param>
    public static IconTile Of(string? iconValue, string? rawIcon)
    {
        if (string.IsNullOrEmpty(iconValue))
            return new("+", "24px", ImageFit.Contain, NodePageProjections.EmptyTileStyle);
        if (iconValue.TrimStart().StartsWith("<svg", StringComparison.OrdinalIgnoreCase))
            return new(iconValue, "48px", ImageFit.Contain, NodePageProjections.TileStyle);
        if (iconValue.StartsWith("data:") || iconValue.StartsWith("http") || iconValue.StartsWith("/"))
            return new(iconValue, "48px",
                iconValue.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ? ImageFit.Contain : ImageFit.Cover,
                NodePageProjections.TileStyle);
        if (rawIcon is { } raw && MeshNodeImageHelper.IsFluentIconName(raw))
            return new(raw, "36px", ImageFit.Contain, NodePageProjections.FluentTileStyle);
        return new(iconValue, "38px", ImageFit.Contain, NodePageProjections.TileStyle);
    }
}

/// <summary>
/// The provenance line as DATA, in three fixed slots — the node type (a link to its Configuration
/// area), the creation stamp, and the last change (the node's own, or the newest activity beneath a
/// container). Every label is already in the viewer's language. An absent segment carries the
/// hidden style and empty text, so the bound row keeps one shape and never says something false.
/// </summary>
/// <param name="TypeStyle">The type segment's style — hidden when the node has no type.</param>
/// <param name="TypeLabel">"Type:", localized.</param>
/// <param name="TypeText">The type's short name.</param>
/// <param name="TypeHref">The type's Configuration page.</param>
/// <param name="CreatedStyle">The creation segment's style.</param>
/// <param name="CreatedLabel">"Created:", localized.</param>
/// <param name="CreatedText">The stamp, with "by {who}" in the viewer's language.</param>
/// <param name="ChangedStyle">The change segment's style.</param>
/// <param name="ChangedLabel">"Updated:" or "Last activity:", localized.</param>
/// <param name="ChangedText">The stamp, "by {who}", and what happened, as authored.</param>
internal sealed record MetaLine(
    string TypeStyle, string TypeLabel, string TypeText, string TypeHref,
    string CreatedStyle, string CreatedLabel, string CreatedText,
    string ChangedStyle, string ChangedLabel, string ChangedText)
{
    /// <summary>The style of a visible segment.</summary>
    internal const string Visible = "align-items: center;";

    /// <summary>
    /// Projects the segments (<see cref="MeshNodeLayoutAreas.BuildMetaEntries"/>) into the three
    /// slots. Pure: the viewer's language is passed in, because the projection runs off the render
    /// turn, where no viewer is ambient.
    /// </summary>
    /// <param name="entries">The segments.</param>
    /// <param name="locale">The viewer's language tag.</param>
    public static MetaLine Of(ImmutableArray<NodeMetaEntry> entries, string? locale)
    {
        var type = entries.FirstOrDefault(e => e.LabelKey == MeshNodeLayoutAreas.MetaTypeKey);
        var created = entries.FirstOrDefault(e => e.LabelKey == MeshNodeLayoutAreas.MetaCreatedKey);
        var changed = entries.FirstOrDefault(e =>
            e.LabelKey is MeshNodeLayoutAreas.MetaUpdatedKey or MeshNodeLayoutAreas.MetaLastActivityKey);
        return new MetaLine(
            StyleOf(type), LabelOf(type, locale), type?.Text ?? "", type?.Href ?? "",
            StyleOf(created), LabelOf(created, locale), TextOf(created, locale),
            StyleOf(changed), LabelOf(changed, locale), TextOf(changed, locale));
    }

    private static string StyleOf(NodeMetaEntry? entry) => entry is null ? NodePageProjections.Hidden : Visible;

    private static string LabelOf(NodeMetaEntry? entry, string? locale)
        => entry is null ? "" : LocalizationCatalog.Get(entry.LabelKey, locale);

    /// <summary>The stamp, then "by" in the viewer's language, then the author's own sentence as authored.</summary>
    private static string TextOf(NodeMetaEntry? entry, string? locale)
    {
        if (entry is null)
            return "";
        var text = entry.Text;
        if (entry.By is { } by)
            text += " " + LocalizationCatalog.Get(MeshNodeLayoutAreas.MetaByKey, locale, by);
        if (entry.What is { } what)
            text += " — " + what;
        return text;
    }
}
