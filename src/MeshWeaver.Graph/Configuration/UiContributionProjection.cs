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
                Href: MeshNodeLayoutAreas.BuildUrl(menuPath, area))
                { LabelKey = contribution.LabelKey });
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
                Id: $"contrib:{node.Path}",
                Label: contribution.Label ?? area,
                ContentBuilder: (h, _) => Controls.LayoutArea(h.Hub.Address, area),
                Icon: contribution.Icon,
                Order: contribution.Order)
                { LabelKey = contribution.LabelKey });
        }
        return items;
    }

    /// <summary>Suffix-aware node-type gate — the platform's <c>Matches</c> semantics.</summary>
    internal static bool NodeTypeGateMatches(string? nodeType, string gate) =>
        nodeType == gate
        || nodeType?.EndsWith("/" + gate, StringComparison.Ordinal) == true;
}
