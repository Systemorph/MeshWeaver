namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Hot, per-path observable cache for <see cref="MeshNode"/>s — particularly
/// NodeType MeshNodes that grains depend on at activation time. The cache owns
/// one <c>Replay(1).RefCount()</c> observable per path, subscribed to
/// <c>workspace.GetMeshNodeStream(path)</c> on the mesh hub. Multiple
/// subscribers (the routing grain, every instance grain of that NodeType) share
/// the same upstream subscription; new subscribers receive the cached latest
/// snapshot instantly via Replay(1).
///
/// <para>Pattern mirrors <c>PartitionRegistry</c> in
/// <c>src/MeshWeaver.Graph/Configuration/PartitionRegistry.cs</c>. The cache is
/// the subscription — there is no snapshot dictionary to invalidate. When the
/// NodeType MeshNode changes (a new release ships, source edit re-compiles,
/// AssemblyLocation flips), the synchronization protocol pushes a fresh
/// emission through the cached observable to every subscriber automatically.</para>
///
/// <para>Designed for the activation-blocker fix described in
/// <c>~/.claude/plans/splendid-sauteeing-garden.md</c>: the routing grain calls
/// this from <c>RouteMessage</c> to await NodeType readiness before forwarding,
/// and the per-instance <c>MessageHubGrain.OnActivateAsync</c> consumes the
/// already-warm cache via <c>Take(1)</c>.</para>
/// </summary>
public interface INodeTypeStreamCache
{
    /// <summary>
    /// Returns the cached observable for the MeshNode at <paramref name="path"/>.
    /// First call subscribes to <c>workspace.GetMeshNodeStream(path)</c> and
    /// installs <c>Replay(1).RefCount()</c>; subsequent calls return the same
    /// instance — one shared upstream subscription, instant snapshot for new
    /// subscribers.
    /// </summary>
    IObservable<MeshNode> GetStream(string path);

    /// <summary>
    /// Applies <paramref name="update"/> to the MeshNode at
    /// <paramref name="path"/> through the SAME cached
    /// <c>MeshNodeStreamHandle</c> that <see cref="GetStream"/> reads. This is
    /// the canonical way for a non-owning hub (e.g. a compile-activity hub) to
    /// write a NodeType's terminal compile state: it MUST go through the one
    /// shared handle, not an ad-hoc <c>GetRemoteStream</c> — an ad-hoc stream
    /// is a separate instance, so its update is "lost" (never seen by the
    /// readers of the cached stream). Returns the post-update MeshNode; caller
    /// MUST Subscribe (the side effect runs on Subscribe).
    /// </summary>
    IObservable<MeshNode> Update(string path, Func<MeshNode, MeshNode> update);
}
