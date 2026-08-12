using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh;

/// <summary>
/// Defines a menu item that a node type registers for its context menu.
/// Rendered dynamically in the portal's node menu.
/// </summary>
/// <param name="Label">Display text for the menu item (e.g., "Edit", "Suggest")</param>
/// <param name="Area">Layout area to navigate to (e.g., "Edit", "Suggest")</param>
/// <param name="Icon">Optional icon — emoji string or SVG URL; null to skip</param>
/// <param name="RequiredPermission">Permission the user must have for this item to appear</param>
/// <param name="Order">Sort order within the menu (lower = earlier)</param>
/// <param name="Href">Optional absolute href — when set, navigates to this URL instead of constructing from Area</param>
/// <param name="Children">Optional child menu items for nested/hierarchical menus</param>
/// <param name="Tooltip">Optional hover tooltip; falls back to <paramref name="Label"/> when null</param>
public record NodeMenuItemDefinition(
    string Label,
    string Area,
    string? Icon = null,
    Permission RequiredPermission = Permission.None,
    int Order = 0,
    string? Href = null,
    IReadOnlyList<NodeMenuItemDefinition>? Children = null,
    string? Tooltip = null)
{
    /// <summary>
    /// Sentinel <see cref="Area"/> for a divider. Renderers draw a rule and never make it activatable.
    /// </summary>
    public const string SeparatorArea = "_separator";

    /// <summary>
    /// Sentinel <see cref="Area"/> for a pure GROUPING parent — an entry that exists only to hold
    /// <see cref="Children"/> and has nowhere of its own to navigate to. Every renderer draws it as a
    /// submenu and NEVER activates it, so a group can never be "clickable to nowhere".
    ///
    /// <para>Why a sentinel rather than an empty <see cref="Area"/>: it mirrors the long-standing
    /// <see cref="SeparatorArea"/> convention, it is greppable, and it makes the wire self-describing —
    /// a client that cannot nest can still tell "this is a group, not an action" instead of rendering a
    /// dead entry that navigates to <c>/{path}/</c>.</para>
    /// </summary>
    public const string GroupArea = "_group";

    /// <summary>True when this entry carries nested children.</summary>
    public bool HasChildren => Children is { Count: > 0 };

    /// <summary>
    /// True when this entry is a submenu parent, and therefore NOT activatable.
    ///
    /// <para>Any entry carrying <see cref="Children"/> is a parent — its own <see cref="Area"/> /
    /// <see cref="Href"/> is deliberately ignored for activation. Both component libraries behind the web
    /// renderers (FAST's <c>fluent-menu-item</c> submenu slot and Fluent React v9's
    /// <c>MenuTrigger</c>-wrapped item) toggle the submenu on click/Enter rather than invoking the parent,
    /// so a "clickable parent that also opens a submenu" is not expressible in either. Making that
    /// explicit here keeps the three renderers honest instead of each inventing its own rule.</para>
    /// </summary>
    public bool IsSubmenuParent => HasChildren || Area == GroupArea;

    /// <summary>
    /// Optional localization key for <see cref="Label"/> (e.g. <c>menu.edit</c>). When set,
    /// <see cref="Localized"/> replaces the English <see cref="Label"/> with the viewer's
    /// translation; when null the English label is used as-is.
    ///
    /// <para>A key rather than a locale parameter on every <c>GetMenuItem</c> helper: the ~20
    /// providers that build these items are static and scattered across projects, but the menu is
    /// aggregated in ONE place that already has the subscriber's <c>AccessService</c>. Keying here
    /// and resolving there keeps the translation concern in a single spot, and an unkeyed item
    /// still renders its English label rather than breaking.</para>
    /// </summary>
    public string? LabelKey { get; init; }

    /// <summary>Optional localization key for <see cref="Tooltip"/>. See <see cref="LabelKey"/>.</summary>
    public string? TooltipKey { get; init; }

    /// <summary>
    /// This item with <see cref="Label"/> and <see cref="Tooltip"/> resolved into the current
    /// viewer's language (recursively, so nested menus translate too). Items with no
    /// <see cref="LabelKey"/> are returned unchanged. Call once, at menu aggregation.
    /// </summary>
    public NodeMenuItemDefinition Localized(AccessService? access)
    {
        var children = Children?.Count > 0
            ? Children.Select(c => c.Localized(access)).ToList()
            : null;
        var label = LabelKey is { Length: > 0 } lk ? access.Localize(lk) : Label;
        var tooltip = TooltipKey is { Length: > 0 } tk ? access.Localize(tk) : Tooltip;

        if (ReferenceEquals(label, Label) && tooltip == Tooltip && children is null)
            return this;

        return this with { Label = label, Tooltip = tooltip, Children = children ?? Children };
    }

    /// <summary>
    /// Value equality that compares <see cref="Children"/> by SEQUENCE (recursively). The synthesized
    /// record equality compares the Children LIST by REFERENCE, and the live menu stream deserializes a
    /// fresh list on every <c>Full</c> re-emission — so a structurally-identical menu was never "equal",
    /// the menu dedup never fired, and <c>PortalLayoutBase</c> re-rendered the WHOLE page on every
    /// unchanged Full (a render-storm cascade to every layout area). Comparing children by sequence closes
    /// that. Flat menus (Children null/empty) are unaffected.
    /// </summary>
    public virtual bool Equals(NodeMenuItemDefinition? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Label == other.Label && Area == other.Area && Icon == other.Icon
            && RequiredPermission == other.RequiredPermission && Order == other.Order
            && Href == other.Href && Tooltip == other.Tooltip
            && ChildrenEqual(Children, other.Children);
    }

    /// <summary>Hash consistent with <see cref="Equals(NodeMenuItemDefinition?)"/> — folds child ELEMENTS,
    /// never the list reference.</summary>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Label);
        hash.Add(Area);
        hash.Add(Icon);
        hash.Add(RequiredPermission);
        hash.Add(Order);
        hash.Add(Href);
        hash.Add(Tooltip);
        foreach (var c in Children ?? [])
            hash.Add(c);
        return hash.ToHashCode();
    }

    private static bool ChildrenEqual(IReadOnlyList<NodeMenuItemDefinition>? a, IReadOnlyList<NodeMenuItemDefinition>? b)
    {
        if (ReferenceEquals(a, b)) return true;
        var ca = a?.Count ?? 0;
        var cb = b?.Count ?? 0;
        if (ca != cb) return false;
        return ca == 0 || a!.SequenceEqual(b!);   // recurses through this Equals
    }
}

/// <summary>
/// Sequence equality for a menu-items list — for <c>DistinctUntilChanged</c> on the live menu stream so a
/// structurally-identical re-emission does NOT re-render the layout. Elements compare by value (the
/// <see cref="NodeMenuItemDefinition"/> value equality above, Children included).
/// </summary>
public sealed class MenuItemsSequenceComparer : IEqualityComparer<IReadOnlyList<NodeMenuItemDefinition>>
{
    /// <summary>Shared instance.</summary>
    public static readonly MenuItemsSequenceComparer Instance = new();

    /// <inheritdoc />
    public bool Equals(IReadOnlyList<NodeMenuItemDefinition>? x, IReadOnlyList<NodeMenuItemDefinition>? y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x is null || y is null || x.Count != y.Count) return false;
        return x.SequenceEqual(y);
    }

    /// <inheritdoc />
    public int GetHashCode(IReadOnlyList<NodeMenuItemDefinition> obj)
    {
        var hash = new HashCode();
        foreach (var i in obj)
            hash.Add(i);
        return hash.ToHashCode();
    }
}

/// <summary>
/// Provider delegate that emits the current set of menu items as a reactive stream.
/// Each emission is the provider's <b>complete</b> set of items for the current state — the
/// renderer replaces (not appends) the provider's slice on every emission. Providers compose
/// the live MeshNode + permission streams, so the menu re-renders automatically when a runtime
/// <c>AccessAssignment</c> propagates (the access-race fix — see
/// <c>Doc/GUI/NodeMenu.md</c>). Emit an empty collection (never <c>Observable.Empty</c>) when
/// the provider contributes nothing for the current node, so the aggregator's
/// <c>CombineLatest</c> never stalls waiting on a silent provider.
/// </summary>
public delegate IObservable<IReadOnlyCollection<NodeMenuItemDefinition>> NodeMenuItemProvider(
    LayoutAreaHost host, RenderingContext context);

/// <summary>
/// DI-registered contributor to the node / mesh menus. Each implementation type is registered
/// once per hub via <c>TryAddEnumerable</c> (same pattern as <c>IAutocompleteProvider</c>) — the
/// renderer resolves all instances from DI, groups them by <see cref="Context"/>, and sorts the
/// resulting items by <see cref="NodeMenuItemDefinition.Order"/>.
/// </summary>
public interface INodeMenuProvider
{
    /// <summary>
    /// Menu context this provider contributes to. Defaults to the "Node" menu. Override to
    /// contribute to another named context (e.g. "Mesh", "SidePanel").
    /// </summary>
    string Context => "Node";

    /// <summary>
    /// Emits this provider's complete set of menu items as a reactive stream. Providers may check
    /// node type / permissions inside the stream — the renderer passes no filter, so any
    /// "contributes nothing" case must emit an <b>empty</b> collection (never
    /// <c>Observable.Empty</c>, which would stall the aggregator's <c>CombineLatest</c>).
    /// Compose live streams (<c>GetMeshNodeStream</c>, <c>GetEffectivePermissions</c>) so the menu
    /// re-renders when permissions or node content change.
    /// </summary>
    IObservable<IReadOnlyCollection<NodeMenuItemDefinition>> GetItems(LayoutAreaHost host, RenderingContext context);
}

/// <summary>
/// Wraps menu items for storage at $Menu in the entity store.
/// Same pattern as DialogControl at $Dialog.
/// </summary>
public record MenuControl : UiControl<MenuControl>
{
    /// <summary>Entity store area name for menu items.</summary>
    public const string MenuArea = "$Menu";

    /// <summary>
    /// Gets the area name for a given menu context.
    /// Default (null) context returns "$Menu"; named contexts return "$Menu:{context}".
    /// </summary>
    public static string GetMenuArea(string? context = null)
        => context == null ? MenuArea : $"{MenuArea}:{context}";

    /// <summary>Creates an empty MenuControl.</summary>
    public MenuControl() : base(ModuleSetup.ModuleName, ModuleSetup.ApiVersion) { }

    /// <summary>Creates a MenuControl with the specified menu items.</summary>
    public MenuControl(IReadOnlyList<NodeMenuItemDefinition> items)
        : base(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
    {
        Items = items;
    }

    /// <summary>Menu items to display in the node context menu.</summary>
    public IReadOnlyList<NodeMenuItemDefinition> Items { get; init; } = [];
}
