using System.Text.Json;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Hot, per-path observable cache for <see cref="MeshNode"/>s. Owns ONE
/// <c>Replay(1).AutoConnect(1)</c> observable per path, subscribed to
/// <c>workspace.GetMeshNodeStream(path)</c> on the mesh hub. Every consumer —
/// the routing layer, every per-instance hub that depends on a NodeType
/// definition, <c>NodeTypeEnrichmentHelpers</c>, compile-activity hubs
/// writing terminal state, path-resolution lookups — goes through the SAME
/// handle. Reads (<see cref="GetStream"/>) and writes (<see cref="Update"/>)
/// share the underlying stream so an update is always visible to every reader.
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
    /// Returns the cached observable for the MeshNode at <paramref name="path"/>.
    /// First call subscribes to <c>workspace.GetMeshNodeStream(path)</c> and
    /// installs <c>Replay(1).AutoConnect(1)</c>; subsequent calls return the
    /// same instance — one shared upstream subscription, instant snapshot
    /// for new subscribers via the Replay buffer.
    /// </summary>
    IObservable<MeshNode> GetStream(string path);

    /// <summary>
    /// Applies <paramref name="update"/> to the MeshNode at
    /// <paramref name="path"/> through the SAME cached
    /// <c>MeshNodeStreamHandle</c> that <see cref="GetStream"/> reads. This is
    /// the canonical way for a non-owning hub (e.g. a compile-activity hub) to
    /// write terminal state: it MUST go through the one shared handle, not an
    /// ad-hoc <c>GetRemoteStream</c> — an ad-hoc stream is a separate
    /// instance, so its update is "lost" (never seen by the readers of the
    /// cached stream). Returns the post-update MeshNode; caller MUST
    /// Subscribe (the side effect runs on Subscribe).
    /// </summary>
    IObservable<MeshNode> Update(string path, System.Func<MeshNode, MeshNode> update);

    /// <summary>
    /// Caller-typed <see cref="GetStream(string)"/>: every emitted MeshNode's
    /// <c>Content</c> is round-tripped through <paramref name="options"/> so the
    /// caller sees a typed domain instance (e.g. <c>ModelProviderConfiguration</c>)
    /// rather than the raw <c>JsonElement</c> the cache hub stores. Use this when
    /// your code pattern-matches on <c>Content</c>'s runtime type.
    ///
    /// <para>The cache hub itself is domain-type-agnostic — it doesn't know about
    /// <c>ModelProviderConfiguration</c> et al. Conversion is done at the boundary
    /// here using the caller's <see cref="JsonSerializerOptions"/>, which DOES
    /// know the types (its <c>$type</c> polymorphic resolver was built from the
    /// caller hub's <c>TypeRegistry</c>). Decouples the process-singleton cache
    /// from every domain type a tenant happens to register.</para>
    /// </summary>
    IObservable<MeshNode> GetStream(string path, JsonSerializerOptions options);

    /// <summary>
    /// Caller-typed <see cref="Update(string, System.Func{MeshNode, MeshNode})"/>:
    /// the <paramref name="update"/> lambda receives a MeshNode whose <c>Content</c>
    /// has been deserialised via <paramref name="options"/>, and the returned
    /// updated MeshNode's <c>Content</c> is serialised back using the same
    /// options when computing the JSON-merge patch posted to the owning hub.
    /// </summary>
    IObservable<MeshNode> Update(
        string path,
        System.Func<MeshNode, MeshNode> update,
        JsonSerializerOptions options);

    /// <summary>
    /// Removes the cached <c>Replay(1)</c> entry for <paramref name="path"/>.
    /// The per-path stream is rebuilt on the next <see cref="GetStream"/> call.
    /// Called by <c>HandleDeleteNodeRequest</c> after the owning hub's persistence
    /// layer confirms the delete, so subsequent reads no longer see the stale
    /// pre-delete value. Idempotent — calling for an unknown path is a no-op.
    /// </summary>
    void Invalidate(string path);
}
