using System;
using MeshWeaver.Domain;
using MeshWeaver.Graph.Security;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// The PURE projection of <see cref="UiContribution"/> data into menu entries — the compiled
/// enforcement point of the closed gate vocabulary (#1645). Kept side-effect-free (no hub, no
/// streams) so the security-relevant logic is unit-testable the way
/// <c>MenuPresentationOverlay</c> is: the reactive aggregators only COMPOSE the live inputs
/// (viewer permissions, admin stream, node stream) and hand them here.
/// </summary>
internal static class UiContributionProjection
{
    /// <summary>
    /// Projects the catalog into one node/mesh-menu context's entries. Gates, all enforced here:
    /// context match, a non-empty Area, the viewer's effective permission (floored at
    /// <see cref="Permission.Read"/> — an anonymous viewer arrives with
    /// <see cref="Permission.None"/> and gets nothing), and the whole node-shape vocabulary via
    /// <see cref="PassesNodeGates"/> (<c>AdminOnly</c>, <c>ExcludePartitionRoot</c>,
    /// <c>ExcludeViewerHome</c>, <c>SyncedOnly</c>, suffix-aware <c>NodeTypes</c>).
    /// </summary>
    public static IReadOnlyCollection<NodeMenuItemDefinition> ProjectMenu(
        IReadOnlyList<(MeshNode Node, UiContribution Content)> contributions,
        string context,
        string menuPath,
        MeshNode? menuNode,
        Permission perms,
        bool isAdmin,
        string? viewerId = null)
    {
        List<NodeMenuItemDefinition>? items = null;
        foreach (var (_, contribution) in contributions)
        {
            if ((contribution.Context ?? UiContribution.NodeContext) != context)
                continue;
            if (contribution.Area is not { Length: > 0 } area)
                continue;
            var required = RequiredPermissionFloor(contribution);
            if (!perms.HasFlag(required))
                continue;
            if (!PassesNodeGates(contribution.Gates, menuPath, menuNode, isAdmin, viewerId))
                continue;

            (items ??= []).Add(new NodeMenuItemDefinition(
                contribution.Label ?? area,
                area,
                contribution.Icon,
                required,
                contribution.Order,
                // A declared Href wins (catalog-style links), resolved through ResolveHref:
                // the {node} token is substituted with the anchoring node and the result must be
                // portal-INTERNAL, else the entry falls back to the derived area URL — narrowing,
                // never widening (#1645). Otherwise the entry opens its area on the anchoring
                // node, like every built-in node-menu item.
                Href: ResolveHref(contribution.Href, menuPath)
                      ?? MeshNodeLayoutAreas.BuildUrl(menuPath, area),
                Tooltip: contribution.Tooltip)
                { LabelKey = contribution.LabelKey, TooltipKey = contribution.TooltipKey });
        }
        return items ?? (IReadOnlyCollection<NodeMenuItemDefinition>)[];
    }

    /// <summary>
    /// Projects the catalog into the global settings tabs. The settings surface has no node to
    /// bind <see cref="UiContribution.RequiredPermission"/> to; the gates here are "authenticated
    /// viewer" (enforced by the CALLER, which returns an empty stream for anonymous) and
    /// <c>AdminOnly</c>. The tab's content is the contributed layout AREA embedded generically —
    /// contributions never introduce a render surface.
    /// </summary>
    public static IReadOnlyList<GlobalSettingsMenuItemDefinition> ProjectSettingsTabs(
        IReadOnlyList<(MeshNode Node, UiContribution Content)> contributions,
        bool isAdmin)
    {
        var items = new List<GlobalSettingsMenuItemDefinition>();
        foreach (var (node, contribution) in contributions)
        {
            if (contribution.Context != UiContribution.SettingsContext)
                continue;
            if (contribution.Area is not { Length: > 0 } area)
                continue;
            if (contribution.Gates?.AdminOnly == true && !isAdmin)
                continue;
            items.Add(new GlobalSettingsMenuItemDefinition(
                // The node id (= the trailing path segment) keeps the tab's /GlobalSettings/{Id}
                // deep link stable when a compiled tab migrates to a same-named seeded contribution.
                Id: node.Id is { Length: > 0 } id ? id : area,
                Label: contribution.Label ?? area,
                // Embed INTO the pane's stack so the contributed tab inherits the same
                // padding/scroll container every compiled tab renders in.
                ContentBuilder: (h, stack) => stack.WithView(Controls.LayoutArea(h.Hub.Address, area)),
                Group: contribution.Group,
                // Icon.Parse is the platform's TOTAL string→Icon conversion (Fluent name, SVG,
                // URL, emoji→text) — the NavMenu renderer expects Icon objects here.
                GroupIcon: Icon.Parse(contribution.GroupIcon),
                Icon: Icon.Parse(contribution.Icon),
                Order: contribution.Order)
                { LabelKey = contribution.LabelKey, GroupKey = contribution.GroupKey });
        }
        return items;
    }

    /// <summary>
    /// Projects the catalog into the PER-NODE settings tabs
    /// (<c>/{nodePath}/Settings/{Id}</c>) — the lane the global settings surface has had since WS7
    /// slice 2 and this one did not, which is why every remaining compiled tab was stuck
    /// (#3055). Same closed vocabulary, same "narrow only" property; the differences are the ones
    /// the surface itself forces:
    ///
    /// <list type="bullet">
    /// <item><description>It answers a DIFFERENT context key
    /// (<see cref="UiContribution.NodeSettingsContext"/>) — see that constant for why the two
    /// surfaces are not one key.</description></item>
    /// <item><description>🚨 It does NOT filter on the viewer's permission and takes none. It
    /// carries <see cref="UiContribution.RequiredPermission"/> onto the definition and lets
    /// <c>SettingsMenuItemsExtensions.FilterByPermission</c> apply it at the render fold, against
    /// the LATEST permission value. Filtering here would bake a permission SNAPSHOT into a
    /// long-lived provider stream — the #1962 defect, where a chain built on an early
    /// <see cref="Permission.None"/> seed stays subscribed and later re-renders the menu with
    /// every entitled tab silently missing. The floor is still applied (an entry that demands
    /// nothing demands <see cref="Permission.Read"/>), so an anonymous viewer, who reaches the
    /// fold as <see cref="Permission.None"/>, gets nothing.</description></item>
    /// <item><description>It carries <see cref="UiContribution.Keywords"/> through to
    /// <see cref="SettingsMenuItemDefinition.Keywords"/>, so a migrated tab keeps answering the
    /// settings SEARCH box. Without it, migrating a tab removes it from search silently.</description></item>
    /// <item><description>The node-shape gates apply, because unlike the global surface this one
    /// HAS an anchoring node.</description></item>
    /// </list>
    /// </summary>
    /// <param name="contributions">The live catalog slice.</param>
    /// <param name="menuPath">The path of the node whose settings page is being rendered.</param>
    /// <param name="menuNode">That node, or null when it could not be resolved.</param>
    /// <param name="isAdmin">The viewer's live platform-admin answer.</param>
    /// <param name="viewerId">The viewer's own partition key, for <c>ExcludeViewerHome</c>.</param>
    public static IReadOnlyList<SettingsMenuItemDefinition> ProjectNodeSettingsTabs(
        IReadOnlyList<(MeshNode Node, UiContribution Content)> contributions,
        string menuPath,
        MeshNode? menuNode,
        bool isAdmin,
        string? viewerId)
    {
        var items = new List<SettingsMenuItemDefinition>();
        foreach (var (node, contribution) in contributions)
        {
            if (contribution.Context != UiContribution.NodeSettingsContext)
                continue;
            if (contribution.Area is not { Length: > 0 } area)
                continue;
            if (!PassesNodeGates(contribution.Gates, menuPath, menuNode, isAdmin, viewerId))
                continue;

            items.Add(new SettingsMenuItemDefinition(
                // The node id (= the trailing path segment) keeps the tab's /Settings/{Id} deep
                // link stable when a compiled tab migrates to a same-named seeded contribution.
                Id: node.Id is { Length: > 0 } id ? id : area,
                Label: contribution.Label ?? area,
                // Embed INTO the pane's stack so a contributed tab inherits the same
                // padding/scroll container every compiled tab renders in. The node argument is
                // deliberately unused: the area renders on the anchoring node's OWN hub, so it
                // resolves that node the same way every other layout area does.
                ContentBuilder: (h, stack, _) => stack.WithView(Controls.LayoutArea(h.Hub.Address, area)),
                Group: contribution.Group,
                // Icon.Parse is the platform's TOTAL string→Icon conversion (Fluent name, SVG,
                // URL, emoji→text) — the NavMenu renderer expects Icon objects here.
                Icon: Icon.Parse(contribution.Icon),
                GroupIcon: Icon.Parse(contribution.GroupIcon),
                Order: contribution.Order,
                RequiredPermission: RequiredPermissionFloor(contribution),
                Keywords: contribution.Keywords)
                { LabelKey = contribution.LabelKey, GroupKey = contribution.GroupKey });
        }
        return items;
    }

    /// <summary>
    /// A contribution can never demand LESS than <see cref="Permission.Read"/> — an entry that
    /// declares nothing still requires Read, which is what makes an anonymous viewer (who arrives
    /// as <see cref="Permission.None"/>) see nothing at all.
    /// </summary>
    private static Permission RequiredPermissionFloor(UiContribution contribution)
        => contribution.RequiredPermission == Permission.None
            ? Permission.Read
            : contribution.RequiredPermission;

    /// <summary>
    /// The CLOSED node-shape gate vocabulary, evaluated in ONE place so the node menu and the
    /// per-node settings page can never drift on what a gate word means. Every clause
    /// SUBTRACTS: an absent gate set passes.
    /// </summary>
    /// <param name="gates">The declared gates, or null.</param>
    /// <param name="menuPath">The anchoring node's path.</param>
    /// <param name="menuNode">The anchoring node, or null when unresolved.</param>
    /// <param name="isAdmin">The viewer's live platform-admin answer.</param>
    /// <param name="viewerId">The viewer's own partition key, or null when anonymous/unknown.</param>
    private static bool PassesNodeGates(
        UiContributionGates? gates, string menuPath, MeshNode? menuNode, bool isAdmin, string? viewerId)
    {
        if (gates is null)
            return true;
        if (gates.AdminOnly && !isAdmin)
            return false;
        if (gates.ExcludePartitionRoot && PartitionRootDeletionGuard.IsUserPartitionRoot(menuNode))
            return false;
        if (gates.ExcludeViewerHome && IsViewerHome(menuPath, viewerId))
            return false;
        // "Still synced" is the shape the Stop-synchronization action needs; a node the viewer has
        // already claimed (any non-Include SyncBehavior) has nothing left to stop. A node that
        // could not be resolved fails the gate — narrowing, never widening, on missing evidence.
        if (gates.SyncedOnly && menuNode is not { SyncBehavior: SyncBehavior.Include })
            return false;
        if (gates.NodeTypes is { Count: > 0 } nodeTypes
            && !nodeTypes.Any(t => NodeTypeGateMatches(menuNode?.NodeType, t)))
            return false;
        return true;
    }

    /// <summary>
    /// The viewer's OWN home: the anchoring path IS the viewer's partition key. The identical
    /// comparison <c>PinLayoutArea.GetMenuItem</c> and <c>PresentationLayoutArea.GetMenuItem</c>
    /// already make, so the gate word and the compiled defaults it will replace agree by
    /// construction. Never true for an anonymous viewer — there is no home to be on.
    /// </summary>
    internal static bool IsViewerHome(string? menuPath, string? viewerId)
        => !string.IsNullOrEmpty(menuPath)
           && !string.IsNullOrEmpty(viewerId)
           && menuPath.Equals(viewerId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The one token a contributed <c>Href</c> may carry: the path of the node whose menu is being
    /// rendered, substituted URL-escaped.
    ///
    /// <para><b>Why it exists.</b> Without it a data-contributed entry can only open an area on the
    /// anchoring node itself — so a plugin that serves a node-anchored surface from its OWN
    /// workspace (<c>/Plugin/Workspace/Area?doc={node}</c> — the shape Collaboration and Approvals
    /// use, and the only shape available to a node-native package, which cannot register an area
    /// onto another type's hub) had no way to name the current node. That gap is what kept such
    /// features compiled into the platform.</para>
    /// </summary>
    internal const string NodeToken = "{node}";

    /// <summary>
    /// Resolves a declared Href: substitutes <see cref="NodeToken"/>, then applies the
    /// portal-internal gate to the RESULT (never to the template — the check has to see what will
    /// actually be navigated to). Returns null when there is no usable declared href, which is the
    /// caller's signal to derive the area URL instead.
    /// </summary>
    internal static string? ResolveHref(string? href, string menuPath)
    {
        if (href is not { Length: > 0 })
            return null;
        // The token is replaced by a MESH PATH, escaped — so it can never introduce a scheme or a
        // protocol-relative host, and the gate below still judges the final string.
        var resolved = href.Contains(NodeToken, StringComparison.Ordinal)
            ? href.Replace(NodeToken, Uri.EscapeDataString(menuPath ?? ""), StringComparison.Ordinal)
            : href;
        return IsPortalInternalHref(resolved) ? resolved : null;
    }

    /// <summary>
    /// The Href gate: a contributed link must be PORTAL-INTERNAL — a single-slash-rooted path
    /// (<c>/search?…</c>, <c>/Agent/AiAgents</c>). A leading scheme (<c>javascript:</c>,
    /// <c>https:</c>) or a protocol-relative <c>//host</c> cannot pass, because a rooted path can
    /// contain neither. External links stay a COMPILED concern until an explicit allowlist
    /// vocabulary exists.
    /// </summary>
    internal static bool IsPortalInternalHref(string href) =>
        href.Length > 1 && href[0] == '/' && href[1] != '/';

    /// <summary>Suffix-aware node-type gate — the platform's <c>Matches</c> semantics.</summary>
    internal static bool NodeTypeGateMatches(string? nodeType, string gate) =>
        nodeType == gate
        || nodeType?.EndsWith("/" + gate, StringComparison.Ordinal) == true;
}
