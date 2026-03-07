using MeshWeaver.Data;
using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh;

/// <summary>
/// Undoes all changes made by a specific activity.
/// For each AffectedPath, restores the entity to its state at ActivityLog.StartVersion.
/// Creates new versions (does NOT revert the version counter).
/// </summary>
public record UndoActivityRequest(string ActivityLogId) : IRequest<DataChangeResponse>;
