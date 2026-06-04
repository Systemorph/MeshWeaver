using System.Text.Json;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Hot, per-path observable cache for <see cref="MeshNode"/>s. Owns ONE
/// <c>Replay(1).AutoConnect(1)</c> observable per path, subscribed to
/// <c>workspace.GetMeshNodeStream(path)</c> on the mesh hub. Every consumer —
/// the routing layer, every per-instance hub that depends on a NodeType
/// definition, <c>NodeTypeEnrichmentHelpers</c>, compile-activity hubs
/// writing terminal state, path-resolution lookups — goes through the SAME
/// handle. Reads (<see cref="GetStream(string, JsonSerializerOptions)"/>) and
/// writes (<see cref="Update(string, Func{MeshNode, MeshNode}, JsonSerializerOptions)"/>)
/// share the underlying stream so an update is always visible to every reader.
/// (See the <c>GetStream</c> / <c>Update</c> overloads below.)
///
/// <para><b>Not NodeType-specific.</b> The cache is path-keyed and works for
/// any MeshNode: NodeType definitions, user partitions, activity nodes,
/// release nodes. The original name (<c>INodeTypeStreamCache</c>) reflected
/// the first consumer; the API was always generic.</para>
///
/// <para><b>Pattern</b>: per-path <c>Replay(1).AutoConnect(1)</c> opens the
/// upstream subscription on the first reader and stays live for the process
/// lifetime — no RefCount tear-down on transient <c>.Take(1)</c> drops, no
/// re-subscribe storms when a per-instance hub joins. Mirrors
/// <c>PartitionRegistry</c> in <c>src/MeshWeaver.Graph/Configuration/</c>.</para>
///
/// <para><b>Activation-blocker fix</b>: routing calls this from
/// <c>RouteMessage</c> to await NodeType readiness before forwarding; the
/// per-instance <c>MessageHubGrain.OnActivateAsync</c> consumes the
/// already-warm cache via <c>Take(1)</c>; the path-resolution layer can also
/// observe the same stream for live "node at path P" lookups without
/// issuing fresh storage-adapter queries.</para>
/// </summary>
public interface IMeshNodeStreamCache
{
    /// <summary>
    /// 🚨 Reads always go through the TYPED overload
    /// <see cref="GetStream(string, JsonSerializerOptions)"/> — the bare
    /// untyped overload was DELETED. The cache hub is domain-type-agnostic and
    /// stores Content as a raw <see cref="System.Text.Json.JsonElement"/>; a bare
    /// read fed <c>node.Content as MyType</c> returns <c>null</c> and the consumer
    /// silently no-ops (the wedged-thread / never-dispatching-watcher bug class).
    /// Callers MUST pass their hub's <c>JsonSerializerOptions</c> so Content is
    /// deserialized to its registered domain type.
    /// </summary>
    /// <remarks>
    /// Every emitted MeshNode's <c>Content</c> is round-tripped through
    /// <paramref name="options"/> so the caller sees a typed domain instance
    /// (e.g. <c>ModelProviderConfiguration</c>, <c>MeshThread</c>) rather than the
    /// raw <c>JsonElement</c> the cache hub stores. Conversion uses the caller's
    /// <see cref="JsonSerializerOptions"/>, whose <c>$type</c> polymorphic resolver
    /// was built from the caller hub's <c>TypeRegistry</c> — decoupling the
    /// process-singleton cache from every domain type a tenant registers.
    /// </remarks>
    IObservable<MeshNode> GetStream(string path, JsonSerializerOptions options);

    /// <summary>
    /// Caller-typed write (the ONLY write overload): the
    /// <paramref name="update"/> lambda receives a MeshNode whose <c>Content</c>
    /// has been deserialised via <paramref name="options"/>, and the returned
    /// updated MeshNode's <c>Content</c> is serialised back using the same
    /// options when computing the JSON-merge patch posted to the owning hub.
    /// 🚨 The untyped <c>Update(path, fn)</c> overload was DELETED — without
    /// deserialization an <c>update</c> reading <c>curr.Content as MyType</c> sees
    /// null and returns the node unchanged (the write silently no-ops).
    /// </summary>
    IObservable<MeshNode> Update(
        string path,
        System.Func<MeshNode, MeshNode> update,
        JsonSerializerOptions options);

    /// <summary>
    /// Removes the cached <c>Replay(1)</c> entry for <paramref name="path"/>.
    /// The per-path stream is rebuilt on the next
    /// <see cref="GetStream(string, JsonSerializerOptions)"/> call.
    /// Called by <c>HandleDeleteNodeRequest</c> after the owning hub's persistence
    /// layer confirms the delete, so subsequent reads no longer see the stale
    /// pre-delete value. Idempotent — calling for an unknown path is a no-op.
    /// </summary>
    void Invalidate(string path);

    /// <summary>
    /// Process-wide synced-query cache: returns the cached
    /// <c>IObservable&lt;IEnumerable&lt;MeshNode&gt;&gt;</c> for the named query,
    /// creating it on first call. Replaces the legacy per-workspace registry
    /// (<c>ConditionalWeakTable&lt;IWorkspace, SyncedQueryRegistry&gt;</c>) so
    /// every consumer sees one shared subscription regardless of which hub
    /// they originate from.
    ///
    /// <para>The upstream synced query subscribes via the cache hub's
    /// workspace (under <c>MeshNodeCacheIdentity</c>), so the subscription
    /// runs under a system-flagged identity and no per-hub AsyncLocal
    /// AccessContext can leak into the query layer. Per-user RLS filtering
    /// happens at <em>subscribe</em> time on the caller side — the wrap
    /// captures the subscriber's <c>AccessService.Context</c> and applies
    /// <c>HasPermission</c> per emission.</para>
    ///
    /// <para>Application code SHOULD prefer <c>hub.GetQuery(id, queries)</c>
    /// or <c>workspace.GetQuery(id, queries)</c> — both delegate here.</para>
    /// </summary>
    IObservable<IEnumerable<MeshNode>> GetQuery(object id, params string[] queries);

    /// <summary>
    /// Lookup-only overload: returns the cached observable for
    /// <paramref name="id"/>, or <c>null</c> if no synced query has been
    /// registered with that id (no get-or-create).
    /// </summary>
    IObservable<IEnumerable<MeshNode>>? GetQuery(object id);

    /// <summary>
    /// Typed-content overload: each emitted <see cref="MeshNode"/>'s
    /// <c>Content</c> is round-tripped through <paramref name="options"/>
    /// at the cache boundary so the caller sees typed domain instances
    /// (e.g. <c>AccessAssignment</c>, <c>PartitionAccessPolicy</c>)
    /// rather than raw <see cref="System.Text.Json.JsonElement"/>. Decouples
    /// the process-singleton cache (domain-type-agnostic by design) from
    /// every domain type a tenant happens to register — same mechanism as
    /// <see cref="GetStream(string, JsonSerializerOptions)"/>.
    /// </summary>
    IObservable<IEnumerable<MeshNode>> GetQuery(object id, JsonSerializerOptions options, params string[] queries);
}
