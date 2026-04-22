using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh;

/// <summary>
/// Internal relay message: tells the mesh hub to perform the storage-level delete of a
/// node, post the terminal DeleteNodeResponse back to the original caller, and dispose
/// the deleted address's grain.
///
/// Why this indirection exists: when a DeleteNodeRequest lands on the node's OWN hub
/// (e.g., posted directly to the node's address by a layout-area click handler or by a
/// recursive child-delete that has already torn down its siblings), the terminal
/// DisposeRequest tears that same hub down. Any reply posted AFTER the storage commit
/// then races callback disposal on the caller side and can be lost — the recursive
/// delete tests hang for exactly this reason.
///
/// By forwarding the commit step to the mesh hub — which never tears itself down — the
/// reply's Sender is the stable mesh hub, the DisposeRequest targets the node hub
/// cleanly, and the caller's RegisterCallback resolves before the node hub is gone.
/// </summary>
/// <param name="Path">Path of the node to delete from storage.</param>
/// <param name="OriginalRequestId">Id of the outer DeleteNodeRequest — used to route the
/// DeleteNodeResponse back to the caller's matching RegisterCallback.</param>
/// <param name="OriginalSender">Address of the original DeleteNodeRequest's sender —
/// used as the target of the reply.</param>
/// <param name="DeletedBy">User or system that initiated the delete, for logging.</param>
internal record CommitNodeDeletionMessage(
    string Path,
    string OriginalRequestId,
    Address OriginalSender,
    string? DeletedBy);
