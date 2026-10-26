using System.Collections.Immutable;

namespace MeshWeaver.Mesh;

/// <summary>One row of a dependency-closure answer: a node reached from the start, the length of the
/// shortest chain to it, and that chain itself, start first (<c>Doc/Architecture/DependencyGraph</c>).</summary>
/// <param name="Node">The node reached.</param>
/// <param name="Depth">The number of edges on the shortest chain (1 = a direct edge).</param>
/// <param name="Path">The chain, from the start node to <paramref name="Node"/> inclusive.</param>
public sealed record DependencyClosureRow(string Node, int Depth, ImmutableArray<string> Path);

/// <summary>
/// The dependency closure over forward edges, computed in memory — the in-memory twin of the
/// PostgreSQL closure table (MeshWeaver.Plugins <c>DependencyGraphSchema</c>) and held to the SAME
/// answer row for row, path included (the shared corpus
/// <c>test/MeshWeaver.Fixture/DependencyClosureCorpus</c> pins both).
///
/// <para><b>The rule both implement.</b> An edge <c>(source, target)</c> means source depends on
/// target. For a pair (ancestor <c>A</c>, descendant <c>D</c>, D depending on A transitively) the
/// depth is the shortest chain, and the NEXT HOP — the first step from D towards A — is the
/// smallest node under ordinal comparison among D's direct dependencies that lie on a shortest
/// chain. A path is rebuilt by following next hops from D to A. A node is never its own dependent,
/// so a cycle yields the two pairs <c>(a, b)</c> and <c>(b, a)</c> and no self-pair.</para>
///
/// <para>Pure and immutable: an instance is built from one edge set and answers any number of
/// reads; a changed edge set is a new instance. A caller that keeps one across reads holds it on a
/// mesh-scoped service, never in a static.</para>
/// </summary>
public sealed class DependencyClosure
{
    private readonly ImmutableDictionary<string, ImmutableSortedSet<string>> forward;
    private readonly ImmutableDictionary<string, ImmutableSortedSet<string>> reverse;

    private DependencyClosure(
        ImmutableDictionary<string, ImmutableSortedSet<string>> forward,
        ImmutableDictionary<string, ImmutableSortedSet<string>> reverse)
    {
        this.forward = forward;
        this.reverse = reverse;
    }

    /// <summary>Builds the closure over <paramref name="edges"/>; a self-edge is ignored.</summary>
    /// <param name="edges">(source, target) pairs — source depends on target.</param>
    public static DependencyClosure Of(IEnumerable<(string Source, string Target)> edges)
    {
        ArgumentNullException.ThrowIfNull(edges);
        var f = new Dictionary<string, ImmutableSortedSet<string>.Builder>(StringComparer.Ordinal);
        var r = new Dictionary<string, ImmutableSortedSet<string>.Builder>(StringComparer.Ordinal);
        static void Add(Dictionary<string, ImmutableSortedSet<string>.Builder> map, string key, string value)
        {
            if (!map.TryGetValue(key, out var set))
                map[key] = set = ImmutableSortedSet.CreateBuilder<string>(StringComparer.Ordinal);
            set.Add(value);
        }
        foreach (var (source, target) in edges)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(target)
                || string.Equals(source, target, StringComparison.Ordinal))
                continue;
            Add(f, source, target);
            Add(r, target, source);
        }
        return new DependencyClosure(
            f.ToImmutableDictionary(kv => kv.Key, kv => kv.Value.ToImmutable(), StringComparer.Ordinal),
            r.ToImmutableDictionary(kv => kv.Key, kv => kv.Value.ToImmutable(), StringComparer.Ordinal));
    }

    /// <summary>Everything that depends on <paramref name="target"/>, transitively, nearest first
    /// (then ordinal); each path runs <paramref name="target"/> → … → node.</summary>
    /// <param name="target">The dependency whose dependents are asked for.</param>
    /// <param name="maxDepth">The deepest row to return, or null for all.</param>
    public ImmutableList<DependencyClosureRow> Dependents(string target, int? maxDepth = null)
    {
        var (depth, nextHop) = TowardsAncestor(target);
        return depth
            .Where(kv => maxDepth is null || kv.Value <= maxDepth)
            .OrderBy(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new DependencyClosureRow(kv.Key, kv.Value,
                [.. Walk(kv.Key, target, nextHop).Reverse()]))
            .ToImmutableList();
    }

    /// <summary>Everything <paramref name="source"/> depends on, transitively, nearest first (then
    /// ordinal); each path runs <paramref name="source"/> → … → node.</summary>
    /// <param name="source">The dependent whose dependencies are asked for.</param>
    /// <param name="maxDepth">The deepest row to return, or null for all.</param>
    public ImmutableList<DependencyClosureRow> Dependencies(string source, int? maxDepth = null)
    {
        var depth = Distances(source, forward);
        return depth
            .Where(kv => maxDepth is null || kv.Value <= maxDepth)
            .OrderBy(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new DependencyClosureRow(kv.Key, kv.Value,
                // The chain follows the ANCESTOR's next hops — the same rows the closure table holds.
                [.. Walk(source, kv.Key, TowardsAncestor(kv.Key).NextHop)]))
            .ToImmutableList();
    }

    /// <summary>The chain <paramref name="dependent"/> → … → <paramref name="dependency"/>, or null
    /// when <paramref name="dependent"/> does not depend on it.</summary>
    /// <param name="dependent">Where the chain starts.</param>
    /// <param name="dependency">Where it ends.</param>
    public ImmutableArray<string>? Path(string dependent, string dependency)
    {
        if (string.Equals(dependent, dependency, StringComparison.Ordinal))
            return [dependent];
        var (depth, nextHop) = TowardsAncestor(dependency);
        return depth.ContainsKey(dependent) ? [.. Walk(dependent, dependency, nextHop)] : null;
    }

    /// <summary>Shortest distance from every dependent of <paramref name="ancestor"/> to it, and each
    /// one's next hop: the smallest direct dependency that is one step closer.</summary>
    private (ImmutableDictionary<string, int> Depth, ImmutableDictionary<string, string> NextHop)
        TowardsAncestor(string ancestor)
    {
        var depth = Distances(ancestor, reverse);
        var hops = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var (node, d) in depth)
            hops[node] = forward[node].First(n =>
                d == 1 ? string.Equals(n, ancestor, StringComparison.Ordinal)
                       : depth.TryGetValue(n, out var dn) && dn == d - 1);
        return (depth, hops.ToImmutable());
    }

    /// <summary>Breadth-first distances from <paramref name="start"/> over <paramref name="next"/>,
    /// excluding the start itself.</summary>
    private static ImmutableDictionary<string, int> Distances(
        string start, ImmutableDictionary<string, ImmutableSortedSet<string>> next)
    {
        var depth = ImmutableDictionary.CreateBuilder<string, int>(StringComparer.Ordinal);
        var frontier = ImmutableList.Create(start);
        for (var d = 1; !frontier.IsEmpty; d++)
        {
            var reached = ImmutableList.CreateBuilder<string>();
            foreach (var node in frontier)
                if (next.TryGetValue(node, out var neighbours))
                    foreach (var n in neighbours)
                        if (!string.Equals(n, start, StringComparison.Ordinal) && !depth.ContainsKey(n))
                        {
                            depth[n] = d;
                            reached.Add(n);
                        }
            frontier = reached.ToImmutable();
        }
        return depth.ToImmutable();
    }

    private static IEnumerable<string> Walk(string from, string to, ImmutableDictionary<string, string> nextHop)
    {
        var current = from;
        yield return current;
        while (!string.Equals(current, to, StringComparison.Ordinal))
        {
            // Every node with a distance has a hop, by construction; a missing one is a defect in
            // this class, never a shorter answer.
            current = nextHop.TryGetValue(current, out var hop)
                ? hop
                : throw new InvalidOperationException($"No next hop from '{current}' towards '{to}'.");
            yield return current;
        }
    }
}
