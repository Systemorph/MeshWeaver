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
/// <para><b>Why the store answers this authoritatively.</b> The rule these sweeps were obeying is the
/// CQRS one: never decide on a node's content from a QUERY, whose read model can be minutes old. The
/// store is not the read model — it is what the owner persists and what the next activation loads, so
/// a NodeType with no live activation has no state anywhere else. The only thing it can lag is a write
/// a LIVE owner has acknowledged and not yet flushed (one 200 ms persistence-sampler window). Both
/// sweeps already accept a far larger lag in the abstain direction — the LISTING is the read model's
/// and a type it misses is simply not judged — and both re-measure on the next import or
/// announcement, so that window cannot invent a hold or a drift that persists.</para>
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
