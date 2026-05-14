namespace MeshWeaver.Mesh;

/// <summary>
/// Fire-and-forget request to persist a MeshNode to the storage layer. Handled
/// by a handler on the per-node hub that calls
/// <see cref="MeshWeaver.Mesh.Services.IStorageAdapter.Write"/>. Used by
/// MeshNodeTypeSource to schedule saves through the actor inbox instead of
/// writing to disk directly from the workspace's update pipeline.
/// </summary>
public record SaveMeshNodeRequest(MeshNode Node);

/// <summary>
/// Fire-and-forget request to delete a MeshNode from the storage layer. Handled
/// by a handler on the per-node hub that calls
/// <see cref="MeshWeaver.Mesh.Services.IStorageAdapter.Delete"/>. The
/// recursive flag forwards to the storage adapter; default is single-node
/// delete (the per-node hub only knows about its own node).
/// </summary>
public record DeleteMeshNodeRequest(string Path, bool Recursive = false);
