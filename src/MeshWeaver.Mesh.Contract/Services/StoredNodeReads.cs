using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Content reads of KNOWN paths that do NOT activate the nodes' per-node hubs: the storage adapter
/// first, the served static/config node second — the order <c>MeshExtensions.ReadNodeAuthoritative</c>
/// has always used for the partition bootstrap.
///
/// <para><b>Why this exists (#6623).</b> <c>GetMeshNodeStream(path)</c> is the authoritative LIVE
/// read, and it gets there by opening a <c>SubscribeRequest</c> to the node's own address — which
/// ACTIVATES that node's per-node hub (its scope, type registry, workspace and three or four
/// <c>sync/</c> hubs, about 4–5 MiB each on a production replica). A sweep that only needs each
/// node's CONTENT once — "what does every NodeType in this partition say?" — therefore built one hub
/// per node it looked at, and the first replica of a generation carried those activations for the
/// rest of its idle window. The store holds exactly what the next activation of that node would load,
/// so for a one-shot read it IS the node's state, and reading it builds nothing.</para>
///
/// <para><b>What it is NOT.</b> It is not a live subscription (nothing re-emits), and it does not see
/// a write a LIVE owner has applied and acknowledged but not yet flushed: the owner's persistence
/// sampler writes on a 200 ms <c>Sample</c>, and a disposing owner flushes its last save first
/// (<c>PendingOwnSave</c>). Use it where a sweep reads many nodes once and its decision tolerates
/// that window — every caller says so where it calls it. A caller that must observe a node change,
/// or that writes, uses <c>GetMeshNodeStream(path)</c>, and should.</para>
///
/// <para>A read FAULT propagates; it is never folded into "absent".</para>
/// </summary>
public static class StoredNodeReads
{
    /// <summary>
    /// The node stored (or served as static/config) at <paramref name="path"/>, or <c>null</c> —
    /// read without activating its per-node hub. A definition-only static entry is a type
    /// definition, not a node, and is never returned.
    /// </summary>
    /// <param name="hub">The hub whose storage adapter and static providers answer.</param>
    /// <param name="path">The exact node path.</param>
    /// <returns>A cold observable emitting the node or <c>null</c> once, then completing.</returns>
    public static IObservable<MeshNode?> ReadStoredNode(this IMessageHub hub, string path)
    {
        ArgumentNullException.ThrowIfNull(hub);
        return Observable.Defer(() =>
        {
            var persistence = hub.ServiceProvider.GetService<IStorageAdapter>();
            return persistence is null
                ? Observable.Return(hub.ServiceProvider.FindServedStaticNode(path))
                : persistence.Read(path, hub.JsonSerializerOptions)
                    .Take(1)
                    .DefaultIfEmpty(null)
                    // The static fallback is resolved LAZILY — FindServedStaticNode enumerates every
                    // registered provider's node list, and storage answers for all but config nodes.
                    .Select(node => node ?? hub.ServiceProvider.FindServedStaticNode(path));
        });
    }

    /// <summary>
    /// The nodes stored (or served as static/config) at <paramref name="paths"/>, keyed by path
    /// (case-insensitive), read in ONE batched storage round-trip where the backend supports it
    /// (<see cref="IStorageAdapter.ReadMany"/>) and without activating any of their per-node hubs.
    /// A path with neither a stored row nor a served static node is simply absent from the result.
    /// </summary>
    /// <param name="hub">The hub whose storage adapter and static providers answer.</param>
    /// <param name="paths">The exact node paths.</param>
    /// <returns>A cold observable emitting the path → node map once, then completing.</returns>
    public static IObservable<ImmutableDictionary<string, MeshNode>> ReadStoredNodes(
        this IMessageHub hub, IReadOnlyCollection<string> paths)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(paths);
        return Observable.Defer(() =>
        {
            var wanted = paths
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray();
            if (wanted.IsEmpty)
                return Observable.Return(ImmutableDictionary<string, MeshNode>.Empty
                    .WithComparers(StringComparer.OrdinalIgnoreCase));
            var persistence = hub.ServiceProvider.GetService<IStorageAdapter>();
            var stored = persistence is null
                ? Observable.Empty<MeshNode>()
                : persistence.ReadMany(wanted, hub.JsonSerializerOptions);
            return stored
                .Where(n => !string.IsNullOrEmpty(n.Path))
                .ToList()
                .Select(rows =>
                {
                    var byPath = ImmutableDictionary.CreateBuilder<string, MeshNode>(StringComparer.OrdinalIgnoreCase);
                    foreach (var row in rows)
                        byPath[row.Path] = row;
                    // Only the paths the store did not answer pay the static enumeration.
                    foreach (var path in wanted.Where(p => !byPath.ContainsKey(p)))
                        if (hub.ServiceProvider.FindServedStaticNode(path) is { } served)
                            byPath[path] = served;
                    return byPath.ToImmutable();
                });
        });
    }
}
