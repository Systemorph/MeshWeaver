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
/// Represents a notification about a main entity (a thread, an approval, a job).
/// Notifications are <b>satellites</b> of their main entity: the notification node
/// has <c>MainNode = mainEntityPath</c> and its own path lives under
/// <c>{mainEntityPath}/_Notification/{id}</c>. Storage routes through the
/// <c>notifications</c> table via <see cref="SatelliteTableMapping"/>.
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
    /// Optional icon path or URL for the notification (e.g.,
    /// <c>/static/NodeTypeIcons/chat.svg</c>). When unset, the
    /// <see cref="NotificationType"/> drives the default icon.
    /// </summary>
    [Browsable(false)]
    public string? Icon { get; init; }

    /// <summary>
    /// Path to the related node (e.g., the approval or document). The bell
    /// list navigates here on click.
    /// </summary>
    [Browsable(false)]
    public string? TargetNodePath { get; init; }

    /// <summary>
    /// Whether the notification has been read. The bell flips this on click.
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
