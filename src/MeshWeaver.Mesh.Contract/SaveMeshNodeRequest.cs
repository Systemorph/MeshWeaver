using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh;

/// <summary>
/// Fire-and-forget request to persist a MeshNode to the storage layer. Handled
/// by a handler on the per-node hub that calls
/// <see cref="MeshWeaver.Mesh.Services.IStorageAdapter.Write"/>. Used by
/// MeshNodeTypeSource to schedule saves through the actor inbox instead of
/// writing to disk directly from the workspace's update pipeline.
///
/// <para>Marked <see cref="SystemMessageAttribute"/> — this is hub-internal
/// persistence infrastructure (a per-node hub posting to itself to flush its
/// own data to storage). Not an end-user write; no AccessControl needed.</para>
/// </summary>
[SystemMessage]
public record SaveMeshNodeRequest(MeshNode Node);

/// <summary>
/// LEGACY request to delete a MeshNode — do not post it in new code; use <see cref="MeshWeaver.Mesh.Services.IMeshService.DeleteNode"/> (or a
/// <see cref="DeleteNodeRequest"/>) — the one delete surface.
///
/// <para>🚨 The per-node hub's handler no longer deletes from storage: it forwards the request to
/// the validated delete under the delivery's own access context, so the caller needs Delete on
/// the node and every delete validator runs (with the recursive pre-flight when
/// <paramref name="Recursive"/> is set). A delivery that carries no access context is refused.
/// It was a raw <c>IStorageAdapter.Delete</c> of any path, reachable from every ingress that
/// forwards deliveries (SignalR, gRPC, in-mesh code), with no check at all. Not a
/// <c>[SystemMessage]</c> any more: it is not hub-internal infrastructure, and a post without an
/// identity must be failed by the post pipeline rather than waved through.</para>
///
/// <para>Kept, as a forwarder, only because in-mesh source compiled at runtime may name it;
/// nothing in the platform posts it. Not marked <c>[Obsolete]</c>: the forwarder, not the
/// attribute, is what closes the hole, and the attribute would need warning suppressions at the
/// handler that must keep referencing it.</para>
/// </summary>
/// <param name="Path">The node to delete.</param>
/// <param name="Recursive">Also delete descendants — forwarded as <see cref="DeleteNodeRequest.Recursive"/>.</param>
public record DeleteMeshNodeRequest(string Path, bool Recursive = false);
