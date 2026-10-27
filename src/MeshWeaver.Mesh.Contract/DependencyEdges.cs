using System.Collections.Immutable;

namespace MeshWeaver.Mesh;

/// <summary>
/// The kinds of forward dependency edge Roslyn records (<c>Doc/Architecture/DependencyGraph</c>).
/// An OPEN vocabulary (policy <c>open-vocabulary-string-constants</c>): another producer — the
/// module workspace build — may record its own kinds in the same field, and a reader that meets
/// an unknown kind keeps it as unknown rather than mapping it onto one of these.
/// </summary>
public static class DependencyEdgeKinds
{
    /// <summary>The source binds a type whose declaration another NodeType owns (its code was
    /// compiled in through a <c>shared=</c> source query or an <c>@@</c> include).</summary>
    public const string NodeType = "NodeType";

    /// <summary>The source's compilation USED an installed module assembly
    /// (<c>Compilation.GetUsedAssemblyReferences()</c>).</summary>
    public const string Module = "Module";
}

/// <summary>
/// One forward dependency edge: the source (the <see cref="DependencyEdgeSet"/> it belongs to)
/// depends on <see cref="Target"/>.
/// </summary>
public sealed record DependencyEdge
{
    /// <summary>What the source depends on — a NodeType path for <see cref="DependencyEdgeKinds.NodeType"/>,
    /// an assembly simple name for <see cref="DependencyEdgeKinds.Module"/>.</summary>
    public required string Target { get; init; }

    /// <summary>The edge's kind — see <see cref="DependencyEdgeKinds"/>.</summary>
    public required string Kind { get; init; }

    /// <summary>The build identity of the TARGET the source compiled against, where one exists — a
    /// module assembly's MVID ("N" format). Null for a NodeType edge: the target's code was
    /// compiled into the source's own assembly, so there is no separate target build.</summary>
    public string? TargetBuildMvid { get; init; }

    /// <summary>A few of the referenced symbols that made the edge (fully qualified type names,
    /// ordinal order, capped) — WHY the source depends on the target. Empty for a module edge.</summary>
    public ImmutableList<string> Evidence { get; init; } = [];
}

/// <summary>
/// The complete forward edge set of ONE compiled source, read by Roslyn from one compilation and
/// stored as one node (<see cref="DependencyEdgeSetPaths.PathFor"/>), replaced whole by the next
/// compile. See <c>Doc/Architecture/DependencyGraph</c>.
///
/// <para>The set carries the identity of the build it was read from: a reader comparing
/// <see cref="BuildMvid"/> with the source's current build tells a current edge set from a stale
/// one. An EMPTY <see cref="Edges"/> list means "extracted, and this build depends on nothing";
/// absence of the set means "never extracted".</para>
/// </summary>
public sealed record DependencyEdgeSet
{
    /// <summary>The compiled source — the NodeType path.</summary>
    public required string Source { get; init; }

    /// <summary>The MVID ("N" format) of the emitted assembly the edges were read from — the
    /// same value <c>NodeTypeDefinition.LatestAssemblyMvid</c> carries for that build.</summary>
    public string? BuildMvid { get; init; }

    /// <summary>
    /// The version of the source's NodeType record that this build's compile stamp LANDED at — the
    /// per-source GENERATION of the set. Record versions are minted monotonically per path, so a
    /// later stamp always carries a higher one, whichever writer (the activation's settle or the
    /// batch bake) made it. The writer never replaces a stored set with a set of a LOWER
    /// generation: an older compile whose stamp landed first and whose edge write arrives last
    /// cannot overwrite the newer build's edges. Null when the producer stamped nothing (a
    /// build-process bake carried on its bundle).
    /// </summary>
    public long? StampVersion { get; init; }

    /// <summary>The generated-input digest of the compile that produced the build (the content
    /// key's first stage), or null when the producer did not compute one.</summary>
    public string? InputDigest { get; init; }

    /// <summary>The source-control commit the compiled sources came from, when the producer knows
    /// it (a build-process bake); null for a runtime compile of the mesh's own sources.</summary>
    public string? Commit { get; init; }

    /// <summary>The forward edges, ordered by kind then target (ordinal).</summary>
    public ImmutableList<DependencyEdge> Edges { get; init; } = [];

    /// <summary>NodeTypes whose code was compiled into this build but never referenced from the
    /// source's own code — an inclusion, not an edge (ordinal order).</summary>
    public ImmutableList<string> IncludedWithoutUse { get; init; } = [];
}

/// <summary>Where a <see cref="DependencyEdgeSet"/> lives, and the node type it is stored under.</summary>
public static class DependencyEdgeSetPaths
{
    /// <summary>The node type of a stored <see cref="DependencyEdgeSet"/>.</summary>
    public const string NodeType = "DependencyEdgeSet";

    /// <summary>The path segment under the source that holds its edge set.</summary>
    public const string Segment = "_Dependency";

    /// <summary>The id of the one forward edge-set node under <see cref="Segment"/>.</summary>
    public const string ForwardId = "Forward";

    /// <summary>The path of <paramref name="source"/>'s forward edge set:
    /// <c>{source}/_Dependency/Forward</c>.</summary>
    /// <param name="source">The compiled source's path (a NodeType path).</param>
    public static string PathFor(string source) => $"{source}/{Segment}/{ForwardId}";

    /// <summary>The namespace the edge-set node is created in: <c>{source}/_Dependency</c>.</summary>
    /// <param name="source">The compiled source's path.</param>
    public static string NamespaceFor(string source) => $"{source}/{Segment}";
}
