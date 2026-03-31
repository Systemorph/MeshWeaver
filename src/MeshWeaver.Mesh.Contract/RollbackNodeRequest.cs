using MeshWeaver.Data;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using MeshWeaver.Messaging.Security;

namespace MeshWeaver.Mesh;

/// <summary>
/// Rolls back a single node to a specific historical version.
/// The node is saved with a new higher version number (content = old state).
/// </summary>
[RequiresPermission(Permission.Update)]
public record RollbackNodeRequest(string Path, long TargetVersion) : IRequest<DataChangeResponse>;
