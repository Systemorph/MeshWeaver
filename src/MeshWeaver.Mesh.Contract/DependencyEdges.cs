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

    /// <summary>The source's compilation BOUND an installed module assembly — read off the emitted
    /// image's <c>AssemblyRef</c> table, like <see cref="Platform"/>.</summary>
    public const string Module = "Module";

    /// <summary>The source's compilation USED a MeshWeaver PLATFORM assembly — one of the host's
    /// own <c>MeshWeaver.*</c> assemblies that is neither an installed module nor a package — read
    /// off the emitted image's <c>AssemblyRef</c> table. The target is the assembly's simple name.
    /// This is the edge a core carve-out reads: which in-mesh code binds <c>MeshWeaver.Graph</c>.
    /// Base-class-library and other third-party host assemblies are NOT recorded (see
    /// <c>Doc/Architecture/DependencyGraph</c> → <i>Why the BCL is not an edge</i>).</summary>
    public const string Platform = "Platform";

    /// <summary>The source's compilation USED an assembly a <c>#r "nuget:…"</c> directive (or, for
    /// a module workspace build, a package from the module-libraries shelf) supplied. The target is
    /// the package id, read off the resolved file's package folder.</summary>
    public const string Package = "Package";

    /// <summary>A reflection call in the source's own code names its target with a CONSTANT
    /// string (<c>Type.GetType("Ns.T, Asm")</c>, <c>Assembly.Load("Asm")</c>, a type-registry
    /// lookup by name, …). The target is always an assembly-qualified pattern
    /// <c>{type}, {assembly}</c> — <c>*</c> for a whole-assembly load, <c>?</c> for a part the
    /// compile could not resolve — so it can never collide with a path or an assembly name.
    /// <see cref="DependencyEdge.TargetKind"/> says what the resolved assembly is.</summary>
    public const string Dynamic = "Dynamic";

    /// <summary>A reflection call in the source's own code whose argument is NOT a constant: what
    /// it binds is unknown at compile time. Recorded honestly as unknown — never dropped. The
    /// target is <c>?{api}</c> (for example <c>?System.Type.GetType</c>), one edge per API, with
    /// the call sites as evidence.</summary>
    public const string DynamicUnresolved = "DynamicUnresolved";

    /// <summary>Only ever a <see cref="DependencyEdge.TargetKind"/>, never an edge's
    /// <see cref="DependencyEdge.Kind"/>: a dynamic binding into the base class library or another
    /// non-MeshWeaver assembly the host supplies.</summary>
    public const string Framework = "Framework";
}

/// <summary>
/// The identity of a <see cref="DependencyEdge.Target"/> across KINDS. A set's targets are unique,
/// and readers key on them (the Postgres projection's primary key is <c>(source, target)</c>), so a
/// target must never mean two things: a package id and an assembly name can be the same string
/// (<c>Acme.Bundle</c> the package shipping <c>Acme.Widget.dll</c>, beside a module ASSEMBLY named
/// <c>Acme.Bundle</c>). Package targets therefore live in their own namespace,
/// <see cref="PackagePrefix"/>; a NodeType target is a path (contains <c>/</c>), a module or platform
/// target an assembly simple name (one runtime identity per name), a dynamic target
/// <c>{type}, {assembly}</c> or <c>?{api}</c>.
/// </summary>
public static class DependencyEdgeTargets
{
    /// <summary>The namespace of a <see cref="DependencyEdgeKinds.Package"/> target.</summary>
    public const string PackagePrefix = "package:";

    /// <summary>The target of a package edge.</summary>
    /// <param name="packageId">The package id.</param>
    public static string ForPackage(string packageId) => PackagePrefix + packageId;

    /// <summary>The package id a package target names (the target itself when it carries no prefix —
    /// a set written before the namespace existed).</summary>
    /// <param name="target">A package edge's target.</param>
    public static string PackageIdOf(string target) =>
        target.StartsWith(PackagePrefix, StringComparison.Ordinal) ? target[PackagePrefix.Length..] : target;
}

/// <summary>
/// The three EVIDENCE CLASSES a dependency is known by (<c>Doc/Architecture/DependencyGraph</c>).
/// An open vocabulary, like <see cref="DependencyEdgeKinds"/>.
/// </summary>
public static class DependencyEvidenceClasses
{
    /// <summary>Roslyn BOUND it: a symbol reference, or a used assembly reference.</summary>
    public const string Static = "Static";

    /// <summary>A reflection call names it by string at run time — resolved from a constant
    /// argument, or unresolved.</summary>
    public const string Dynamic = "Dynamic";

    /// <summary>A declaration states it (a csproj reference, a package manifest's <c>requires</c>
    /// or <c>module</c>, a <c>#r "nuget:"</c> directive) — what the reconciliation report compares
    /// the static and dynamic edges against.</summary>
    public const string Declared = "Declared";
}

/// <summary>
/// One forward dependency edge: the source (the <see cref="DependencyEdgeSet"/> it belongs to)
/// depends on <see cref="Target"/>.
/// </summary>
public sealed record DependencyEdge
{
    /// <summary>What the source depends on — a NodeType path for <see cref="DependencyEdgeKinds.NodeType"/>,
    /// an assembly simple name for <see cref="DependencyEdgeKinds.Module"/> and
    /// <see cref="DependencyEdgeKinds.Platform"/>, a package id for <see cref="DependencyEdgeKinds.Package"/>,
    /// an assembly-qualified pattern for <see cref="DependencyEdgeKinds.Dynamic"/> and <c>?{api}</c>
    /// for <see cref="DependencyEdgeKinds.DynamicUnresolved"/>. Unique within one set.</summary>
    public required string Target { get; init; }

    /// <summary>The edge's kind — see <see cref="DependencyEdgeKinds"/>.</summary>
    public required string Kind { get; init; }

    /// <summary>The build identity of the TARGET the source compiled against, where one exists — a
    /// module assembly's MVID ("N" format). Null for a NodeType edge: the target's code was
    /// compiled into the source's own assembly, so there is no separate target build.</summary>
    public string? TargetBuildMvid { get; init; }

    /// <summary>A few of the referenced symbols that made the edge (fully qualified type names,
    /// ordinal order, capped) — WHY the source depends on the target. Empty for a module or
    /// platform edge; the package's assembly names for a package edge; the call sites
    /// (<c>{api}("…") in {type}</c>) for a dynamic edge.</summary>
    public ImmutableList<string> Evidence { get; init; } = [];

    /// <summary>How the edge is known — see <see cref="DependencyEvidenceClasses"/>.
    /// <see cref="DependencyEvidenceClasses.Static"/> for a bound reference, which is also what a
    /// set written before the field existed reads as.</summary>
    public string EvidenceClass { get; init; } = DependencyEvidenceClasses.Static;

    /// <summary>For a dynamic edge: what the resolved target IS — a
    /// <see cref="DependencyEdgeKinds"/> value (<see cref="DependencyEdgeKinds.Platform"/>,
    /// <see cref="DependencyEdgeKinds.Module"/>, <see cref="DependencyEdgeKinds.Package"/>,
    /// <see cref="DependencyEdgeKinds.NodeType"/> for a type in the source's own compilation,
    /// <see cref="DependencyEdgeKinds.Framework"/>), or null when the compile could not tell —
    /// which a reader must treat as "may be the platform". Null for a static edge, whose
    /// <see cref="Kind"/> already says it.</summary>
    public string? TargetKind { get; init; }
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
