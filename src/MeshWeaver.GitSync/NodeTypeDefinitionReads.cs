using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.GitSync;

/// <summary>
/// The CONTENT half of a GitSync sweep over a partition's NodeTypes — "what does each listed type's
/// definition say?" — read WITHOUT activating the types' per-node hubs (#6623).
///
/// <para><b>What it replaced, and what that cost.</b> Both sweeps that ask this question —
/// <see cref="BundleKeyedHoldReading"/> (every import of a synced Space) and
/// <see cref="SealedPublicationSyncReconciler"/> (every seal announcement, and the boot pass) —
/// read each type through <c>GetMeshNodeStream(path).Take(1)</c>. That stream reaches the content by
/// SUBSCRIBING at the type's own address, so every NodeType of every synced Space got a per-node hub
/// built for a single read, and kept it for its idle window. On the first replica of a generation
/// that is the 403 two-segment node hubs of the 2026-10-11 census
/// (<c>Doc/Architecture/SyncHubPopulation</c> §9–10).</para>
///
/// <para><b>What the answer is: the DURABLE snapshot, not the live record.</b> The rule these sweeps
/// were obeying is the CQRS one: never decide on a node's content from a QUERY, whose read model can
/// be minutes old. The store is not the read model — it is what the owner persists and what the next
/// activation loads, so a NodeType with no live activation has no state anywhere else. Where an owner
/// IS live, the store trails it: ordinarily by one persistence-sampler window (200 ms) plus the write
/// itself, but with NO bound after a store write that FAILED — <c>MeshDataSource</c> logs a faulted
/// sampled save and does not retry it, and its <c>PendingOwnSave</c> counts the accepted request as
/// saved, so the live record stays ahead of the row until the owner's next change is written. (That
/// is a durability defect of the owner — the next activation loads the older row and the
/// acknowledged state is lost — and it is not this read's to fix.) So neither caller may treat the
/// answer as fresh, and neither needs to:</para>
///
/// <list type="bullet">
/// <item><see cref="BundleKeyedHold.Decide"/> acts only where the definition's
/// <c>CurrentSourceFingerprint</c> is REPRODUCED by folding the partition's current Code nodes (its
/// calibration step); a row whose fingerprint trails the sources fails that comparison and the type
/// is abstained on — the same branch a stale Code listing already takes. A row that trails only in
/// <c>BuildProvenance</c> errs one of two ways: a type it misses as adopted is not held, which is the
/// documented abstain direction (the type reports <c>StaleAdopted</c>, announced); a type it still
/// shows as adopted is held, which keeps that type's sources where they are for this import — nothing
/// is written or deleted — and the hold is released by the same shelf rule as any other.</item>
/// <item><see cref="SyncedPartitionDrift.Measure"/> on a trailing fingerprint either misses drift
/// (abstain; the next announcement re-measures) or reports drift that is no longer there, whose
/// action is a re-import at the commit the source already claims — it moves nothing backwards, and
/// the re-written sources make the owner write its definition again.</item>
/// </list>
///
/// <para>Neither answer persists beyond the owner's next successful write, and both sweeps re-measure
/// on the next import or announcement.</para>
/// </summary>
internal static class NodeTypeDefinitionReads
{
    /// <summary>
    /// Path → definition for every path in <paramref name="typePaths"/> whose stored content reads
    /// as a <see cref="NodeTypeDefinition"/>, in one batched store read. A path with no row, or whose
    /// content is not a definition, is absent — the caller's "not compared / not judged" direction.
    /// A store FAULT propagates.
    /// </summary>
    /// <param name="hub">The hub whose storage adapter and static providers answer.</param>
    /// <param name="typePaths">The NodeType paths a listing named.</param>
    /// <param name="logger">Diagnostics for the content read.</param>
    /// <returns>A cold observable emitting the map once, then completing.</returns>
    internal static IObservable<ImmutableDictionary<string, NodeTypeDefinition>> ReadDefinitions(
        IMessageHub hub, IReadOnlyCollection<string> typePaths, ILogger? logger)
        => hub.ReadStoredNodes(typePaths)
            .Select(nodes => nodes
                .Select(kv => (Path: kv.Key,
                    Definition: kv.Value.ContentAs<NodeTypeDefinition>(hub.JsonSerializerOptions, logger)))
                .Where(p => p.Definition is not null)
                .ToImmutableDictionary(p => p.Path, p => p.Definition!, StringComparer.Ordinal));
}
