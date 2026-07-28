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
    /// <para>A dependency CYCLE cannot be ordered, so it must not be able to drop or duplicate a
    /// type. When the peel stalls, ONLY the types genuinely in a cycle (self-reachable through the
    /// remaining edges) are released — in path order — and the peel then RESUMES. Types merely
    /// DOWNSTREAM of a cycle are not cyclic and must not be treated as such: flushing the whole
    /// remainder in path order would put a dependent ahead of the dependency it waits on, so it gets
    /// attempted instead of failing fast, burning a full per-type budget on a build that cannot
    /// succeed. Every input path is emitted exactly once, always.</para>
    /// </summary>
    public static ImmutableList<string> TopologicalOrder(
        IReadOnlyDictionary<string, ImmutableHashSet<string>> dependencies,
        out ImmutableList<string> cyclic)
    {
        var remaining = dependencies.ToDictionary(
            kv => kv.Key,
            // Only edges to types we actually know about can be waited on.
            kv => kv.Value.Where(dependencies.ContainsKey)
                          .ToHashSet(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

        var ordered = ImmutableList.CreateBuilder<string>();
        var cyclicBuilder = ImmutableList.CreateBuilder<string>();
        while (remaining.Count > 0)
        {
            var ready = remaining
                .Where(kv => kv.Value.Count == 0)
                .Select(kv => kv.Key)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (ready.Length == 0)
            {
                // Stalled: release the actual cycle members so their dependents can order behind
                // them on the next pass.
                ready = remaining.Keys
                    .Where(p => IsInCycle(p, remaining))
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                // Defensive: a stall with nothing self-reachable is not reachable by construction,
                // but breaking out beats looping forever — the leftovers are still emitted below.
                if (ready.Length == 0)
                    break;

                cyclicBuilder.AddRange(ready);
            }

            foreach (var path in ready)
            {
                ordered.Add(path);
                remaining.Remove(path);
            }
            foreach (var deps in remaining.Values)
                foreach (var path in ready)
                    deps.Remove(path);
        }

        var stranded = remaining.Keys.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        cyclicBuilder.AddRange(stranded);
        ordered.AddRange(stranded);

        cyclic = cyclicBuilder.ToImmutable();
        return ordered.ToImmutable();
    }

    /// <summary>
    /// Is <paramref name="start"/> genuinely part of a cycle — can it reach ITSELF by following
    /// remaining dependency edges? Distinguishes a cycle member from a type that merely depends on
    /// one, which is the difference between "cannot be ordered" and "must be ordered later".
    /// </summary>
    private static bool IsInCycle(string start, IReadOnlyDictionary<string, HashSet<string>> remaining)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>(remaining[start]);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current.Equals(start, StringComparison.OrdinalIgnoreCase))
                return true;
            if (!seen.Add(current))
                continue;
            if (remaining.TryGetValue(current, out var deps))
                foreach (var next in deps)
                    stack.Push(next);
        }
        return false;
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
