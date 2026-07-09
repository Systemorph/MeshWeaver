using System.ComponentModel;
using System.Text.Json.Serialization;

namespace MeshWeaver.Mesh;

/// <summary>
/// User-facing grouping of <see cref="NotificationType"/>s for delivery preferences. Several
/// notification types collapse into one configurable row (all three approval types → Approvals).
/// </summary>
public enum NotificationCategory
{
    /// <summary>Approval requested / given / rejected.</summary>
    Approvals,
    /// <summary>You were granted access (a role) on a node.</summary>
    AccessGranted,
    /// <summary>An AI chat thread finished a round.</summary>
    ChatReady,
    /// <summary>Platform/system events (failures, admin alerts).</summary>
    System
}

/// <summary>
/// Per-user notification delivery preferences — for each <see cref="NotificationCategory"/>,
/// whether it is delivered in-app (the bell) and/or by email. Stored as a MeshNode
/// (<c>NotificationSettings</c>) under the user at
/// <see cref="NotificationSettingsPaths.PathFor"/>; the Notifications settings tab data-binds to it
/// and <see cref="NotificationCategoryExtensions"/> maps a <see cref="NotificationType"/> to its row.
///
/// <para><b>Serialization:</b> every bool carries <c>[JsonIgnore(Never)]</c>. The mesh serializer
/// uses <see cref="JsonIgnoreCondition.WhenWritingDefault"/>, which omits the CLR default
/// (<c>false</c>) from the RFC 7396 merge patch — so an "on → off" edit would silently be dropped.
/// Forcing <c>Never</c> makes every value round-trip. Defaults: bell on for all; email on for the
/// two categories a user most wants pushed (access grants + approvals).</para>
/// </summary>
public record NotificationSettings
{
    /// <summary>Show approval notifications in the bell.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    [Description("Approvals — show in the notification bell")]
    public bool ApprovalsInApp { get; init; } = true;

    /// <summary>Show access-granted notifications in the bell.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    [Description("Access granted — show in the notification bell")]
    public bool AccessGrantedInApp { get; init; } = true;

    /// <summary>Show chat-ready notifications in the bell.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    [Description("Chat ready — show in the notification bell")]
    public bool ChatReadyInApp { get; init; } = true;

    /// <summary>Show system notifications in the bell.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    [Description("System — show in the notification bell")]
    public bool SystemInApp { get; init; } = true;

    /// <summary>Email approval notifications.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    [Description("Approvals — send an email")]
    public bool ApprovalsEmail { get; init; } = true;

    /// <summary>Email access-granted notifications.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    [Description("Access granted — send an email")]
    public bool AccessGrantedEmail { get; init; } = true;

    /// <summary>Email chat-ready notifications.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    [Description("Chat ready — send an email")]
    public bool ChatReadyEmail { get; init; }

    /// <summary>Email system notifications.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    [Description("System — send an email")]
    public bool SystemEmail { get; init; }

    /// <summary>Whether the given category is delivered to the bell.</summary>
    public bool InApp(NotificationCategory category) => category switch
    {
        NotificationCategory.Approvals => ApprovalsInApp,
        NotificationCategory.AccessGranted => AccessGrantedInApp,
        NotificationCategory.ChatReady => ChatReadyInApp,
        _ => SystemInApp,
    };

    /// <summary>Whether the given category is delivered by email.</summary>
    public bool Email(NotificationCategory category) => category switch
    {
        NotificationCategory.Approvals => ApprovalsEmail,
        NotificationCategory.AccessGranted => AccessGrantedEmail,
        NotificationCategory.ChatReady => ChatReadyEmail,
        _ => SystemEmail,
    };
}

/// <summary>Maps a <see cref="NotificationType"/> to its preference <see cref="NotificationCategory"/>.</summary>
public static class NotificationCategoryExtensions
{
    /// <summary>The configurable category a notification type belongs to.</summary>
    public static NotificationCategory ToCategory(this NotificationType type) => type switch
    {
        NotificationType.ApprovalRequired
            or NotificationType.ApprovalGiven
            or NotificationType.ApprovalRejected => NotificationCategory.Approvals,
        NotificationType.AccessGranted => NotificationCategory.AccessGranted,
        NotificationType.ChatReady => NotificationCategory.ChatReady,
        _ => NotificationCategory.System,
    };
}

/// <summary>Well-known paths for the per-user <c>NotificationSettings</c> node.</summary>
public static class NotificationSettingsPaths
{
    /// <summary>Path segment (satellite) that holds a user's settings nodes.</summary>
    public const string SettingsSegment = "_Settings";

    /// <summary>Node id of the notification-preferences node under a user's <c>_Settings</c>.</summary>
    public const string NodeId = "Notifications";

    /// <summary>Namespace of the settings node for <paramref name="userId"/> (<c>{userId}/_Settings</c>).</summary>
    public static string NamespaceFor(string userId) => $"{userId}/{SettingsSegment}";

    /// <summary>Full path of the notification-preferences node for <paramref name="userId"/>.</summary>
    public static string PathFor(string userId) => $"{userId}/{SettingsSegment}/{NodeId}";
}
