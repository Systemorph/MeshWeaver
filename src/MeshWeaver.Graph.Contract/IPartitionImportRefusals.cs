using System.Collections.Immutable;

namespace MeshWeaver.Graph;

/// <summary>
/// 🚨 <b>Issue #4469 — one source node an import could NOT write, named at the place the operator
/// actually lands: the compile error.</b>
///
/// <para>A node the import refused leaves the partition <b>referenced-but-incomplete</b>. The files
/// that reference the missing symbol DID land, so every NodeType built from them fails on
/// <c>CS0246</c> / <c>CS0103</c> for a symbol whose file is plainly in git — and nothing in that
/// message, and nothing on the NodeType, connects it to the import that produced the state.
/// Measured on <c>memex.systemorph.com</c>, 2026-09-15: a literal NUL byte in
/// <c>Hosting/Deployment/Source/SelfUpdateRouting.cs</c> (unstorable in a Postgres text column) cost
/// one row, five Hosting NodeTypes parked on <c>CS0246 SelfUpdateRouting</c> —
/// <c>Hosting/InstanceAction</c> among them, so no instance action ran on the control instance at
/// all — and the evening went into re-deriving a fact the system already held.</para>
///
/// <para><b>Why a seam and not a direct read.</b> The refusal is remembered by the IMPORTER, in the
/// partition's per-node import manifest (<see cref="ImportRefusal"/>, #4467) — and the importer
/// lives in <c>MeshWeaver.Graph</c>, which references the compile pipeline, not the other way
/// round. This is the same one-question contract <see cref="IPartitionSourceTracking"/> is, for the
/// same reason: the compiler layer gets the answer without a reference to the layer above it, and
/// the manifest's on-disk shape stays private to the code that writes it.</para>
///
/// <para>🚨 <b>Three answers, never two.</b> <see cref="Refusals"/> emits <see langword="null"/>
/// when the question could NOT be answered (the ledger could not be read, the budget elapsed, the
/// reader may not see it) and an EMPTY list when it was answered and the partition records no
/// refusal. Collapsing those would let "I could not look" render as "an import lost nothing", which
/// is the reading that made #4459 an outage in the first place — and, the other way round, it is
/// what stops the compile diagnosis from ever ASSERTING an import refusal it has not established.
/// A symbol can equally be missing because it was legitimately deleted, or because the module is
/// not loaded on this replica (a declined bundle — MeshWeaver#3583, a different defect entirely);
/// saying "an import dropped this" when it did not is worse than today's silence.</para>
/// </summary>
public interface IPartitionImportRefusals
{
    /// <summary>
    /// The nodes <paramref name="partition"/>'s import bookkeeping records as REFUSED — declared by
    /// the source, evaluated, and not in the mesh. ONE emission, bounded; never faults.
    /// </summary>
    /// <param name="partition">The top-level path segment (<c>Hosting</c>, never <c>Hosting/…</c>).</param>
    /// <returns><see langword="null"/> when the question could not be answered; otherwise the
    /// recorded refusals (possibly empty).</returns>
    IObservable<ImmutableList<ImportRefusal>?> Refusals(string partition);
}

/// <summary>
/// One node an import evaluated and could not write, as the partition's bookkeeping remembers it.
/// </summary>
/// <param name="NodePath">The mesh path the source declares and the mesh does not hold.</param>
/// <param name="Reason">The write path's own verdict on those bytes — a validator rule, a byte the
/// store will not take, an RLS denial; those have three different fixes, which is why "refused"
/// alone was never enough (#3101's argument, for nodes). <see langword="null"/> when the refusal was
/// recorded by a build that did not yet keep reasons — present, but not diagnosed.</param>
public sealed record ImportRefusal(string NodePath, string? Reason);
