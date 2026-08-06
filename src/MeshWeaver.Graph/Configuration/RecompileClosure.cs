using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Which NodeTypes must RECOMPILE after a set of node paths changed — the affected-set half of the
/// "assembly is the deliverable" rule. An update channel that lands new <c>Source/*.cs</c> nodes and
/// walks away leaves every affected assembly stale: the mesh keeps serving the old behavior while
/// the nodes read current (the repeatedly-paid GitSync deploy lesson, and the 2026-08-04
/// <c>ParameterSegment</c> incident for cross-package sharers).
///
/// <para><b>The affected set is a DIRECT match, deliberately not transitive.</b> A type is affected
/// exactly when (a) its OWN NodeType node is among the changed paths (its authored definition
/// moved), or (b) a changed path falls inside its resolved Source/Test queries — the SAME
/// <see cref="CodeQueryResolver"/> expansion + <see cref="CodeQueryResolver.Matches"/> the compiler
/// and the release classification run, so "affected" can never disagree with "compiled". It is not
/// transitive because a sharer compiles the shared files' TEXT into its own assembly
/// (<see cref="NodeTypeDependencyGraph"/>): when <c>A/Source/X.cs</c> changes, every type whose
/// queries reach <c>X.cs</c> holds stale text — but a type that merely shares the SHARER's own
/// sources holds text that did not change, so its assembly is already correct.</para>
///
/// <para><b>Ordering</b> reuses <see cref="NodeTypeDependencyGraph.TopologicalOrder(IReadOnlyDictionary{string, ImmutableHashSet{string}}, out ImmutableList{string})"/>
/// over the FULL type graph, filtered to the affected set — dependencies before dependents, the
/// same deterministic order the pre-warmer compiles in. Filtering the full order (rather than
/// ordering a subgraph) keeps edges THROUGH unaffected intermediates intact.</para>
///
/// <para>Pure data on purpose — no hub, no mesh, no I/O — so the rules are unit-testable without a
/// fixture, exactly like <see cref="NodeTypeDependencyGraph"/>.</para>
/// </summary>
public static class RecompileClosure
{
    /// <summary>
    /// The NodeTypes affected by <paramref name="changedNodePaths"/>: a type whose own node changed,
    /// or whose expanded Source/Test queries match any changed path. Deterministic (sorted by path).
    /// </summary>
    /// <param name="types">Every known NodeType: path → definition (null = defaults apply).</param>
    /// <param name="changedNodePaths">Node paths written or pruned by the update.</param>
    public static ImmutableSortedSet<string> AffectedTypes(
        IReadOnlyDictionary<string, NodeTypeDefinition?> types,
        IReadOnlyCollection<string> changedNodePaths)
    {
        if (types.Count == 0 || changedNodePaths.Count == 0)
            return ImmutableSortedSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase);

        var changed = changedNodePaths
            .Where(p => !string.IsNullOrEmpty(p))
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

        var affected = ImmutableSortedSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (typePath, definition) in types)
        {
            if (changed.Contains(typePath))
            {
                affected.Add(typePath);
                continue;
            }

            // The SAME expansion the compiler runs (defaults, $self, @-shorthand, bare-namespace
            // rebasing) and the SAME path matcher the release classification uses — a private
            // re-implementation here is how "affected" and "compiled" would drift apart.
            var expanded = CodeQueryResolver
                .ExpandAll(definition?.Sources, CodeQueryResolver.DefaultSources, typePath)
                .Concat(CodeQueryResolver.ExpandAll(definition?.Tests, CodeQueryResolver.DefaultTests, typePath))
                .ToArray();
            if (changed.Any(p => CodeQueryResolver.Matches(p, expanded)))
                affected.Add(typePath);
        }
        return affected.ToImmutable();
    }

    /// <summary>
    /// The affected set in COMPILE ORDER — dependencies before dependents — computed by filtering
    /// the full <see cref="NodeTypeDependencyGraph.TopologicalOrder(IReadOnlyDictionary{string, ImmutableHashSet{string}}, out ImmutableList{string})"/>
    /// down to <paramref name="affected"/>. <paramref name="cyclic"/> reports the affected members
    /// that sit in a dependency cycle (they are still emitted, deterministically — a cycle must be
    /// SEEN, never silently drop a recompile); callers surface them loudly.
    /// </summary>
    public static ImmutableList<string> OrderAffected(
        IReadOnlyDictionary<string, NodeTypeDefinition?> types,
        IReadOnlyCollection<string> affected,
        out ImmutableList<string> cyclic)
    {
        if (affected.Count == 0)
        {
            cyclic = ImmutableList<string>.Empty;
            return ImmutableList<string>.Empty;
        }
        var order = NodeTypeDependencyGraph.TopologicalOrder(
            NodeTypeDependencyGraph.Build(types), out var allCyclic);
        var affectedSet = affected.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        cyclic = allCyclic.Where(affectedSet.Contains).ToImmutableList();
        return order.Where(affectedSet.Contains).ToImmutableList();
    }
}
