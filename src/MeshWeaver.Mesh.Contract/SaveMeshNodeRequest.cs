using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh;

/// <summary>
/// The per-node hub's own PERSISTENCE STEP: write <see cref="Node"/> to the storage layer. Posted
/// by the hub to itself — the persistence sampler on every own-node change, and the deferred
/// re-post behind an unresolved post-commit flush claim — and handled on that hub's inbox so the
/// writes for one node are serialised.
///
/// <para>🚨 <b>Only a self-post is a raw write.</b> The handler writes through
/// <see cref="MeshWeaver.Mesh.Services.IStorageAdapter.Write"/> only when the delivery's sender IS
/// the receiving hub and it did not enter through a participant ingress. Any other sender gets
/// the checked write: the save is forwarded as a <see cref="CreateOrUpdateNodeRequest"/> under the
/// delivery's own access context, so Create/Update permission and every node validator decide it,
/// and a delivery with no identity is refused. It used to write whatever any sender named, and
/// every ingress (SignalR, gRPC) forwards any delivery to any address — an anonymous connection
/// could overwrite any node.</para>
///
/// <para>Marked <see cref="InfrastructureOnlyAttribute"/>: a participant connection's delivery of
/// it is refused before the handler runs, whatever sender it claims. Marked
/// <see cref="SystemMessageAttribute"/> because the self-post carries no user identity — it is the
/// hub persisting state an earlier, already-checked write produced.</para>
/// </summary>
/// <param name="Node">The node state to persist — the hub's own node, for the self-post.</param>
[InfrastructureOnly]
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
