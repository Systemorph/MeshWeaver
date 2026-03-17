using MeshWeaver.Data;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh;

/// <summary>
/// Undoes all changes made by a specific activity.
/// For each AffectedPath, restores the entity to its state at ActivityLog.StartVersion.
/// Creates new versions (does NOT revert the version counter).
/// </summary>
[RequiresPermission(Permission.Update)]
public record UndoActivityRequest(string ActivityLogId) : IRequest<DataChangeResponse>;
