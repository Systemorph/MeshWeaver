using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting;

/// <summary>
/// Default <see cref="IMeshNodeStreamCache"/> — a per-path stream cache over
/// <c>workspace.GetMeshNodeStream(path)</c>. Holds ONE shared
/// <see cref="MeshNodeStreamHandle"/> per path in a concurrent dictionary.
/// Every consumer — the routing path, every per-instance hub of a NodeType,
/// <c>NodeTypeEnrichmentHelpers</c>, compile-activity hubs writing terminal
/// state, path-resolution lookups — goes through that ONE handle. Reads
/// (<see cref="GetStream"/>) and writes (<see cref="Update"/>) share the same
/// underlying stream, so an update is always visible to every reader.
///
/// <para>The handle is opened on the mesh hub's workspace under
/// <see cref="AccessService.ImpersonateAsSystem"/> — that's correct for the
/// system-internal infrastructure subscription. Per-user enforcement lives
/// at the <see cref="GetStream"/> seam: the cache asks the owning node hub
/// via <see cref="GetPermissionRequest"/> for the current user's effective
/// permissions on the path; only when the response carries
/// <see cref="Permission.Read"/> does the gated observable forward the
/// upstream emissions. Per-(path,user) validations are cached for
/// <see cref="AccessTtl"/> to avoid hammering the owning hub.</para>
///
/// <para><b>Never go around the cache.</b> An ad-hoc
/// <c>workspace.GetRemoteStream(...)</c> from some other hub is a SEPARATE
/// stream instance; updating it is "lost" — never seen by the readers of the
/// cached stream (this was the bug behind compile state never landing on a
/// NodeType's MeshNode). Non-owning hubs MUST use <see cref="Update"/>.</para>
///
/// <para><b>No side-effects on emission.</b> The cache does not kick
/// compilation — opening the stream activates the per-NodeType hub via the
/// <c>SubscribeRequest</c>, and that hub's OWN compile watcher
/// (<c>NodeTypeCompilationHelpers.InstallCompileWatcher</c>) flips
/// <c>CompilationStatus = Pending</c> on its OWN stream only on explicit
/// user-driven <c>RequestedReleaseAt</c> flips.</para>
/// </summary>
internal sealed class MeshNodeStreamCache : IMeshNodeStreamCache
{
    /// <summary>One cache entry: the updatable handle plus the shared,
    /// replay-cached read view over it. The Shared observable is the raw
    /// system-side stream; per-user access gating is applied in
    /// <see cref="GetStream"/> before each subscriber consumes it.</summary>
    private sealed record Entry(MeshNodeStreamHandle Handle, IObservable<MeshNode> Shared);

    /// <summary>
    /// Validity window for a cached <c>(path,user) → Permission</c> probe. A
    /// hit within this window short-circuits the <see cref="GetPermissionRequest"/>
    /// round-trip; a miss issues the request and caches its response. Trade-off:
    /// permission revocations propagate after at most <c>AccessTtl</c> — short
    /// enough for interactive UX, long enough that <c>GetStream</c> hot paths
    /// don't hammer the owning hub. The value matches the canonical
    /// AccessControl cache TTL used elsewhere in the codebase (30s).
    /// </summary>
    private static readonly TimeSpan AccessTtl = TimeSpan.FromSeconds(30);

    private readonly IMessageHub meshHub;
    private readonly ILogger<MeshNodeStreamCache> logger;
    private readonly ConcurrentDictionary<string, Entry> _streams = new();
    private readonly ConcurrentDictionary<(string Path, string UserId), AccessEntry> _access = new();

    /// <summary>Cached effective-permission probe with expiry.</summary>
    private sealed record AccessEntry(Permission Permissions, DateTimeOffset ValidUntil);

    public MeshNodeStreamCache(IMessageHub meshHub, ILogger<MeshNodeStreamCache> logger)
    {
        this.meshHub = meshHub;
        this.logger = logger;
    }

    private Entry GetEntry(string path) =>
        _streams.GetOrAdd(path, p =>
        {
            logger.LogDebug("MeshNodeStreamCache: opening shared stream for {Path}", p);
            // 🚨 Bypass the cache when opening our OWN upstream — otherwise
            // GetMeshNodeStream(workspace, path) auto-redirects back into the
            // cache and we'd recurse forever waiting for ourselves.
            var handle = meshHub.GetWorkspace().GetMeshNodeStreamBypassCache(p);
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

    /// <summary>
    /// Returns a per-user access-gated view of the cached shared stream. The
    /// gate is enforced by asking the owning node hub for the current user's
    /// effective permissions via <see cref="GetPermissionRequest"/>; the
    /// response is cached for <see cref="AccessTtl"/> per <c>(path,user)</c>.
    /// On <see cref="Permission.Read"/> ⇒ the upstream observable is returned
    /// directly; on missing Read ⇒ the observable terminates with
    /// <see cref="UnauthorizedAccessException"/>.
    ///
    /// <para>Authoritative source: the node's OWN hub (not the cache, not the
    /// caller's hub). The hub already runs the validator chain when it
    /// answers <see cref="GetPermissionRequest"/>; consulting it keeps the
    /// gate aligned with every other access decision in the system.</para>
    /// </summary>
    public IObservable<MeshNode> GetStream(string path)
    {
        var shared = GetEntry(path).Shared;

        var accessService = meshHub.ServiceProvider.GetService<AccessService>();
        if (accessService is null)
            return shared; // No AccessService (minimal test fixture) — pass-through.

        // RLS not installed on this mesh ⇒ no gate. GetPermissionRequest handler
        // is wired up by AddRowLevelSecurity → AddAccessControlPipeline on every
        // per-node hub; without RLS the message has no handler and the feature
        // makes no sense.
        if (meshHub.ServiceProvider.GetService<ISecurityService>() is null)
            return shared;

        // Capture the caller's identity synchronously, on the caller's thread,
        // before the cold pipeline runs. The CarryAccessContext wrap re-stamps
        // AsyncLocal on each emission so downstream Subscribe callbacks see
        // the same identity regardless of where the emission lands.
        var captured = accessService.Context ?? accessService.CircuitContext;
        if (captured is null || string.IsNullOrEmpty(captured.ObjectId))
            return shared; // No user identity (background / system path) — pass-through.

        // 🚨 Prod-2026-05-21 regression guard: posting GetPermissionRequest to
        // an Address whose first segment is a NodeType name (e.g. "Thread",
        // "AccessAssignment") causes PostgreSqlPathRoutingAdapter to lower-case
        // it into a schema name and `EnsureSchemaForPartitionSync` blows up
        // with `relation "thread.mesh_nodes" does not exist`. The cache gate
        // only makes sense for paths that ARE real partition-rooted node
        // paths. If the first segment is empty or matches a known NodeType
        // name, skip the gate entirely.
        if (LooksLikeNodeTypePath(path))
            return shared;

        var key = (Path: path, UserId: captured.ObjectId);
        return Observable.Defer(() =>
        {
            // TTL cache hit ⇒ short-circuit the round-trip.
            if (_access.TryGetValue(key, out var cached)
                && cached.ValidUntil > DateTimeOffset.UtcNow)
            {
                return GateOnRead(cached.Permissions, shared, path, captured);
            }

            // Miss ⇒ ask the owning hub for the user's effective permissions on
            // this path, then gate. The GetPermissionRequest handler is wired
            // on every per-node hub by AddAccessControlPipeline (called from
            // AddRowLevelSecurity). Without RLS the gate doesn't fire at all
            // — see the no-AccessService bail-out at the top of GetStream.
            return meshHub.Observe(
                    new GetPermissionRequest(),
                    o => o.WithTarget(new Address(path)).WithAccessContext(captured))
                .Select(d => (d.Message as GetPermissionResponse)?.Permissions ?? Permission.None)
                .Take(1)
                .SelectMany(perms =>
                {
                    _access[key] = new AccessEntry(perms,
                        DateTimeOffset.UtcNow + AccessTtl);
                    return GateOnRead(perms, shared, path, captured);
                });
        }).CarryAccessContext(accessService);
    }

    private static IObservable<MeshNode> GateOnRead(
        Permission perms, IObservable<MeshNode> shared, string path, AccessContext user)
    {
        if (perms.HasFlag(Permission.Read))
            return shared;
        return Observable.Throw<MeshNode>(new UnauthorizedAccessException(
            $"User '{user.ObjectId}' lacks Read permission on '{path}'"));
    }

    public IObservable<MeshNode> Update(string path, Func<MeshNode, MeshNode> update) =>
        // The underlying MeshNodeStreamHandle.Update already wraps with
        // CarryAccessContext, so writes through the cache automatically carry
        // the caller's user identity into the partition write. No additional
        // wrap needed here. See AccessContextPropagation.md.
        GetEntry(path).Handle.Update(update);

    /// <summary>
    /// True when <paramref name="path"/> is empty or its first segment matches
    /// a known NodeType name (from <see cref="PartitionDefinition.NodeTypeToSuffix"/>).
    /// Used by <see cref="GetStream"/> to skip the access-check round-trip on
    /// non-partition-rooted paths, which previously triggered the prod
    /// 2026-05-21 regression where <see cref="PostgreSqlPathRoutingAdapter"/>
    /// lower-cased the segment as a schema name and blew up with
    /// <c>relation "thread.mesh_nodes" does not exist</c>.
    /// </summary>
    private static bool LooksLikeNodeTypePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return true;
        var slashIdx = path.IndexOf('/');
        var firstSegment = slashIdx < 0 ? path : path[..slashIdx];
        if (string.IsNullOrEmpty(firstSegment)) return true;
        // NodeTypeToSuffix is the canonical registry of "this is a NodeType
        // name, not a partition name". If the first segment is in here, the
        // path was never going to resolve as a partition path.
        return PartitionDefinition.NodeTypeToSuffix.ContainsKey(firstSegment);
    }
}
