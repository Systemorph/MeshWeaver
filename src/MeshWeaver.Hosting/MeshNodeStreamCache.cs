using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
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
            // Replay(1) + eager .Connect() inside ImpersonateAsSystem: the
            // upstream SubscribeRequest opens ONCE under the well-known
            // System identity. The cache is process-wide infrastructure
            // serving every reader (routing, NodeType activation, path-
            // resolution, etc.) — none of them know which user triggered
            // the read, and the cache emission fans out to subscribers
            // who each apply their own AccessContext at consumption time
            // (this read is system-internal, not user-attributable).
            //
            // ImpersonateAsHub(meshHub) was insufficient: it stamps the
            // mesh hub's own address (mesh/{guid}) as the principal, but
            // no AccessAssignment grants that principal access to
            // partition nodes — owners' RLS denies with
            //   "user 'mesh/{guid}' lacks Read permission on '{path}'"
            // (OrleansThreadAccessTest reproduces this exactly).
            // ImpersonateAsSystem grants Permission.All unconditionally
            // (SecurityService whitelists WellKnownUsers.System), which
            // matches the infrastructure nature of the cache read.
            //
            // Eager Connect() (vs AutoConnect(1)) keeps the upstream alive
            // for process lifetime — no RefCount churn, identity captured
            // deterministically at cache-creation rather than at first
            // random consumer.
            var connectable = handle.Replay(1);
            var accessService = meshHub.ServiceProvider.GetService<AccessService>();
            if (accessService is not null)
            {
                using (accessService.ImpersonateAsSystem())
                    connectable.Connect();
            }
            else
            {
                connectable.Connect();
            }
            return new Entry(handle, connectable);
        });

    public IObservable<MeshNode> GetStream(string path) => GetEntry(path).Shared;

    public IObservable<MeshNode> Update(string path, Func<MeshNode, MeshNode> update) =>
        GetEntry(path).Handle.Update(update);
}
