using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Caching.Memory;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Singleton, silo-wide cache of shared NodeType MeshNode streams. Wraps
/// <see cref="MeshNodeStreamExtensions.GetMeshNodeStream(IWorkspace, string)"/> in a
/// <c>Replay(1).RefCount()</c> so that every consumer of a given NodeType path
/// shares one upstream subscription. Backed by <see cref="IMemoryCache"/> with a
/// 1-hour sliding expiration — after the last consumer unsubscribes, the cached
/// observable lingers for one idle hour, then evicts (next call rebuilds and
/// re-subscribes upstream).
///
/// <para>Replaces the per-consumer pattern
/// <c>workspace.GetMeshNodeStream(new Address(nodeTypePath))</c> with a shared
/// stream: subscriber count is bounded by "active NodeTypes in the last hour"
/// instead of "number of consumer instances times number of call sites".</para>
///
/// <para>Stage 2 of NodeTypeService deletion. See
/// <c>Doc/Architecture/SyncedMeshNodeQueries.md</c> +
/// <c>feedback_dirty_flag_on_owner</c>.</para>
/// </summary>
public sealed class NodeTypeStreamCache : IDisposable
{
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());

    private static readonly TimeSpan IdleTtl = TimeSpan.FromHours(1);

    /// <summary>
    /// Returns the shared <c>Replay(1).RefCount()</c> stream of the NodeType MeshNode
    /// at <paramref name="nodeTypePath"/>. Filters out null emissions (pre-existence /
    /// post-delete frames). First subscribe pays a <c>SubscribeRequest</c> round-trip;
    /// every subsequent caller within the idle TTL piggybacks on the same upstream.
    /// </summary>
    public IObservable<MeshNode> Get(IWorkspace workspace, string nodeTypePath) =>
        _cache.GetOrCreate(nodeTypePath, entry =>
        {
            entry.SlidingExpiration = IdleTtl;
            return workspace.GetMeshNodeStream(nodeTypePath)
                .Where(n => n is not null)
                .Select(n => n!)
                .Replay(1)
                .RefCount();
        })!;

    /// <summary>
    /// Convenience overload — resolves the workspace from <paramref name="hub"/> via
    /// <see cref="MessageHubExtensions.GetWorkspace"/>. Use this from hubs that already
    /// have an <see cref="IMessageHub"/> handy.
    /// </summary>
    public IObservable<MeshNode> Get(IMessageHub hub, string nodeTypePath) =>
        Get(hub.GetWorkspace(), nodeTypePath);

    /// <summary>
    /// Evicts the cached stream for <paramref name="nodeTypePath"/>. Existing subscribers
    /// keep receiving emissions through the held <c>Replay/RefCount</c>; the next
    /// <see cref="Get"/> call rebuilds a fresh upstream subscription. Rarely needed —
    /// the upstream stream is already cross-silo via the workspace's
    /// <see cref="MeshNodeReference"/> reducer.
    /// </summary>
    public void Invalidate(string nodeTypePath) => _cache.Remove(nodeTypePath);

    /// <inheritdoc/>
    public void Dispose() => (_cache as IDisposable)?.Dispose();
}
