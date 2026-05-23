using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.ShortGuid;

namespace MeshWeaver.Graph;

/// <summary>
/// Static helper for creating notification MeshNodes as <b>satellites</b> of the
/// main entity they're about (thread, approval, doc, …). The notification's
/// <see cref="MeshNode.MainNode"/> is the entity's path; its own path is
/// <c>{mainNodePath}/_Notification/{id}</c>. Storage routes through the
/// dedicated <c>notifications</c> table via
/// <see cref="PartitionDefinition.StandardTableMappings"/>.
/// </summary>
public static class NotificationService
{
    /// <summary>Path segment that marks a node as a Notification satellite.</summary>
    public const string SatelliteSegment = "_Notification";

    /// <summary>
    /// Creates a notification as a satellite of <paramref name="mainNodePath"/>.
    /// Path = <c>{mainNodePath}/_Notification/{newId}</c>; MainNode = mainNodePath.
    /// Returns an IObservable that emits the created node and completes —
    /// subscribe to drive the write. Safe to compose inside hub handlers /
    /// click actions via Subscribe.
    /// </summary>
    public static IObservable<MeshNode> CreateNotification(
        IMeshService nodeFactory,
        string mainNodePath,
        string title,
        string message,
        NotificationType type,
        string? targetNodePath = null,
        string? createdBy = null,
        string? icon = null)
    {
        var notificationId = Guid.NewGuid().AsString();
        var parentPath = $"{mainNodePath}/{SatelliteSegment}";

        var notification = new Notification
        {
            Id = notificationId,
            Title = title,
            Message = message,
            Icon = icon,
            TargetNodePath = targetNodePath ?? mainNodePath,
            IsRead = false,
            CreatedAt = DateTimeOffset.UtcNow,
            NotificationType = type,
            CreatedBy = createdBy
        };

        var node = new MeshNode(notificationId, parentPath)
        {
            Name = title,
            NodeType = NotificationNodeType.NodeType,
            State = MeshNodeState.Active,
            MainNode = mainNodePath,
            Content = notification
        };

        return nodeFactory.CreateNode(node);
    }
}
