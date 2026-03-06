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
    /// </summary>
    public static async Task CreateNotificationAsync(
        IMeshNodePersistence nodeFactory,
        string targetUserId,
        string title,
        string message,
        NotificationType type,
        string? targetNodePath,
        string? createdBy)
    {
        var notificationId = Guid.NewGuid().AsString();
        var parentPath = $"User/{targetUserId}";
        var notificationPath = $"{parentPath}/{notificationId}";

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

        await nodeFactory.CreateNodeAsync(node, createdBy);
    }
}
