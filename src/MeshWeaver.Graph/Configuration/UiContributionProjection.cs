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
    /// <see cref="Permission.None"/> and gets nothing), <c>AdminOnly</c>, and the node-shape
    /// vocabulary (<c>ExcludePartitionRoot</c>, suffix-aware <c>NodeTypes</c>).
    /// </summary>
    public static IReadOnlyCollection<NodeMenuItemDefinition> ProjectMenu(
        IReadOnlyList<(MeshNode Node, UiContribution Content)> contributions,
        string context,
        string menuPath,
        MeshNode? menuNode,
        Permission perms,
        bool isAdmin)
    {
        List<NodeMenuItemDefinition>? items = null;
        foreach (var (_, contribution) in contributions)
        {
            if ((contribution.Context ?? UiContribution.NodeContext) != context)
                continue;
            if (contribution.Area is not { Length: > 0 } area)
                continue;
            var required = contribution.RequiredPermission == Permission.None
                ? Permission.Read
                : contribution.RequiredPermission;
            if (!perms.HasFlag(required))
                continue;
            var gates = contribution.Gates;
            if (gates?.AdminOnly == true && !isAdmin)
                continue;
            if (gates?.ExcludePartitionRoot == true
                && PartitionRootDeletionGuard.IsUserPartitionRoot(menuNode))
                continue;
            if (gates?.NodeTypes is { Count: > 0 } nodeTypes
                && !nodeTypes.Any(t => NodeTypeGateMatches(menuNode?.NodeType, t)))
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
