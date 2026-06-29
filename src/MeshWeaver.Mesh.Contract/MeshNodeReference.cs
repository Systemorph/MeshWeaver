using MeshWeaver.Data;

namespace MeshWeaver.Mesh;

/// <summary>
/// Workspace reference for typed access to a single <see cref="MeshNode"/> stream.
/// <list type="bullet">
///   <item><c>Path == null</c> — addresses the hub's own <see cref="MeshNode"/>
///     (the canonical case used by every per-node hub via
///     <c>MeshDataSource</c>'s reducer).</item>
///   <item><c>Path != null</c> — addresses the <see cref="MeshNode"/> at
///     <c>Path</c> within a synced collection (e.g. resolved by
///     <c>SyncedQueryMeshNodes</c>'s reducer to the cached per-path remote
///     stream). Updates on the resulting stream propagate through the
///     synchronization protocol to the owning per-node hub.</item>
/// </list>
/// </summary>
public record MeshNodeReference(string? Path = null) : WorkspaceReference<MeshNode>;
