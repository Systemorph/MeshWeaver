using System.Collections.Concurrent;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting;

/// <summary>
/// Default <see cref="INodeTypeStreamCache"/> — a pure per-path stream cache.
/// Holds ONE shared <see cref="MeshNodeStreamHandle"/> per path in a concurrent
/// dictionary. Every consumer — the routing path, every per-instance hub of
/// that NodeType, <c>NodeTypeEnrichmentHelpers</c>, and compile-activity hubs
/// writing terminal state — goes through that ONE handle. Reads
/// (<see cref="GetStream"/>) and writes (<see cref="Update"/>) share the same
/// underlying stream, so an update is always visible to every reader.
///
/// <para>The handle is opened on the mesh hub's workspace. That is safe:
/// <c>GetMeshNodeStream</c> for a non-own path returns an
/// <c>ISynchronizationStream</c> which runs on its OWN hub/scheduler, not the
/// caller's — the requesting workspace's hub only dispatches the initial
/// <c>SubscribeRequest</c>.</para>
///
/// <para><b>Never go around the cache.</b> An ad-hoc
/// <c>workspace.GetRemoteStream(...)</c> from some other hub is a SEPARATE
/// stream instance; updating it is "lost" — never seen by the readers of the
/// cached stream (this was the bug behind compile state never landing on a
/// NodeType's MeshNode). Non-owning hubs MUST use <see cref="Update"/>.</para>
///
/// <para><b>No side-effects on emission.</b> The cache does not kick
/// compilation — opening the stream activates the per-NodeType hub via the
/// <c>SubscribeRequest</c>, and that hub's OWN compile watcher kickoff
/// (<c>NodeTypeCompilationHelpers.InstallCompileWatcher</c>) flips
/// <c>CompilationStatus = Pending</c> on its OWN stream and runs Roslyn.</para>
/// </summary>
internal sealed class NodeTypeStreamCache : INodeTypeStreamCache
{
    /// <summary>One cache entry: the updatable handle plus the shared,
    /// replay-cached read view over it.</summary>
    private sealed record Entry(MeshNodeStreamHandle Handle, IObservable<MeshNode> Shared);

    private readonly IMessageHub meshHub;
    private readonly ILogger<NodeTypeStreamCache> logger;
    private readonly ConcurrentDictionary<string, Entry> _streams = new();

    public NodeTypeStreamCache(IMessageHub meshHub, ILogger<NodeTypeStreamCache> logger)
    {
        this.meshHub = meshHub;
        this.logger = logger;
    }

    private Entry GetEntry(string path) =>
        _streams.GetOrAdd(path, p =>
        {
            logger.LogDebug("NodeTypeStreamCache: opening shared stream for {Path}", p);
            var handle = meshHub.GetWorkspace().GetMeshNodeStream(p);
            // Replay(1).RefCount() so new readers get the current snapshot
            // instantly and all readers share one upstream subscription.
            return new Entry(handle, handle.Replay(1).RefCount());
        });

    public IObservable<MeshNode> GetStream(string path) => GetEntry(path).Shared;

    public IObservable<MeshNode> Update(string path, Func<MeshNode, MeshNode> update) =>
        GetEntry(path).Handle.Update(update);
}
