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
/// <para>🚨 <b>Known residue, deliberately not covered.</b> An <c>@@</c> include pulls a Code node
/// that no source query matches, so it is absent from this fold — a change to an included-only
/// snippet does not move the fingerprint. That is not a new hole: it is the SAME set
/// <c>NodeTypeDefinition.CompiledSources</c> records, so <c>IsDirty</c> has always missed it too.
/// Closing it means resolving includes on the live side (a mesh read per include) and is tracked
/// separately rather than smuggled in here — but it is named, because a guard whose coverage is
/// assumed rather than stated is how this mechanism went inert the first time.</para>
/// </summary>
public static class NodeTypeSourceFingerprint
{
    /// <summary>
    /// The fingerprint of <paramref name="sourceNodes"/> as a compile input for the NodeType at
    /// <paramref name="nodeTypePath"/>.
    ///
    /// <para>NEVER null: an empty source set folds to the empty-set hash, a real value. That is
    /// deliberate — "this type compiles from nothing" is a fact worth comparing, and returning null
    /// would make a bundle baked from three files adopt as merely <i>unverified</i> against a mesh
    /// whose sources have all been deleted, instead of being refused.</para>
    /// </summary>
    /// <param name="sourceNodes">The RAW resolved source+test node set — the output of
    /// <c>NodeSources.GetSources</c> at runtime, or <c>NodeSet.ResolveSources</c> in the bake. The
    /// shaping fold is applied here so both callers cannot apply a different one.</param>
    /// <param name="nodeTypePath">The NodeType's mesh path (diagnostics only).</param>
    /// <param name="logger">Diagnostics for the shaping fold (skipped executable cells).</param>
    public static string Compute(
        IEnumerable<MeshNode> sourceNodes,
        string nodeTypePath,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(sourceNodes);
        ArgumentException.ThrowIfNullOrEmpty(nodeTypePath);

        var (sources, paths) = NodeCompileShaping.CollectCompileSources(
            sourceNodes, nodeTypePath, logger ?? NullLogger.Instance);

        // CollectCompileSources returns the two lists in lock-step (one MatchedPath per
        // CodeConfiguration it accepted), and PartitionSourceFingerprint sorts by path before
        // hashing — so the enumeration order the fold happened to use cannot reach the result.
        return PartitionSourceFingerprint.Compute(
            paths.Select((path, index) => (path, TokenOf(sources[index]))));
    }

    /// <summary>SHA-256 over the code text — the only property of a source node the emitted bytes
    /// depend on (<see cref="NodeCompileShaping.CombineSources"/> reads <c>Code</c> and nothing
    /// else).</summary>
    private static string TokenOf(CodeConfiguration code) =>
        Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(code.Code ?? string.Empty)));
}
