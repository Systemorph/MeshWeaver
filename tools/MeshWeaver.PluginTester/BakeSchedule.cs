using System.Collections.Immutable;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;

namespace MeshWeaver.PluginTester;

/// <summary>
/// How the <c>compile</c> bake (<see cref="TreeBake"/>) schedules its NodeTypes: in PARALLEL, at
/// most <see cref="TreeBake.Options.MaxParallel"/> at a time, each type starting only once every
/// type it is ordered after has SETTLED. The ordering is the runtime's own
/// (<see cref="NodeTypeDependencyGraph"/>: a type that pulls Code nodes out of another type's
/// subtree is built after that type) and the execution is <see cref="Cascade"/> — the same
/// <c>Merge(maxParallel)</c> gate <c>mw-plugin-test build</c> runs its packages through, so no
/// semaphore and no thread parked on a wait.
///
/// <para><b>Why the order costs almost nothing and changes nothing.</b> A bake compile references
/// the FRAMEWORK and its MODULES only — never another NodeType's emitted assembly (see
/// <c>CascadeBuild.BuildPackage</c>, Systemorph/MeshWeaver.Plugins#1970) — so no type's bytes depend
/// on another type's compile having run. The edges are honoured anyway because they are the
/// platform's declared build order and a future reference to a sibling's output must not race it;
/// what makes the output byte-identical to a serial run is that every compile is a pure function
/// of the node set and the reference set, and that the results are folded back in path order.</para>
///
/// <para><b>Settled, not green.</b> Every type reports GREEN to the cascade whatever its compile
/// verdict: the bake has always compiled EVERY type and reported each failure on its own line, and
/// a dependent the cascade blocked on a red upstream would be a type the serial bake compiled and
/// the parallel one silently dropped — a shrunk denominator wearing the same exit code. The compile
/// verdict travels in the payload.</para>
/// </summary>
internal static class BakeSchedule
{
    /// <summary>
    /// The acyclic build-after map over <paramref name="types"/>: each type's
    /// <see cref="NodeTypeDependencyGraph.DependenciesOf"/>, with the edges inside a dependency
    /// cycle removed (<see cref="NodeTypeDependencyGraph.WithoutCycleEdges"/>). Keyed and valued
    /// with the types' own path spellings.
    /// <para>🚨 The graph is case-INSENSITIVE and the bake's work items are ORDINAL: the loader,
    /// the node set and the bake's grouping keep <c>Ctrl/Foo</c> and <c>Ctrl/foo</c> apart, so
    /// the graph runs over case-insensitive GROUPS (spelled by their ordinal-least member) and
    /// every member gets its group's order back, expanded to every ordinal spelling of each group
    /// it waits for. That can only order MORE, never drop a type: a map keyed case-insensitively
    /// would instead throw on the pair and abort the whole bake.</para>
    /// </summary>
    /// <param name="types">The NodeTypes the bake attempts: path and definition.</param>
    internal static ImmutableDictionary<string, ImmutableArray<string>> BuildAfter(
        IEnumerable<(string Path, NodeTypeDefinition? Definition)> types)
    {
        var all = types.ToList();
        var groups = all
            .GroupBy(t => t.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Key: g.Select(t => t.Path).Min(StringComparer.Ordinal)!, Members: g.ToList()))
            .ToList();
        var keys = groups.Select(g => g.Key).ToArray();
        var spellings = groups.ToDictionary(
            g => g.Key,
            g => g.Members.Select(t => t.Path).Distinct(StringComparer.Ordinal).ToImmutableArray(),
            StringComparer.OrdinalIgnoreCase);
        var groupDependencies = groups.ToDictionary(
            g => g.Key,
            g => g.Members
                .SelectMany(t => NodeTypeDependencyGraph.DependenciesOf(t.Definition, t.Path, keys))
                .Where(d => !string.Equals(d, g.Key, StringComparison.OrdinalIgnoreCase))
                .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        var acyclic = NodeTypeDependencyGraph.WithoutCycleEdges(groupDependencies);

        var result = ImmutableDictionary.CreateBuilder<string, ImmutableArray<string>>(StringComparer.Ordinal);
        foreach (var (key, members) in groups)
        {
            var after = acyclic[key]
                .SelectMany(d => spellings[d])
                .Distinct(StringComparer.Ordinal)
                .OrderBy(d => d, StringComparer.Ordinal)
                .ToImmutableArray();
            foreach (var member in members)
                result[member.Path] = after;
        }
        return result.ToImmutable();
    }

    /// <summary>
    /// Runs <paramref name="work"/> once per id, at most <paramref name="maxParallel"/> at a time,
    /// never starting an id before every id <paramref name="buildAfter"/> names for it has
    /// finished. Emits every id's result once all have settled, ORDINAL by id — the order the
    /// serial bake produced them in.
    /// </summary>
    /// <typeparam name="T">The per-type payload.</typeparam>
    /// <param name="ids">The ids to run.</param>
    /// <param name="buildAfter">The ids each id waits for; must be acyclic over <paramref name="ids"/>.</param>
    /// <param name="work">One id's work. Its own failures belong in the payload.</param>
    /// <param name="maxParallel">The concurrency cap — 1 is the serial bake.</param>
    internal static IObservable<ImmutableArray<Cascade.NodeResult<T>>> Run<T>(
        IReadOnlyCollection<string> ids,
        IReadOnlyDictionary<string, ImmutableArray<string>> buildAfter,
        Func<string, T> work,
        int maxParallel) =>
        Cascade.Run<T>(
            ids,
            id => buildAfter.TryGetValue(id, out var deps) ? deps : [],
            (id, _) => (work(id), true),
            maxParallel);
}
