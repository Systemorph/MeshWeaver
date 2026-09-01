using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Shared lookup helpers over every registered <see cref="IStaticNodeProvider"/>.
/// These replace the now-deleted <c>MeshConfiguration.Nodes</c> dictionary —
/// callers iterate the providers directly so any source of static nodes
/// (built-in NodeTypes, per-organization providers, etc.) is consulted
/// uniformly.
///
/// <para>🚨 <b>Every "which static node is at this path" question is answered by
/// <see cref="ResolveStaticNodes"/> and nothing else.</b> Two readers of the same path used to
/// implement their own resolution — <c>StaticNodeQueryProvider</c> gave the <c>AddMeshNodes</c>
/// seed priority and excluded any other provider's node at a seed-claimed path, while
/// <see cref="FindServedStaticNode"/> took a bare <c>FirstOrDefault</c> in DI-registration order.
/// Registration order is not a property a host controls (a provider added by
/// <c>AddPersistence</c> lands before the builder's own deferred registrations), so a second
/// static node at a platform seed's path was served by one reader and not the other, with no
/// error, no warning and no ambiguity diagnostic — the path simply resolved to different content
/// depending on which way you arrived at it (MeshWeaver#2908). One rule, one implementation, both
/// readers.</para>
/// </summary>
public static class StaticNodeProviderExtensions
{
    /// <summary>
    /// 🚨 <b>THE static-node precedence rule.</b> Flattens every registered
    /// <see cref="IStaticNodeProvider"/> into <b>at most one node per path</b>:
    ///
    /// <list type="number">
    ///   <item>the <c>MeshBuilder.AddMeshNodes(...)</c> <b>seed</b> wins every tie — it is the
    ///     host's own declaration, and it is the bucket that carries config-node semantics
    ///     (search-context exclusion) at the query seam, so a bridged copy emitted by another
    ///     provider must never stand in for it;</item>
    ///   <item>among the remaining providers, <b>first registered wins</b>;</item>
    ///   <item>within one provider, its own <see cref="IStaticNodeProvider.GetStaticNodes"/> order
    ///     decides (the seed provider applies last-write-wins by path before it yields).</item>
    /// </list>
    ///
    /// <para>Definition-only entries are NOT filtered here: they still CLAIM their path (Postgres
    /// owns the runtime node there, and a second provider must not sneak a served node underneath
    /// a path the host declared DB-backed). <see cref="FindServedStaticNode"/> applies the
    /// served/definition-only distinction on top of this resolution.</para>
    ///
    /// <para>Lazy on purpose — a <c>FirstOrDefault</c> over the result short-circuits, and the seed
    /// (where nearly every <see cref="FindStaticNode"/> lookup lands) is walked first.</para>
    /// </summary>
    /// <param name="providers">The registered static node providers, in DI-registration order.</param>
    /// <returns>Every static node, seed first, deduplicated by path (case-insensitive).</returns>
    public static IEnumerable<MeshNode> ResolveStaticNodes(IEnumerable<IStaticNodeProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        return Iterate(OrderByPrecedence(providers));

        static IEnumerable<MeshNode> Iterate(IReadOnlyList<IStaticNodeProvider> ordered)
        {
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var provider in ordered)
            foreach (var node in provider.GetStaticNodes())
            {
                // A pathless node cannot be de-duplicated by path; it is passed through so this
                // resolution can never silently DROP a contribution it has no key for.
                if (string.IsNullOrEmpty(node.Path) || claimed.Add(node.Path))
                    yield return node;
            }
        }
    }

    /// <summary>
    /// The same resolution as <see cref="ResolveStaticNodes"/>, kept in its two buckets because the
    /// query seam treats them differently: seed (<c>AddMeshNodes</c>) nodes are registration
    /// declarations and are suppressed under <c>context:search</c> / <c>is:content</c>, while other
    /// providers' nodes are not. Splitting here rather than at the query provider is what keeps
    /// "who wins a contested path" from having a second implementation.
    /// </summary>
    internal static (IReadOnlyList<MeshNode> Seed, IReadOnlyList<MeshNode> Provided)
        ResolveStaticNodeBuckets(IEnumerable<IStaticNodeProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        var ordered = OrderByPrecedence(providers);
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seed = new List<MeshNode>();
        var provided = new List<MeshNode>();
        foreach (var provider in ordered)
        {
            var bucket = IsSeedProvider(provider) ? seed : provided;
            foreach (var node in provider.GetStaticNodes())
                if (string.IsNullOrEmpty(node.Path) || claimed.Add(node.Path))
                    bucket.Add(node);
        }
        return (seed, provided);
    }

    /// <summary>
    /// Seed providers first, everything else in registration order — a STABLE partition, so a
    /// host's registration order still decides among equals.
    /// </summary>
    private static IReadOnlyList<IStaticNodeProvider> OrderByPrecedence(
        IEnumerable<IStaticNodeProvider> providers)
    {
        var list = providers as IReadOnlyList<IStaticNodeProvider> ?? providers.ToArray();
        var seedCount = 0;
        for (var i = 0; i < list.Count; i++)
            if (IsSeedProvider(list[i]))
                seedCount++;
        // Nothing to reorder when the seed is absent, alone, or already the whole set.
        if (seedCount == 0 || seedCount == list.Count)
            return list;
        var ordered = new List<IStaticNodeProvider>(list.Count);
        foreach (var provider in list)
            if (IsSeedProvider(provider))
                ordered.Add(provider);
        foreach (var provider in list)
            if (!IsSeedProvider(provider))
                ordered.Add(provider);
        return ordered;
    }

    /// <summary>
    /// The <c>MeshBuilder.AddMeshNodes(...)</c> seed — registered by <c>MeshBuilder.Register()</c>
    /// as <c>StaticMeshNodeListProvider</c>. Type identity, not registration position: the
    /// position is exactly what a host cannot control.
    /// </summary>
    internal static bool IsSeedProvider(IStaticNodeProvider provider) =>
        provider is StaticMeshNodeListProvider;

    /// <summary>
    /// The display name for a provider in a diagnostic. The <c>AddMeshNodes</c> seed is by far the
    /// most common claimant (every built-in NodeType declaration sits at a top-level path); name
    /// the CALL, not the internal wrapper type.
    /// </summary>
    private static string ProviderName(IStaticNodeProvider provider) =>
        IsSeedProvider(provider) ? "MeshBuilder.AddMeshNodes" : provider.GetType().Name;

    /// <summary>
    /// Enumerates every static <see cref="MeshNode"/> across every registered
    /// <see cref="IStaticNodeProvider"/>, resolved by <see cref="ResolveStaticNodes"/> — seed
    /// first, at most one node per path.
    /// </summary>
    public static IEnumerable<MeshNode> EnumerateStaticNodes(this IServiceProvider serviceProvider) =>
        ResolveStaticNodes(serviceProvider.GetServices<IStaticNodeProvider>());

    /// <summary>
    /// The static node at <paramref name="path"/> (case-insensitive), or null when no provider
    /// offers one. Resolution is <see cref="ResolveStaticNodes"/>'s, so this agrees with the query
    /// seam by construction.
    /// </summary>
    public static MeshNode? FindStaticNode(this IServiceProvider serviceProvider, string path) =>
        serviceProvider.EnumerateStaticNodes()
            .FirstOrDefault(n => string.Equals(n.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The static node genuinely <b>SERVED</b> at <paramref name="path"/>: the
    /// <see cref="ResolveStaticNodes"/> winner for that path when it is not
    /// <see cref="MeshNode.IsDefinitionOnly"/>, else <c>null</c>.
    ///
    /// <para>Definition-only entries are skipped deliberately: a DB-synced NodeType catalog's
    /// in-memory type-def supplies its <c>HubConfiguration</c> BY NAME but is NOT the runtime node
    /// at its path — Postgres owns that row (Doc/Architecture/NodeTypeCatalogs.md). This is the ONE
    /// resolution shared by every "who serves this path" seam: <c>MeshDataSource.WithMeshNodes</c>
    /// (which serves the match via <c>WithInitialData</c>, bypassing persistence entirely), the
    /// persistence-sampler gate, the create path's already-exists check, and the plugin installer's
    /// shadowing pre-flight. Keeping them on one lookup is what guarantees "served static" ⇔ "not
    /// persistence-backed" can never drift apart.</para>
    ///
    /// <para>🚨 The winner is taken FIRST and the definition-only test applied SECOND, never the
    /// other way round: a path the host declared DB-backed must resolve to "nothing static serves
    /// this" even when a lower-precedence provider still offers a served node there. Scanning for
    /// "the first served node" instead would let that provider's node win a path the seed had
    /// deliberately handed to Postgres — the divergence in MeshWeaver#2908.</para>
    /// </summary>
    public static MeshNode? FindServedStaticNode(this IServiceProvider serviceProvider, string path) =>
        serviceProvider.FindStaticNode(path) is { IsDefinitionOnly: false } served ? served : null;

    /// <summary>
    /// The type name of the <see cref="IStaticNodeProvider"/> that SERVES <paramref name="path"/>,
    /// or <c>null</c> when nothing does. Names the claimant in a collision diagnostic so the fix is
    /// a lookup away instead of a bisect. Follows <see cref="ResolveStaticNodes"/>'s precedence, so
    /// it names the provider whose node <see cref="FindServedStaticNode"/> actually returns.
    /// </summary>
    public static string? ServingStaticProviderName(this IServiceProvider serviceProvider, string path)
    {
        var claims = ClaimsAt(serviceProvider.GetServices<IStaticNodeProvider>(), path);
        if (claims.Count == 0 || claims[0].Node.IsDefinitionOnly)
            return null;
        return ProviderName(claims[0].Provider);
    }

    /// <summary>
    /// Every provider claiming <paramref name="path"/>, in precedence order (the winner first).
    /// </summary>
    private static IReadOnlyList<(IStaticNodeProvider Provider, MeshNode Node)> ClaimsAt(
        IEnumerable<IStaticNodeProvider> providers, string path)
    {
        var claims = new List<(IStaticNodeProvider, MeshNode)>();
        foreach (var provider in OrderByPrecedence(providers))
        {
            // Within one provider the first match is that provider's claim — the same
            // within-provider order ResolveStaticNodes honours.
            var node = provider.GetStaticNodes()
                .FirstOrDefault(n => string.Equals(n.Path, path, StringComparison.OrdinalIgnoreCase));
            if (node is not null)
                claims.Add((provider, node));
        }
        return claims;
    }

    /// <summary>
    /// Every path claimed by MORE THAN ONE static provider with <b>different</b> content — the
    /// registration that <see cref="ResolveStaticNodes"/> resolves silently and that a host almost
    /// certainly did not intend.
    ///
    /// <para>🚨 Why "different content" and not "more than one claimant": declaring a node type
    /// registers the SAME declaration twice by design (<c>AddPartitionType</c> calls
    /// <c>builder.AddMeshNodes(CreateMeshNode())</c> and registers a provider that yields
    /// <c>CreateMeshNode()</c> again), so a bare duplicate count would fire on every built-in type
    /// and be ignored within a week. Two claimants offering byte-identical declarations are
    /// redundant; two claimants offering DIFFERENT declarations mean one of them is being dropped,
    /// which is the failure this exists to name.</para>
    ///
    /// <para>Comparison is <see cref="PartitionSourceFingerprint.ComputeNodeToken"/> — the repo's
    /// deterministic per-node source token, which excludes the un-serialisable
    /// <c>HubConfiguration</c> delegate (two calls to one factory produce different delegates and
    /// would otherwise never compare equal).</para>
    /// </summary>
    /// <param name="providers">The registered static node providers, in DI-registration order.</param>
    /// <param name="options">Serializer options for the content token; the hub's when available.</param>
    /// <returns>One human-readable line per contested path; empty when nothing collides.</returns>
    public static IReadOnlyList<string> DescribeStaticProviderCollisions(
        IEnumerable<IStaticNodeProvider> providers,
        JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        var ordered = OrderByPrecedence(providers);
        // path -> claims, in precedence order. Built in ONE sweep: asking ClaimsAt per path would
        // re-enumerate every provider once per path.
        var byPath = new Dictionary<string, List<(IStaticNodeProvider Provider, MeshNode Node)>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var provider in ordered)
        {
            var seenHere = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in provider.GetStaticNodes())
            {
                if (string.IsNullOrEmpty(node.Path) || !seenHere.Add(node.Path))
                    continue;
                if (!byPath.TryGetValue(node.Path, out var claims))
                    byPath[node.Path] = claims = new List<(IStaticNodeProvider, MeshNode)>();
                claims.Add((provider, node));
            }
        }

        var report = new List<string>();
        foreach (var (path, claims) in byPath)
        {
            if (claims.Count < 2)
                continue;
            var shadowed = ShadowedClaims(claims, options);
            if (shadowed.Count == 0)
                continue;
            report.Add(
                $"Path '{path}' is claimed by {claims.Count} static node providers with DIFFERENT "
                + $"content. '{ProviderName(claims[0].Provider)}' wins and "
                + string.Join(", ", shadowed.Select(c => $"'{ProviderName(c.Provider)}'"))
                + " is silently dropped. Static-node precedence is: the MeshBuilder.AddMeshNodes "
                + "seed first, then providers in registration order — registration order is not "
                + "something a host controls, so overriding another contributor's node by adding a "
                + "second one at its path is NOT supported (MeshWeaver#2908). Remove one of the "
                + "registrations, or give the host's own declaration its own path.");
        }
        report.Sort(StringComparer.Ordinal);
        return report;
    }

    /// <summary>
    /// <inheritdoc cref="DescribeStaticProviderCollisions(IEnumerable{IStaticNodeProvider}, JsonSerializerOptions)"/>
    /// </summary>
    public static IReadOnlyList<string> DescribeStaticProviderCollisions(
        this IServiceProvider serviceProvider,
        JsonSerializerOptions? options = null) =>
        DescribeStaticProviderCollisions(serviceProvider.GetServices<IStaticNodeProvider>(), options);

    /// <summary>
    /// The claims after the winner whose content token differs from the winner's — i.e. the
    /// contributions this resolution actually DROPS.
    /// </summary>
    private static IReadOnlyList<(IStaticNodeProvider Provider, MeshNode Node)> ShadowedClaims(
        IReadOnlyList<(IStaticNodeProvider Provider, MeshNode Node)> claims,
        JsonSerializerOptions? options)
    {
        var winnerToken = PartitionSourceFingerprint.ComputeNodeToken(claims[0].Node, options);
        var shadowed = new List<(IStaticNodeProvider, MeshNode)>();
        for (var i = 1; i < claims.Count; i++)
            if (!string.Equals(
                    PartitionSourceFingerprint.ComputeNodeToken(claims[i].Node, options),
                    winnerToken,
                    StringComparison.Ordinal))
                shadowed.Add(claims[i]);
        return shadowed;
    }

    /// <summary>
    /// The ONE message describing a static claim collision at <paramref name="path"/> — a
    /// registered <see cref="IStaticNodeProvider"/> SERVES the path while durable content is trying
    /// to live there, and/or a second static provider claims the same path with different content.
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
    ///
    /// <para>🚨 The second half (#2908): when more than one static provider claims the path, the
    /// loser is dropped silently and the two seams that read the path used to disagree about which
    /// one that was. The message names every claimant so a host that appended a node at a platform
    /// seed's path learns that it did, instead of discovering it by comparing two views.</para>
    /// </summary>
    /// <param name="serviceProvider">Resolves the registered static node providers.</param>
    /// <param name="path">The contested path.</param>
    /// <returns>The diagnostic, or <c>null</c> when nothing claims <paramref name="path"/> statically.</returns>
    public static string? DescribeStaticServeCollision(this IServiceProvider serviceProvider, string path)
    {
        var claims = ClaimsAt(serviceProvider.GetServices<IStaticNodeProvider>(), path);
        if (claims.Count == 0)
            return null;
        var partition = path.Split('/', 2)[0];
        var shadowed = claims.Count > 1
            ? ShadowedClaims(claims, options: null)
            : Array.Empty<(IStaticNodeProvider Provider, MeshNode Node)>();
        var contested = shadowed.Count > 0
            ? $" It is ALSO claimed by {string.Join(", ", shadowed.Select(c => $"'{ProviderName(c.Provider)}'"))} "
              + "with different content, which this resolution drops — static-node precedence is the "
              + "MeshBuilder.AddMeshNodes seed first, then providers in registration order "
              + "(MeshWeaver#2908)."
            : string.Empty;
        if (claims[0].Node.IsDefinitionOnly)
            // Nothing is SERVED here — the winner handed the path to persistence. Only the
            // multi-claimant half of the message can apply.
            return shadowed.Count > 0
                ? $"Path '{path}' is claimed by more than one static node provider."
                  + $" '{ProviderName(claims[0].Provider)}' wins and marks the path definition-only"
                  + $" (the durable row owns it).{contested}"
                : null;
        return $"Path '{path}' is SERVED BY A STATIC NODE PROVIDER ({ProviderName(claims[0].Provider)}), "
               + "so durable content cannot live there: the per-node hub at "
               + $"'{path}' is seeded from the static node and "
               + "has no persistence backing, which leaves the path unreadable, un-creatable and "
               + "un-writable (MeshWeaver#1209). Configure this host to serve the partition from the "
               + $"database instead — pass '{partition}' in serveFromPartition (e.g. "
               + $"AddAI(serveFromPartition: [\"{partition}\"]), or Features:StaticRepoSync:Partitions) "
               + "— which marks the static entry definition-only and lets the durable row own the path."
               + contested;
    }
}
