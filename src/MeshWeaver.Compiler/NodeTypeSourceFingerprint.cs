using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Security.Cryptography;
using System.Text;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeshWeaver.Compiler;

/// <summary>
/// 🚨 <b>The ONE definition of "which source did these bytes come from"</b> (#2813) — computed
/// identically by the PRODUCER that bakes a prebuilt assembly and by the CONSUMER that is asked to
/// adopt it, so the two values are comparable and a mismatch is proof of staleness rather than of a
/// shape difference.
///
/// <para>The producer writes it into the bundle manifest per assembly entry
/// (<c>BundleWriter.AssemblyEntry.SourceFingerprint</c>); <c>PrebuiltAssemblySeeder.Seed</c> stamps
/// it on the node as <c>NodeTypeDefinition.AdoptedSourceFingerprint</c>; the owning hub computes
/// this same function over ITS live source set into
/// <c>NodeTypeDefinition.CurrentSourceFingerprint</c>; and
/// <c>NodeTypeCompilationHelpers.ApplyAdoptedSourceStamp</c> compares the two. Equal ⇒
/// <c>AdoptedVerified</c>. Different ⇒ <c>AdoptionRefused</c>: the bytes are last week's code over
/// today's data, which is what destroyed four client documents.</para>
///
/// <para><b>Why it hashes the COMPILE INPUT and not the source MeshNodes' serialised content.</b>
/// The obvious shape — <c>PartitionSourceFingerprint.Compute(nodes, versioned: false,
/// hub.JsonSerializerOptions)</c>, which is what the live half computed before this existed — is
/// unusable as a CROSS-PROCESS comparison, in three ways that all produce FALSE REFUSALS (an
/// outage strictly worse than the bug):</para>
/// <list type="bullet">
///   <item><b>Run bookkeeping churns it.</b> <see cref="CodeConfiguration"/> carries
///     <c>LastExecutedAt</c> / <c>LastExecutedBy</c> / <c>LastExecutedCodeHash</c> /
///     <c>LastActivityPath</c>, written when a reader presses Run on a code cell. Hashing the
///     serialised content would move the live fingerprint with no source change at all, and no
///     producer can ever know those values.</item>
///   <item><b>The two sides serialise differently by design.</b> The consumer has a hub and
///     therefore a TypeRegistry (polymorphic <c>$type</c> discriminators); the compiler-driven bake
///     deliberately has neither — <c>TreeNodeLoader</c> materialises exactly the two content types
///     a compile reads and leaves everything else null, precisely so a half-populated registry
///     cannot silently degrade content to <c>JsonElement</c>. Two honest readers, two different
///     JSON strings.</item>
///   <item><b>Node metadata is not compile input.</b> A description, an icon, an order — none of
///     them change a byte of the emitted assembly, and letting them decide whether an assembly is
///     stale answers a question nobody asked.</item>
/// </list>
///
/// <para>So the fingerprint is taken over exactly what Roslyn is handed: the
/// <see cref="NodeCompileShaping.CollectCompileSources"/> fold — deduplicated (ordinal-ignore-case),
/// executable cells and blank files dropped — reduced to <c>(node path, SHA-256 of the code
/// text)</c>. That fold is the SAME call both the mesh compile path
/// (<c>MeshNodeCompilationService</c>) and the tree bake (<see cref="NodeSetCompiler"/>) already
/// make, so producer and consumer cannot fork on which files count.</para>
///
/// <para><b>Why it lives in this assembly.</b> <see cref="FrameworkBuildIdentity.FullMvidAssemblies"/>
/// is the transitive closure of <c>MeshWeaver.Compiler</c>, so any change to this function moves the
/// framework identity — and adoption is already gated on that identity matching. A producer and a
/// consumer that could adopt across each other therefore necessarily run the SAME implementation of
/// this hash. Put it anywhere else and two meshes could disagree about the shape while agreeing
/// about the gate, which is a false-refusal outage waiting on a refactor.</para>
///
/// <para>🚨 <b>And it covers the <c>@@</c> INCLUDE CLOSURE</b> (#2948). An include pulls a Code node
/// that NO source query matches, so it is absent from the fold above while being present in the
/// emitted bytes. Until #2948 that made this hash silently partial: editing an included-only
/// snippet moved neither fingerprint, and a prebuilt assembly baked before that edit still adopted
/// as <c>AdoptedVerified</c> — a VERIFIED claim over source nobody had hashed, which is strictly
/// worse than the <c>AdoptedUnverified</c> it looks better than. Both halves now resolve includes
/// before hashing: the bake already did (<see cref="NodeSetCompiler.ResolveInputs"/> hands back the
/// closure its own substitution collected), and the live side resolves it through the mesh in the
/// sources watcher. Every included node contributes a <c>@@{path}</c> entry carrying the SHA-256 of
/// its own text — see <see cref="NodeCompileShaping.CollectIncludeClosure"/> for why that walk is
/// order-stable and cycle-safe.</para>
///
/// <para>🚨 <b>Residue, named rather than assumed.</b> The include set is NOT added to
/// <c>NodeTypeDefinition.CompiledSources</c> / <c>CurrentSourceVersions</c>, so
/// <c>NodeTypeDefinition.IsDirty</c> still does not notice an included-only edit, and the live
/// sources watcher — which re-runs on its source QUERY — does not re-publish until the type's own
/// source set moves or its hub reactivates. That is the recompile-trigger half, deliberately left
/// where it was: it is a different mechanism (a change feed, not a hash), it is the same gap
/// <c>CompiledSources</c> has always had, and the bundle manifest's <c>sourceVersions</c> is a
/// producer/consumer contract that both bakes and <c>BakeEquivalenceTest</c> pin to the RAW query
/// match. What #2948 closes is the VERIFIED claim, which is decided by these two fingerprints and
/// nothing else.</para>
/// </summary>
public static class NodeTypeSourceFingerprint
{
    /// <summary>
    /// The key prefix under which an <c>@@</c>-include contributes to the fold. It keeps the
    /// include closure in a namespace of its own, so a node that is BOTH a matched source and the
    /// target of an include from a sibling contributes one entry as each, and neither can be
    /// mistaken for the other by a reader of the framed input.
    /// </summary>
    private const string IncludeKeyPrefix = "@@";

    /// <summary>
    /// The fingerprint of <paramref name="sourceNodes"/> as a compile input for the NodeType at
    /// <paramref name="nodeTypePath"/>, resolving the <c>@@</c>-include closure through
    /// <paramref name="readInclude"/> first — the shape every LIVE caller uses, because only a mesh
    /// read can say what an included-only snippet says today.
    ///
    /// <para>🚨 Faults are NOT swallowed. If an include read cannot be completed (a stall, an
    /// unavailable owner) this observable errors, and the caller must treat that as INCONCLUSIVE —
    /// leave the previous value standing — never as "the include is absent". Degrading a failed
    /// read to absence shortens the closure, which reads exactly like a stale bundle and refuses a
    /// perfectly good adoption: a self-inflicted outage worse than the bug. Same rule as the emit
    /// canary (#890): a probe must not answer its scariest branch on its own inability to run.</para>
    /// </summary>
    /// <param name="sourceNodes">The RAW resolved source+test node set — the output of
    /// <c>NodeSources.GetSources</c> at runtime, or <c>NodeSet.ResolveSources</c> in the bake. The
    /// shaping fold is applied here so both callers cannot apply a different one.</param>
    /// <param name="nodeTypePath">The NodeType's mesh path. Also the include ANCHOR, matching what
    /// the emit path uses for root files.</param>
    /// <param name="readInclude">Reads one include target (anchored path, authored fallback).</param>
    /// <param name="logger">Diagnostics for the shaping fold (skipped executable cells).</param>
    public static IObservable<string> Compute(
        IEnumerable<MeshNode> sourceNodes,
        string nodeTypePath,
        Func<string, string?, IObservable<(MeshNode? Node, string Path)>> readInclude,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(sourceNodes);
        ArgumentException.ThrowIfNullOrEmpty(nodeTypePath);
        ArgumentNullException.ThrowIfNull(readInclude);

        var log = logger ?? NullLogger.Instance;
        var (sources, paths) = NodeCompileShaping.CollectCompileSources(
            sourceNodes, nodeTypePath, log);

        return NodeCompileShaping
            .CollectIncludeClosure(sources, nodeTypePath, readInclude, log)
            .Select(closure => Fold(sources, paths, closure));
    }

    /// <summary>
    /// The fingerprint for a caller that has ALREADY resolved the include closure — the tree bake,
    /// whose compile substituted the includes anyway and hands the collected set back on
    /// <see cref="NodeSetCompiler.CompileInputs.ResolvedIncludes"/>. No second walk, no second set
    /// of reads, and no <c>.Wait()</c> at a synchronous build-step boundary.
    ///
    /// <para>NEVER null: an empty source set folds to the empty-set hash, a real value. That is
    /// deliberate — "this type compiles from nothing" is a fact worth comparing, and returning null
    /// would make a bundle baked from three files adopt as merely <i>unverified</i> against a mesh
    /// whose sources have all been deleted, instead of being refused.</para>
    ///
    /// <para>🚨 Pass the closure the compile ACTUALLY resolved. Passing an empty dictionary for a
    /// type that has includes does not produce "a fingerprint without includes" — it produces a
    /// value that DISAGREES with every honest consumer, i.e. a refusal.</para>
    /// </summary>
    /// <param name="sourceNodes">The RAW resolved source+test node set.</param>
    /// <param name="nodeTypePath">The NodeType's mesh path (diagnostics only).</param>
    /// <param name="resolvedIncludes">The <c>@@</c>-include closure: resolved path → code text.</param>
    /// <param name="logger">Diagnostics for the shaping fold (skipped executable cells).</param>
    public static string Compute(
        IEnumerable<MeshNode> sourceNodes,
        string nodeTypePath,
        IReadOnlyDictionary<string, string> resolvedIncludes,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(sourceNodes);
        ArgumentException.ThrowIfNullOrEmpty(nodeTypePath);
        ArgumentNullException.ThrowIfNull(resolvedIncludes);

        var (sources, paths) = NodeCompileShaping.CollectCompileSources(
            sourceNodes, nodeTypePath, logger ?? NullLogger.Instance);
        return Fold(sources, paths, resolvedIncludes);
    }

    /// <summary>
    /// The fingerprint of a source set that has NO <c>@@</c> includes at all — the shape a caller
    /// uses when it holds the source TEXT and no way to read the mesh (a producer-side unit check,
    /// a fixture).
    ///
    /// <para>🚨 <b>It REFUSES a set that does have one</b>, rather than quietly hashing without it.
    /// That is the whole difference between this overload and a trap-door: an empty closure over a
    /// set with includes is not "the fingerprint minus the includes", it is a value that disagrees
    /// with every honest producer and consumer — i.e. an adoption refusal, and on a
    /// <c>Modules:RequirePrebuilt</c> mesh a terminal one. A convenience overload that can silently
    /// under-cover is exactly how the #2813 mechanism sat inert: the 7-argument
    /// <c>PrebuiltAssemblySeeder.Seed</c> hard-coded <c>sourceFingerprint: null</c> and every
    /// production caller took it. So this one cannot be taken by accident.</para>
    /// </summary>
    /// <param name="sourceNodes">The RAW resolved source+test node set.</param>
    /// <param name="nodeTypePath">The NodeType's mesh path (diagnostics only).</param>
    /// <param name="logger">Diagnostics for the shaping fold (skipped executable cells).</param>
    /// <exception cref="InvalidOperationException">The shaped set contains an <c>@@</c> include
    /// directive, so its closure cannot be known without reading it.</exception>
    public static string Compute(
        IEnumerable<MeshNode> sourceNodes,
        string nodeTypePath,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(sourceNodes);
        ArgumentException.ThrowIfNullOrEmpty(nodeTypePath);

        var (sources, paths) = NodeCompileShaping.CollectCompileSources(
            sourceNodes, nodeTypePath, logger ?? NullLogger.Instance);

        for (var i = 0; i < sources.Count; i++)
        {
            if (sources[i].Code is { Length: > 0 } code
                && NodeCompileShaping.CodeIncludePattern.IsMatch(code))
                throw new InvalidOperationException(
                    $"'{paths[i]}' declares an @@ include, so the source fingerprint for "
                    + $"'{nodeTypePath}' cannot be computed from the matched sources alone (#2948). "
                    + "An include pulls a Code node NO source query matches: it is inside the "
                    + "emitted bytes, so it must be inside the hash, or an edit to it leaves a "
                    + "prebuilt assembly adopting as AdoptedVerified against source that changed "
                    + "under it. Use the overload taking an include READER (live/mesh callers) or "
                    + "the one taking the closure the compile already resolved "
                    + "(NodeSetCompiler.CompileInputs.ResolvedIncludes).");
        }

        return Fold(sources, paths, ImmutableDictionary<string, string>.Empty);
    }

    /// <summary>
    /// The fold itself. <c>CollectCompileSources</c> returns the two lists in lock-step (one
    /// MatchedPath per <c>CodeConfiguration</c> it accepted), and
    /// <c>PartitionSourceFingerprint.Compute</c> sorts by path before hashing — so neither
    /// the enumeration order of the source set nor the traversal order of the include walk can
    /// reach the result.
    /// </summary>
    private static string Fold(
        IReadOnlyList<CodeConfiguration> sources,
        IReadOnlyList<string> paths,
        IReadOnlyDictionary<string, string> resolvedIncludes)
        => PartitionSourceFingerprint.Compute(
            paths
                .Select((path, index) => (Path: path, Token: TokenOf(sources[index].Code)))
                .Concat(resolvedIncludes.Select(
                    entry => (Path: IncludeKeyPrefix + entry.Key, Token: TokenOf(entry.Value)))));

    /// <summary>SHA-256 over the code text — the only property of a source node the emitted bytes
    /// depend on (<see cref="NodeCompileShaping.CombineSources"/> reads <c>Code</c> and nothing
    /// else).</summary>
    private static string TokenOf(string? code) =>
        Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(code ?? string.Empty)));
}
