using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace MeshWeaver.Mesh;

/// <summary>The delivery mechanism a <see cref="NotificationChannel"/> uses.</summary>
public enum NotificationChannelKind
{
    /// <summary>The in-app notification bell (the always-on default — every user has it implicitly).</summary>
    InApp,
    /// <summary>Email via Microsoft Graph (an outbound <see cref="Email"/> node is created).</summary>
    Email,
    /// <summary>Microsoft Teams (chat/channel message).</summary>
    Teams
}

/// <summary>
/// A delivery channel a user has configured for notifications (mail, Teams, …). The notification
/// triage agent routes each notification to zero or more of these channels according to the user's
/// <see cref="NotificationRule"/>s. User-authored and owned: stored under
/// <c>{username}/_NotificationChannel/{id}</c>.
/// </summary>
public record NotificationChannel
{
    /// <summary>Unique identifier for the channel.</summary>
    [Browsable(false)]
    [Key]
    public string Id { get; init; } = Guid.NewGuid().ToString();

    /// <summary>Human-friendly name shown in settings (e.g. "Work email", "Teams DM").</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>How this channel delivers.</summary>
    public NotificationChannelKind Kind { get; init; } = NotificationChannelKind.InApp;

    /// <summary>
    /// Where to deliver — an email address for <see cref="NotificationChannelKind.Email"/>, a Teams
    /// user/conversation id for <see cref="NotificationChannelKind.Teams"/>. When null the channel
    /// targets the owning user's own default address for that kind.
    /// </summary>
    public string? Target { get; init; }

    /// <summary>Whether the channel is active. Disabled channels are skipped by triage.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>User ObjectId that owns the channel.</summary>
    [Browsable(false)]
    public string? CreatedBy { get; init; }
}
