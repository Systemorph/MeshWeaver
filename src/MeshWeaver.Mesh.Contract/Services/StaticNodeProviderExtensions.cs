using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Shared lookup helpers over every registered <see cref="IStaticNodeProvider"/>.
/// These replace the now-deleted <c>MeshConfiguration.Nodes</c> dictionary —
/// callers iterate the providers directly so any source of static nodes
/// (built-in NodeTypes, per-organization providers, etc.) is consulted
/// uniformly.
/// </summary>
public static class StaticNodeProviderExtensions
{
    /// <summary>
    /// Enumerates every static <see cref="MeshNode"/> across every registered
    /// <see cref="IStaticNodeProvider"/>. Order is not specified; callers
    /// that need deterministic results should sort.
    /// </summary>
    public static IEnumerable<MeshNode> EnumerateStaticNodes(this IServiceProvider serviceProvider) =>
        serviceProvider.GetServices<IStaticNodeProvider>().SelectMany(p => p.GetStaticNodes());

    /// <summary>
    /// First static node whose <see cref="MeshNode.Path"/> matches
    /// <paramref name="path"/> (case-insensitive). Returns null when no
    /// provider offers a matching node.
    /// </summary>
    public static MeshNode? FindStaticNode(this IServiceProvider serviceProvider, string path) =>
        serviceProvider.EnumerateStaticNodes()
            .FirstOrDefault(n => string.Equals(n.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The static node genuinely <b>SERVED</b> at <paramref name="path"/>: the first
    /// non-<see cref="MeshNode.IsDefinitionOnly"/> static node across every registered
    /// <see cref="IStaticNodeProvider"/> whose <see cref="MeshNode.Path"/> matches
    /// (case-insensitive), or <c>null</c>.
    ///
    /// <para>Definition-only entries are skipped deliberately: a DB-synced NodeType catalog's
    /// in-memory type-def supplies its <c>HubConfiguration</c> BY NAME but is NOT the runtime node
    /// at its path — Postgres owns that row (Doc/Architecture/NodeTypeCatalogs.md). This is the ONE
    /// resolution shared by every "who serves this path" seam: <c>MeshDataSource.WithMeshNodes</c>
    /// (which serves the match via <c>WithInitialData</c>, bypassing persistence entirely), the
    /// persistence-sampler gate, the create path's already-exists check, and the plugin installer's
    /// shadowing pre-flight. Keeping them on one lookup is what guarantees "served static" ⇔ "not
    /// persistence-backed" can never drift apart.</para>
    /// </summary>
    public static MeshNode? FindServedStaticNode(this IServiceProvider serviceProvider, string path) =>
        serviceProvider.EnumerateStaticNodes()
            .FirstOrDefault(n => !n.IsDefinitionOnly
                && string.Equals(n.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The type name of the <see cref="IStaticNodeProvider"/> that SERVES <paramref name="path"/>,
    /// or <c>null</c> when nothing does. Names the claimant in a collision diagnostic so the fix is
    /// a lookup away instead of a bisect.
    /// </summary>
    public static string? ServingStaticProviderName(this IServiceProvider serviceProvider, string path)
    {
        foreach (var provider in serviceProvider.GetServices<IStaticNodeProvider>())
        {
            var served = provider.GetStaticNodes().FirstOrDefault(
                n => !n.IsDefinitionOnly
                     && string.Equals(n.Path, path, StringComparison.OrdinalIgnoreCase));
            if (served is null)
                continue;
            // The AddMeshNodes seed is by far the most common claimant (every built-in NodeType
            // definition sits at a top-level path); name the CALL, not the internal wrapper type.
            return provider is StaticMeshNodeListProvider
                ? "MeshBuilder.AddMeshNodes"
                : provider.GetType().Name;
        }
        return null;
    }

    /// <summary>
    /// The ONE message describing a static-vs-durable claim collision at <paramref name="path"/> —
    /// a registered <see cref="IStaticNodeProvider"/> SERVES the path while durable content is
    /// trying to live there.
    ///
    /// <para>🚨 Why this needs its own diagnostic (#1209): the static claim wins every serve seam,
    /// so the per-node hub at that path is seeded from a node that is BY DESIGN never persisted. It
    /// emits one Full snapshot at v0 and never again — which makes the partition root
    /// simultaneously unreadable (the static-node query provider excludes it), un-creatable (the
    /// create path answers "node already exists" from the static entry) and un-writable (every
    /// upsert lands on the static-served hub, whose save is suppressed). Nothing in that chain says
    /// "collision"; downstream it surfaces only as a 30 s timeout on whatever waited for the node's
    /// stream to carry the durable state (the deterministic <c>install: TimeoutException</c> on the
    /// <c>Agent</c>/<c>Skill</c> plugin packages, 2026-08-11). The cure is per-host configuration —
    /// <c>serveFromPartition</c>, which flips the static entry to
    /// <see cref="MeshNode.IsDefinitionOnly"/> so the durable row owns the path — and this message
    /// is what points at it.</para>
    /// </summary>
    /// <param name="serviceProvider">Resolves the registered static node providers.</param>
    /// <param name="path">The contested path.</param>
    /// <returns>The diagnostic, or <c>null</c> when nothing serves <paramref name="path"/> statically.</returns>
    public static string? DescribeStaticServeCollision(this IServiceProvider serviceProvider, string path)
    {
        // ONE sweep: the provider name is non-null on exactly the same condition as
        // FindServedStaticNode, so resolving the claimant also answers "is there a collision".
        if (serviceProvider.ServingStaticProviderName(path) is not { } claimant)
            return null;
        var partition = path.Split('/', 2)[0];
        return $"Path '{path}' is SERVED BY A STATIC NODE PROVIDER ({claimant}), so durable content "
               + $"cannot live there: the per-node hub at '{path}' is seeded from the static node and "
               + "has no persistence backing, which leaves the path unreadable, un-creatable and "
               + "un-writable (MeshWeaver#1209). Configure this host to serve the partition from the "
               + $"database instead — pass '{partition}' in serveFromPartition (e.g. "
               + $"AddAI(serveFromPartition: [\"{partition}\"]), or Features:StaticRepoSync:Partitions) "
               + "— which marks the static entry definition-only and lets the durable row own the path.";
    }
}
