using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph;

/// <summary>
/// The ORDER an import must write one source's nodes in, as a list of STAGES: everything inside a
/// stage may be written concurrently, and a stage begins only after the previous one has completed.
/// <see cref="StageOfPath"/> answers "which stage is this path written in" so a caller can decide,
/// per node, whether this pass really does put its NodeType in place first.
/// </summary>
/// <param name="Stages">Stage 0 first. Every input node appears in exactly one stage, exactly once —
/// including duplicates of the same path, which stay together in input order.</param>
/// <param name="Cyclic">The paths that are mutually dependent, so no order can satisfy them all.
/// Empty for every well-formed source. See <see cref="ImportWriteOrder"/> for the policy.</param>
/// <param name="StageOfPath">Case-insensitive path → stage index.</param>
public sealed record ImportWritePlan(
    ImmutableList<ImmutableList<MeshNode>> Stages,
    ImmutableList<string> Cyclic,
    ImmutableDictionary<string, int> StageOfPath);

/// <summary>
/// Orders a static-repo import's writes so a NODE TYPE IS WRITTEN BEFORE THE INSTANCES THAT NAME IT
/// (issue #2556).
///
/// <para><b>The incident.</b> The create pipeline refuses a node whose <c>NodeType</c> names nothing
/// the mesh knows — <c>Upsert of '…' failed: NodeType 'X' is not registered</c>. The importer wrote a
/// source's nodes in whatever order the source enumerated them, five at a time, so a repo shipping an
/// instance of a type it introduces had its instance refused whenever enumeration happened to put it
/// first. The <c>#2229</c> baseline guard then HELD the sync baseline precisely so a later pass would
/// retry — but the retry re-ran the identical ordering, so the identical refusal came back. memex-cloud
/// measured 6,902 refusals in 90 minutes, and one node refused 40 times in 120: a loop that could not
/// converge, because the ordering it retried was the thing that was wrong.</para>
///
/// <para><b>Ordering is SUFFICIENT — the refusal is not about a TypeRegistry.</b> Despite its wording,
/// the check is <c>IStaticNodeProvider</c> ∪ <c>IStorageAdapter.Exists(typePath)</c> (MeshExtensions,
/// create step 3): a NODE at the type's path, not a compiled assembly and not a hub type registration.
/// The write that puts it there is commit-then-publish — <c>CreateNodeResponse.Ok</c> is posted only
/// after storage emits — so a completed type write is already visible to the next node's probe. There
/// is no registration lag for ordering to merely postpone. (What DOES lag is the compile that turns
/// that node into a live type; instances do not wait on it, which is why the two must not be
/// conflated.)</para>
///
/// <para><b>Two edges, both derived from paths only.</b> Nothing here casts <c>Content</c>, because a
/// node read back from storage carries its content as an untyped <c>JsonElement</c>:</para>
/// <list type="bullet">
///   <item><b>Type before instance</b> — a node depends on the node whose PATH equals its
///     <c>NodeType</c>, when this import carries one. This is the whole of #2556.</item>
///   <item><b>Compile inputs before their type</b> — a type node depends on the <c>Source/</c> and
///     <c>Test/</c> nodes under its own path, because creating the type is what triggers the compile
///     that reads them. Same rule <c>PackageInstaller.InstallNodeRepo</c> has applied to node-repo
///     plugin installs since #815; deliberately NOT every descendant, because a typed instance nested
///     under a leaf-shaped type would then be dragged AHEAD of the type it needs.</item>
/// </list>
///
/// <para><b>Cycle policy.</b> A cycle — <c>A</c> typed by <c>B</c> while <c>B</c> is typed by <c>A</c>
/// — is a defect in the SOURCE, not a state of the mesh, and no write order can satisfy it. The peel
/// therefore runs over <see cref="NodeTypeDependencyGraph.TopologicalOrder(IReadOnlyDictionary{string,
/// ImmutableHashSet{string}}, out ImmutableList{string})"/>'s strongly-connected-component
/// condensation, which gives three properties this import needs:</para>
/// <list type="number">
///   <item><b>It never stalls and never drops a node.</b> The condensation of a directed graph is
///     acyclic, so every input path is emitted exactly once even in a cycle.</item>
///   <item><b>A cycle is NOT demoted to last</b> (#1347). Its members are emitted at the position the
///     component becomes ready, in path order — deterministic, so the same source always produces the
///     same sequence and a failure is reproducible.</item>
///   <item><b>It is REPORTED.</b> <see cref="ImportWritePlan.Cyclic"/> names the members so the import
///     can say so out loud instead of failing mysteriously. A member the type check then refuses is
///     recorded as a BLOCKED CREATE — named, Warning, and not counted as a per-file failure — because
///     retrying it cannot help, and counting it as a failure would freeze the whole Space's sync
///     baseline over one unfixable node.</item>
/// </list>
///
/// <para><b>Pure on purpose</b> — a function over paths, no hub, no mesh, no I/O — so the ordering
/// rules are unit-testable without a fixture, exactly like <see cref="NodeTypeDependencyGraph"/>
/// itself and <c>StaticRepoImporter.ComputePrunableNodes</c>.</para>
/// </summary>
public static class ImportWriteOrder
{
    /// <summary>
    /// A node is a TYPE DEFINITION when its content is a <see cref="NodeTypeDefinition"/> or — the
    /// case that actually happens on a round-trip — its <c>NodeType</c> field is the meta-type marker
    /// <see cref="MeshNode.NodeTypePath"/>. The second half is not belt-and-braces: a node read back
    /// from storage on a hub without <see cref="NodeTypeDefinition"/> in its TypeRegistry degrades to
    /// an untyped <c>JsonElement</c>, so the pattern match alone silently answers "not a type".
    /// </summary>
    public static bool IsNodeTypeDefinition(MeshNode node) =>
        node.Content is NodeTypeDefinition
        || string.Equals(node.NodeType, MeshNode.NodeTypePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when this plan writes <paramref name="node"/>'s NodeType in a STRICTLY EARLIER stage than
    /// the node itself — i.e. the type is guaranteed to be present by the time the node is written.
    /// False for a node whose type this import does not carry (a cross-source type: nothing here can
    /// order it into existence), and false for a cycle member (its type is carried, but not ahead of
    /// it). Those are exactly the nodes whose type must be probed against the mesh instead.
    /// </summary>
    public static bool TypeIsOrderedAhead(ImportWritePlan plan, MeshNode node) =>
        !string.IsNullOrEmpty(node.NodeType)
        && plan.StageOfPath.TryGetValue(node.NodeType, out var typeStage)
        && plan.StageOfPath.TryGetValue(node.Path, out var nodeStage)
        && typeStage < nodeStage;

    /// <summary>
    /// Builds the write plan for one import's node set. Within a stage the source's own enumeration
    /// order is preserved, so a source whose nodes have no dependencies at all is written in exactly
    /// the order it always was — the ordering only moves what the graph actually constrains.
    /// </summary>
    public static ImportWritePlan Plan(IReadOnlyList<MeshNode> nodes)
    {
        var empty = new ImportWritePlan(
            ImmutableList<ImmutableList<MeshNode>>.Empty,
            ImmutableList<string>.Empty,
            ImmutableDictionary.Create<string, int>(StringComparer.OrdinalIgnoreCase));
        if (nodes is null || nodes.Count == 0)
            return empty;

        // The graph is over PATHS. Duplicates of one path (a source may yield them) stay together in
        // input order so every input node is written exactly once, in a defined place.
        var groups = new Dictionary<string, List<MeshNode>>(StringComparer.OrdinalIgnoreCase);
        var appearance = new List<string>();
        foreach (var node in nodes)
        {
            var path = node.Path ?? string.Empty;
            if (!groups.TryGetValue(path, out var group))
            {
                groups[path] = group = new List<MeshNode>();
                appearance.Add(path);
            }
            group.Add(node);
        }

        var typePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in appearance)
            if (groups[path].Any(IsNodeTypeDefinition))
                typePaths.Add(path);

        var dependencies = ImmutableDictionary.CreateBuilder<string, ImmutableHashSet<string>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var path in appearance)
        {
            var deps = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);

            // Edge 1 — type before instance.
            foreach (var node in groups[path])
                if (!string.IsNullOrEmpty(node.NodeType)
                    && groups.ContainsKey(node.NodeType!)
                    && !string.Equals(node.NodeType, path, StringComparison.OrdinalIgnoreCase))
                    deps.Add(node.NodeType!);

            // Edge 2 — a type's own compile inputs before the type.
            if (typePaths.Contains(path))
                foreach (var candidate in appearance)
                    if (IsCompileInputOf(candidate, path))
                        deps.Add(candidate);

            dependencies[path] = deps.ToImmutable();
        }

        var graph = dependencies.ToImmutable();
        // Dependencies FIRST, cycles condensed and reported — the same peel the compile pre-warmer
        // orders NodeType builds with, so import order and compile order cannot disagree.
        var ordered = NodeTypeDependencyGraph.TopologicalOrder(graph, out var cyclic);

        // Stage = longest dependency chain behind a path. Walking in topological order means every
        // dependency is already staged when we reach its dependent — except a cycle's back edge,
        // whose target is deliberately still unstaged and therefore contributes nothing. A cycle is
        // thus laid out in path order across consecutive stages rather than raced within one, which
        // keeps "who was written first" deterministic even where no order can be correct.
        var stageOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in ordered)
        {
            var stage = 0;
            foreach (var dependency in graph[path])
                if (stageOf.TryGetValue(dependency, out var dependencyStage) && dependencyStage >= stage)
                    stage = dependencyStage + 1;
            stageOf[path] = stage;
        }

        var index = appearance
            .Select((path, i) => (path, i))
            .ToDictionary(x => x.path, x => x.i, StringComparer.OrdinalIgnoreCase);
        var stages = stageOf
            .GroupBy(kv => kv.Value)
            .OrderBy(g => g.Key)
            .Select(g => g
                .Select(kv => kv.Key)
                .OrderBy(path => index[path])
                .SelectMany(path => groups[path])
                .ToImmutableList())
            .ToImmutableList();

        return new ImportWritePlan(
            stages, cyclic, stageOf.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// <paramref name="candidate"/> is a compile input of the NodeType at <paramref name="typePath"/>:
    /// a node in its <c>Source/</c> or <c>Test/</c> subtree — the convention every source query
    /// resolves against. Deliberately NOT "any descendant": a typed instance nested under a
    /// leaf-shaped type is a DEPENDENT of that type, and treating it as an input would order it
    /// ahead of the very type it needs (the trap <c>PackageInstaller</c>'s bucket 0 documents).
    /// </summary>
    private static bool IsCompileInputOf(string candidate, string typePath) =>
        candidate.StartsWith(typePath + "/Source/", StringComparison.OrdinalIgnoreCase)
        || candidate.StartsWith(typePath + "/Test/", StringComparison.OrdinalIgnoreCase);
}
