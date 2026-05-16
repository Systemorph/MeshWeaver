using System.Collections.Concurrent;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting;

/// <summary>
/// Default <see cref="IMeshNodeStreamCache"/> (and the back-compat
/// <see cref="IMeshNodeStreamCache"/> alias) — a pure per-path stream cache
/// over <c>workspace.GetMeshNodeStream(path)</c>. Holds ONE shared
/// <see cref="MeshNodeStreamHandle"/> per path in a concurrent dictionary.
/// Every consumer — the routing path, every per-instance hub of a NodeType,
/// <c>NodeTypeEnrichmentHelpers</c>, compile-activity hubs writing terminal
/// state, path-resolution lookups — goes through that ONE handle. Reads
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
internal sealed class MeshNodeStreamCache : IMeshNodeStreamCache
{
    /// <summary>One cache entry: the updatable handle plus the shared,
    /// replay-cached read view over it.</summary>
    private sealed record Entry(MeshNodeStreamHandle Handle, IObservable<MeshNode> Shared);

    private readonly IMessageHub meshHub;
    private readonly ILogger<MeshNodeStreamCache> logger;
    private readonly ConcurrentDictionary<string, Entry> _streams = new();

    public MeshNodeStreamCache(IMessageHub meshHub, ILogger<MeshNodeStreamCache> logger)
    {
        this.meshHub = meshHub;
        this.logger = logger;
    }

    private Entry GetEntry(string path) =>
        _streams.GetOrAdd(path, p =>
        {
            logger.LogDebug("MeshNodeStreamCache: opening shared stream for {Path}", p);
            var handle = meshHub.GetWorkspace().GetMeshNodeStream(p);
            // Replay(1).AutoConnect(1): the upstream subscription opens once
            // on the FIRST subscriber and stays live for the entire process
            // lifetime — no RefCount tear-down when transient
            // <c>.Take(1)</c> consumers drop their subscription. RefCount was
            // racing every <c>EnrichWithNodeType</c> call: a per-instance hub
            // activates, calls <c>.Take(1)</c>, count goes 1→0 → upstream
            // tears down, then the NEXT instance arrives → RefCount opens a
            // fresh SubscribeRequest → no cached snapshot to replay → either
            // races a stale Initial or piles up 60+ SubscribeRequests on the
            // owning NodeType hub. AutoConnect(1) eliminates that churn —
            // one subscription, durable cache for every reader.
            var connectable = handle.Replay(1);
            return new Entry(handle, connectable.AutoConnect(1));
        });

    public IObservable<MeshNode> GetStream(string path) => GetEntry(path).Shared;

    public IObservable<MeshNode> Update(string path, Func<MeshNode, MeshNode> update) =>
        GetEntry(path).Handle.Update(update);
}
