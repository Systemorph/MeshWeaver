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
/// Fire-and-forget request to delete a MeshNode from the storage layer. Handled
/// by a handler on the per-node hub that calls
/// <see cref="MeshWeaver.Mesh.Services.IStorageAdapter.Delete"/>. The
/// recursive flag forwards to the storage adapter; default is single-node
/// delete (the per-node hub only knows about its own node).
///
/// <para>Marked <see cref="SystemMessageAttribute"/> — hub-internal cleanup,
/// no end-user write semantics.</para>
/// </summary>
[SystemMessage]
public record DeleteMeshNodeRequest(string Path, bool Recursive = false);
