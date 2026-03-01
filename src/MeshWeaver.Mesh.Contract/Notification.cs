using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace MeshWeaver.Mesh;

/// <summary>Type of notification.</summary>
public enum NotificationType
{
    /// <summary>An approval is required from this user.</summary>
    ApprovalRequired,
    /// <summary>An approval was granted.</summary>
    ApprovalGiven,
    /// <summary>An approval was rejected.</summary>
    ApprovalRejected,
    /// <summary>General notification.</summary>
    General
}

/// <summary>
/// Represents a notification for a user.
/// Notifications live under User/{userId} independently — no ISatelliteContent.
/// </summary>
public record Notification
{
    /// <summary>Unique identifier for the notification.</summary>
    [Browsable(false)]
    [Key]
    public string Id { get; init; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Title of the notification.
    /// </summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// Detailed message body.
    /// </summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// Path to the related node (e.g., the approval or document).
    /// </summary>
    [Browsable(false)]
    public string? TargetNodePath { get; init; }

    /// <summary>
    /// Whether the notification has been read.
    /// </summary>
    [Browsable(false)]
    public bool IsRead { get; init; }

    /// <summary>
    /// When the notification was created.
    /// </summary>
    [Browsable(false)]
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Type of notification.
    /// </summary>
    [Browsable(false)]
    public NotificationType NotificationType { get; init; }

    /// <summary>
    /// User ObjectId of who created the notification (e.g., the requester).
    /// </summary>
    [Browsable(false)]
    public string? CreatedBy { get; init; }
}
