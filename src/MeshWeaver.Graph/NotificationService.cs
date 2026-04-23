using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.ShortGuid;

namespace MeshWeaver.Graph;

/// <summary>
/// Static helper for creating notification MeshNodes under a user's namespace.
/// </summary>
public static class NotificationService
{
    /// <summary>
    /// Creates a notification MeshNode under User/{targetUserId}/{newGuid}.
    /// Returns an IObservable that emits the created node and completes — subscribe to drive
    /// the write. Safe to compose inside hub handlers / click actions via Subscribe.
    /// </summary>
    public static IObservable<MeshNode> CreateNotification(
        IMeshService nodeFactory,
        string targetUserId,
        string title,
        string message,
        NotificationType type,
        string? targetNodePath,
        string? createdBy)
    {
        var notificationId = Guid.NewGuid().AsString();
        var parentPath = $"User/{targetUserId}";

        var notification = new Notification
        {
            Id = notificationId,
            Title = title,
            Message = message,
            TargetNodePath = targetNodePath,
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
            Content = notification
        };

        return nodeFactory.CreateNode(node);
    }
}
