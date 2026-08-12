using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Which NodeTypes a NodeType must be built AFTER — computed from its DECLARED SOURCES, not guessed.
///
/// <para><b>Why this exists.</b> A NodeType may pull Code nodes out of ANOTHER NodeType's subtree:
/// <c>Store/Plugin</c> declares <c>shared=@Store/Coupon/Source</c>, <c>shared=@Store/Order/Source</c>
/// and <c>shared=@Store/BillingProfile/Source</c>. Those files are compiled INTO
/// <c>Store/Plugin</c>'s assembly, so <c>Store/Plugin</c> is not buildable until the types that own
/// them are themselves settled. Nothing expressed that ordering, so a cold pod raced every dynamic
/// type at once and the dependents blew their 60s activation budget:</para>
///
/// <code>
/// [STALE-CALLBACK] cache/krhs…: 3 callback(s) pending &gt; 30000ms:
///     SubscribeRequest@AgenticEngineering(33034ms),
///     SubscribeRequest@DataModeling(45043ms),
///     SubscribeRequest@Store/Plugin(45028ms)
/// System.TimeoutException: No response received … within 00:01:00
///     for request SubscribeRequest → target Store/Plugin.
/// </code>
///
/// <para>Every <c>AgenticEngineering</c>-style plugin root IS a <c>Store/Plugin</c> node, so the
/// instance cannot activate until its type is built, and the type cannot build until ITS
/// dependencies are. The cascade is a dependency chain, so the fix is a dependency ORDER.</para>
///
/// <para><b>Pure data on purpose.</b> Everything here is a static function over paths and declared
/// source queries — no hub, no mesh, no I/O — so the ordering rules are unit-testable without a
/// fixture (the same reason <c>ProvisionPlan</c> keeps its plan pure).</para>
/// </summary>
public static class NodeTypeDependencyGraph
{
    /// <summary>
    /// The node paths one NodeType's source/test queries reach into, after expansion by
    /// <see cref="CodeQueryResolver"/> (so <c>$self</c>, <c>@</c>-shorthand and bare-namespace
    /// rebasing are already applied and we read the SAME queries the compiler runs).
    /// </summary>
    public static ImmutableHashSet<string> ReferencedPaths(NodeTypeDefinition? definition, string selfPath)
    {
        var queries = CodeQueryResolver
            .ExpandAll(definition?.Sources, CodeQueryResolver.DefaultSources, selfPath)
            .Concat(CodeQueryResolver.ExpandAll(definition?.Tests, CodeQueryResolver.DefaultTests, selfPath));

        var paths = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var query in queries)
            foreach (var value in PathValues(query))
                paths.Add(value);
        return paths.ToImmutable();
    }

    /// <summary>
    /// The NodeType that OWNS a node path: the longest known type path that is the path itself or a
    /// parent of it. Longest-wins matters — with both <c>Store</c> and <c>Store/Coupon</c> known,
    /// <c>Store/Coupon/Source</c> belongs to <c>Store/Coupon</c>, not <c>Store</c>.
    /// </summary>
    public static string? OwningType(string path, IEnumerable<string> knownTypePaths) =>
        knownTypePaths
            .Where(t => !string.IsNullOrEmpty(t) && IsSelfOrUnder(path, t))
            .OrderByDescending(t => t.Length)
            .ThenBy(t => t, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    /// <summary>
    /// The types <paramref name="selfPath"/> must be built after. A reference into a type's own
    /// subtree is not a dependency, and a reference to an unknown path is ignored rather than
    /// invented — an unresolvable edge must never stall the build order.
    /// </summary>
    public static ImmutableHashSet<string> DependenciesOf(
        NodeTypeDefinition? definition, string selfPath, IEnumerable<string> knownTypePaths)
    {
        var known = knownTypePaths as IReadOnlyCollection<string> ?? knownTypePaths.ToArray();
        var deps = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in ReferencedPaths(definition, selfPath))
        {
            var owner = OwningType(reference, known);
            if (owner is not null && !string.Equals(owner, selfPath, StringComparison.OrdinalIgnoreCase))
                deps.Add(owner);
        }
        return deps.ToImmutable();
    }

    /// <summary>Builds the whole dependency map from the known NodeTypes and their definitions.</summary>
    public static ImmutableDictionary<string, ImmutableHashSet<string>> Build(
        IReadOnlyDictionary<string, NodeTypeDefinition?> types)
    {
        var known = types.Keys.ToArray();
        var map = ImmutableDictionary.CreateBuilder<string, ImmutableHashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, definition) in types)
            map[path] = DependenciesOf(definition, path, known);
        return map.ToImmutable();
    }

    /// <summary>
    /// DEPENDENCIES FIRST. A deterministic topological order (Kahn, ties broken by path) so the same
    /// mesh always warms in the same sequence and a failure is reproducible.
    ///
    /// <para><b>A cycle is an ordering OBSTACLE, not a priority signal (issue #1347).</b> The sort
    /// runs over the graph's STRONGLY CONNECTED COMPONENTS, not over individual types: every type
    /// that is mutually reachable with another collapses into one unit, and the peel orders the
    /// units. A component's members are emitted together, in path order, as soon as everything
    /// OUTSIDE the component that it waits on has been emitted — which for a component with no
    /// external dependencies means the very first round, exactly like any other independent type.
    /// The condensation of a directed graph is acyclic by construction, so the peel can never
    /// stall.</para>
    ///
    /// <para>🚨 It used to stall, and the stall was the bug. Cycle members were released only when
    /// nothing else was ready — i.e. AFTER every acyclic type had already been peeled — so a cycle
    /// was DEMOTED TO LAST for the crime of being a cycle. On memex that put
    /// <c>Store/Coupon ↔ Store/Order ↔ Store/Plugin</c>, and behind them <c>Store/Catalog</c>,
    /// <c>Store/Enrollment</c>, <c>Store/Install</c>, <c>Store/Provision</c>, at the very END of an
    /// 85-type sweep — the store cover and paywall chain, the most user-visible types on the
    /// portal, made cheap last. Nothing about a cycle says "warm this late"; it only says "these
    /// three cannot be ordered relative to each other".</para>
    ///
    /// <para>Types merely DOWNSTREAM of a cycle are not part of it and still order behind it: they
    /// are their own single-member components with an edge into the cycle's component. That
    /// distinction is what lets the warmer report them <c>UpstreamFailed</c> immediately instead of
    /// attempting a build that cannot succeed and burning a whole per-type budget. Every input path
    /// is emitted exactly once, always.</para>
    /// </summary>
    public static ImmutableList<string> TopologicalOrder(
        IReadOnlyDictionary<string, ImmutableHashSet<string>> dependencies,
        out ImmutableList<string> cyclic)
    {
        // Only edges to types we actually know about can be waited on, and a self-edge is not a
        // dependency (DependenciesOf already drops it; belt and braces, because a self-edge would
        // otherwise read as a one-member "cycle").
        var edges = dependencies.ToDictionary(
            kv => kv.Key,
            kv => kv.Value
                .Where(dependencies.ContainsKey)
                .Where(d => !string.Equals(d, kv.Key, StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

        var (componentOf, members) = Condense(edges);

        // Unit edges: a component waits on every component any of its members reaches, minus itself.
        var remaining = members.Keys.ToDictionary(
            unit => unit,
            unit => members[unit]
                .SelectMany(m => edges[m])
                .Select(d => componentOf[d])
                .Where(c => !string.Equals(c, unit, StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

        // Which components wait ON a given component — so releasing one only re-examines its actual
        // dependents instead of rescanning the whole map every round.
        var dependents = members.Keys.ToDictionary(
            unit => unit,
            _ => new List<string>(),
            StringComparer.OrdinalIgnoreCase);
        foreach (var (unit, deps) in remaining)
            foreach (var dep in deps)
                dependents[dep].Add(unit);

        // 🚨 A PRIORITY peel, not a round-based one: always take the smallest READY component,
        // re-evaluating readiness after every release. That is what keeps a dependency and its
        // dependents CONTIGUOUS — `Store/Plugin`'s component is followed immediately by
        // `Store/Catalog`, rather than by everything else that happened to be ready in the same
        // round. On a real mesh that is the difference between the store/paywall chain warming as
        // one block near the front and being spread across the whole sweep (#1347).
        //
        // Still fully deterministic: the key is the component's smallest member path, and the
        // ready set is ordered, so the same graph always yields the same sequence.
        var ready = new SortedSet<string>(
            remaining.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key),
            StringComparer.OrdinalIgnoreCase);

        var ordered = ImmutableList.CreateBuilder<string>();
        var cyclicBuilder = ImmutableList.CreateBuilder<string>();
        while (ready.Count > 0)
        {
            var unit = ready.Min!;
            ready.Remove(unit);
            remaining.Remove(unit);

            var group = members[unit];
            ordered.AddRange(group);
            if (group.Count > 1)
                cyclicBuilder.AddRange(group);

            foreach (var dependent in dependents[unit])
                if (remaining.TryGetValue(dependent, out var deps)
                    && deps.Remove(unit)
                    && deps.Count == 0)
                    ready.Add(dependent);
        }

        // Unreachable: the condensation of a directed graph is acyclic, so the peel always drains.
        // Emitting any leftover beats dropping a type — a type missing from the warm order is a
        // type nobody compiles until a user trips over it, which is the original bug.
        foreach (var unit in remaining.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            ordered.AddRange(members[unit]);
            cyclicBuilder.AddRange(members[unit]);
        }

        cyclic = cyclicBuilder.ToImmutable();
        return ordered.ToImmutable();
    }

    /// <summary>
    /// Collapse the graph into its strongly connected components: two types share a component when
    /// each can reach the other through dependency edges — the exact definition of "these cannot be
    /// ordered relative to each other". Returns the component key of every path (its component's
    /// SMALLEST member path) and each component's members in path order.
    ///
    /// <para>Computed from a forward reachability closure rather than Tarjan: the graph is one node
    /// per dynamic NodeType (a few hundred at the largest mesh we run) and this reads as its own
    /// specification, which for a function that decides compile ORDER is worth more than the
    /// asymptotics. It is also the same walk the previous cycle check did, hoisted out of the peel
    /// loop and computed once.</para>
    /// </summary>
    private static (Dictionary<string, string> ComponentOf, Dictionary<string, List<string>> Members)
        Condense(IReadOnlyDictionary<string, HashSet<string>> edges)
    {
        var reaches = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var start in edges.Keys)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stack = new Stack<string>(edges[start]);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (!seen.Add(current))
                    continue;
                if (edges.TryGetValue(current, out var next))
                    foreach (var n in next)
                        stack.Push(n);
            }
            reaches[start] = seen;
        }

        var componentOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var members = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        // Ascending path order, so the FIRST unassigned path of a component is its smallest member
        // and therefore the component key — no second pass to find it.
        foreach (var path in edges.Keys.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            if (componentOf.ContainsKey(path))
                continue;
            var group = edges.Keys
                .Where(other => string.Equals(other, path, StringComparison.OrdinalIgnoreCase)
                    || (reaches[path].Contains(other) && reaches[other].Contains(path)))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var member in group)
                componentOf[member] = path;
            members[path] = group;
        }

        return (componentOf, members);
    }

    /// <summary>Convenience overload for callers that do not care which types were cyclic.</summary>
    public static ImmutableList<string> TopologicalOrder(
        IReadOnlyDictionary<string, ImmutableHashSet<string>> dependencies) =>
        TopologicalOrder(dependencies, out _);

    /// <summary>
    /// The dependency that BLOCKS <paramref name="path"/> — the first (path-ordered, so the answer
    /// is stable) direct dependency already known to have failed — or <c>null</c> when nothing
    /// upstream is broken.
    ///
    /// <para>Only DIRECT dependencies are examined, and that is deliberate: walk the types in
    /// <see cref="TopologicalOrder(IReadOnlyDictionary{string, ImmutableHashSet{string}}, out ImmutableList{string})"/>
    /// and add each blocked type to <paramref name="failed"/> as you skip it, and the block
    /// propagates TRANSITIVELY on its own — a dependent of a skipped type sees its direct
    /// dependency already in the set. No second traversal, and no way for the two notions of
    /// "reachable" to disagree.</para>
    ///
    /// <para>Building a dependent whose upstream is broken cannot succeed: its assembly is missing
    /// the very sources the upstream owns. Detecting that up front turns a guaranteed per-type
    /// timeout into an immediate, named outcome.</para>
    /// </summary>
    public static string? FirstBlockedBy(
        string path,
        IReadOnlyDictionary<string, ImmutableHashSet<string>> dependencies,
        IReadOnlySet<string> failed) =>
        dependencies.TryGetValue(path, out var deps)
            ? deps.Where(failed.Contains)
                  .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                  .FirstOrDefault()
            : null;

    /// <summary><c>path</c> is <paramref name="root"/> itself or lives under it.</summary>
    private static bool IsSelfOrUnder(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The <c>path:</c> / <c>namespace:</c> values of one expanded query. Those are the only tokens
    /// that name a location; everything else (<c>scope:</c>, <c>nodeType:</c>) is a filter.
    /// </summary>
    private static IEnumerable<string> PathValues(string query)
    {
        foreach (var token in query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = token.IndexOf(':');
            if (colon <= 0) continue;
            var key = token[..colon];
            var value = token[(colon + 1)..];
            if (value.Length == 0) continue;
            if (key.Equals("path", StringComparison.OrdinalIgnoreCase)
                || key.Equals("namespace", StringComparison.OrdinalIgnoreCase))
                yield return value;
        }
    }
}
