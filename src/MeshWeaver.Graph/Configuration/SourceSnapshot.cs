using System.Collections.Immutable;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// A compile's point-in-time source set TOGETHER WITH whether that set was actually
/// ESTABLISHED — i.e. whether every declared source query answered.
///
/// <para>🚨 This distinction is the whole point of the type. Before it existed, a source set
/// that came back empty (or short) because a discovery query ERRORED / never answered was
/// byte-for-byte indistinguishable from a set that legitimately matched nothing: both arrived
/// as a bare <c>IEnumerable&lt;MeshNode&gt;</c>. The compiler handed the short set to Roslyn,
/// and Roslyn — correctly, given what it was shown — emitted a completely genuine-looking
/// diagnostic about the author's code:</para>
///
/// <code>
/// DynamicTypePreWarmer: BusinessRules/Scope → CompileError
///   CS0246: The type or namespace name 'ScopeLibrary' could not be found
///   CS0103: The name 'ScopeTestsArea' does not exist in the current context
/// </code>
///
/// <para>On memex-cloud (2026-08-11, ci.2947) that happened to 14 of 56 sampled types during a
/// rollout: the OUTGOING pod still held those types' hub activations, so the incoming pod's
/// source discovery round-tripped cross-silo into a busy peer and starved. The types had
/// compiled in ~1s the same day, on the same content. The bake readiness gate cannot tell a
/// <c>CompileError</c> caused by an unreadable mesh from a real image regression, so
/// <c>BakePhase.Regressed</c> stuck, readiness was refused, and the rollout stalled on healthy
/// content — with the stalled pod re-baking, which was itself the cluster load.</para>
///
/// <para><b>The rule this type enforces: a compile whose SOURCE SET could not be established is
/// not a verdict about the code.</b> It must never reach Roslyn, and it must never be recorded
/// as <see cref="Mesh.Services.CompilationStatus.Error"/> — see
/// <see cref="SourceDiscoveryUnavailableException"/> for how it is surfaced instead.</para>
///
/// <para>Three outcomes, all distinguishable:
/// <list type="bullet">
///   <item><b>Established, non-empty</b> — the normal case; compile.</item>
///   <item><b>Established, EMPTY</b> (<see cref="Empty"/>) — every query answered and matched
///     nothing. That is a CONTENT fact (the sources were deleted, or the type is
///     configuration-only), so the compile proceeds and any failure classifies as
///     <c>PreWarmStatus.NoSources</c>, which correctly does not gate a rollout.</item>
///   <item><b>Unestablished</b> (<see cref="Unavailable"/>) — at least one query errored or
///     never answered, so nothing at all is known about the source set. NOT a verdict.</item>
/// </list></para>
/// </summary>
/// <param name="Sources">
/// What discovery did find. On an unestablished snapshot this is whatever partial set was
/// collected — kept for DIAGNOSIS only; it must never be compiled, because "partial" is exactly
/// what produces a phantom <c>CS0246</c>.
/// </param>
/// <param name="UnestablishedReason">
/// <c>null</c> when the set is established. Otherwise a human-readable reason naming the query
/// (or queries) that did not answer — the thing that used to be unknowable.
/// </param>
internal readonly record struct SourceSnapshot(
    IReadOnlyList<MeshNode> Sources,
    string? UnestablishedReason)
{
    /// <summary>Every declared source query answered, so this set IS the source set.</summary>
    public bool IsEstablished => UnestablishedReason is null;

    /// <summary>The established, genuinely-empty snapshot — the consensus "there are no sources".</summary>
    public static SourceSnapshot Empty { get; } =
        new(ImmutableList<MeshNode>.Empty, null);

    /// <summary>An established snapshot over <paramref name="sources"/>.</summary>
    public static SourceSnapshot Established(IEnumerable<MeshNode> sources) =>
        new(sources as IReadOnlyList<MeshNode> ?? sources.ToList(), null);

    /// <summary>
    /// A snapshot that could NOT be established: <paramref name="reason"/> names what failed.
    /// <paramref name="partial"/> carries whatever was collected, for diagnosis only.
    /// </summary>
    public static SourceSnapshot Unavailable(string reason, IReadOnlyList<MeshNode>? partial = null) =>
        new(partial ?? ImmutableList<MeshNode>.Empty, reason);
}

/// <summary>
/// The compile refused to run because its SOURCE SET COULD NOT BE ESTABLISHED — a discovery
/// query errored, or never answered within
/// <see cref="CompilationCacheOptions.SourceSnapshotTimeout"/>.
///
/// <para>🚨 Deliberately its own type so the terminal write-back can record
/// <see cref="Mesh.Services.CompilationStatus.Unavailable"/> ("the compile state could not be
/// determined — nothing is known to be wrong with the source") instead of
/// <see cref="Mesh.Services.CompilationStatus.Error"/> ("Roslyn rejected your code"). Everything
/// downstream already reads that distinction correctly and has done since #641: the instance
/// overlay swaps to the retry-flavoured copy instead of "please correct the code",
/// <c>EnsureCompileDispatched</c> treats it as "never determined" and re-dispatches on the next
/// REQUEST (never a timer), and <c>DynamicTypePreWarmer.WarmOne</c> maps it to
/// <c>PreWarmStatus.TimedOut</c> — which <c>NodeTypeBakeGateState</c> files under "not evaluated"
/// and therefore never counts as a regression.</para>
///
/// <para>This is NOT leniency about compile errors. A real compile error on an ESTABLISHED
/// source set still lands as <c>CompilationStatus.Error</c> → <c>PreWarmStatus.CompileError</c>
/// → a gating regression. The only thing that changed is that "I could not read the sources" is
/// no longer allowed to impersonate "your code is broken".</para>
/// </summary>
public sealed class SourceDiscoveryUnavailableException(string message)
    : Exception(message);
