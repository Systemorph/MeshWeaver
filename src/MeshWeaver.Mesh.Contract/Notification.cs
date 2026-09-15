using System.Collections.Immutable;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace MeshWeaver.Mesh;

/// <summary>Type of notification. New values are APPENDED (never inserted) so the
/// serialized ordinals of existing rows stay stable.</summary>
public enum NotificationType
{
    /// <summary>An approval is required from this user.</summary>
    ApprovalRequired,
    /// <summary>An approval was granted.</summary>
    ApprovalGiven,
    /// <summary>An approval was rejected.</summary>
    ApprovalRejected,
    /// <summary>General notification (maps to the <see cref="NotificationCategory.System"/> preference).</summary>
    General,
    /// <summary>A user was granted access (a role) on a node.</summary>
    AccessGranted,
    /// <summary>An AI thread round finished and a response is ready.</summary>
    ChatReady,
    /// <summary>A platform/system event (indexing/import failure, compile park, …).</summary>
    System
}

/// <summary>
/// Represents a notification ADDRESSED to exactly one addressee — a person (their user partition)
/// or the platform operators collectively (<c>Admin</c>).
///
/// <para>🚨 <b>The addressee owns the delivery location, not the entity the notification is
/// about.</b> A notification node lives at <c>{addressee}/_Notification/{id}</c> with
/// <c>MainNode = {addressee}</c>; the entity it concerns is a REFERENCE in
/// <see cref="TargetNodePath"/>. Storage routes through the addressee's <c>notifications</c> table
/// via <see cref="SatelliteTableMapping"/>, so the bell reads ONE schema per addressee instead of
/// UNIONing every partition on the server. Two consequences fall out and both are the point:
/// visibility is the ordinary path-based permission fold on the addressee's partition (no
/// satellite rule needed), and mark-as-read writes into the reader's own partition.</para>
///
/// <para>Written before 2026-09-03 these lived under the ENTITY —
/// <c>{mainEntityPath}/_Notification/{id}</c> — which is why the bell could not name a partition
/// and why a platform-admin notification in <c>Admin</c> was never returned at all
/// (<c>Admin</c> is excluded from <c>public.searchable_schemas</c>). See
/// Doc/Architecture/AddressedNotifications.</para>
/// </summary>
public record Notification
{
    /// <summary>Unique identifier for the notification.</summary>
    [Browsable(false)]
    [Key]
    public string Id { get; init; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Title of the notification, in English — the FALLBACK when <see cref="TitleKey"/> is unset or
    /// names a key that is in no catalog. Read it through
    /// <c>NotificationLocalizationExtensions.LocalizedTitle</c>, never directly, on any surface a
    /// person looks at.
    /// </summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// Detailed message body, in English — the FALLBACK for <see cref="MessageKey"/>, exactly as
    /// <see cref="Title"/> is for <see cref="TitleKey"/>.
    /// </summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// Catalog key (<c>notification.*</c>) for <see cref="Title"/>; when set, the reader resolves
    /// THIS in their own language and <see cref="Title"/> is only the fallback.
    ///
    /// <para>🚨 <b>A notification is written with no viewer in scope</b> — a package-update
    /// reconciler poll, a module-discovery scan, a startup-error drain, a compile park — so the
    /// writer runs as SYSTEM and cannot know whose language to pick, while the row it lands in is
    /// read for days by people whose languages differ. Resolving at write time bakes one language
    /// into a shared record and is the bug this exists to stop
    /// (Systemorph/MeshWeaver#4373). Same split as <c>LogMessage.MessageKey</c> for activity
    /// transcripts (#3236).</para>
    ///
    /// <para>Null on every row written before #4373, and on every notification whose title is
    /// verbatim upstream text no catalog can carry.</para>
    /// </summary>
    [Browsable(false)]
    public string? TitleKey { get; init; }

    /// <summary>
    /// The named arguments for <see cref="TitleKey"/> — the template refers to them as
    /// <c>{name}</c>, <c>{count}</c>, … so a translator may reorder them freely. Named rather than
    /// positional precisely because these are PERSISTED: a row written months ago must still bind
    /// correctly to a template someone has since rewritten.
    /// <para>Values round-trip through JSON, so a value read back is typically a
    /// <see cref="System.Text.Json.JsonElement"/>; the renderer switches on its kind and never
    /// casts.</para>
    /// </summary>
    [Browsable(false)]
    public ImmutableDictionary<string, object>? TitleArgs { get; init; }

    /// <summary>Catalog key for <see cref="Message"/> — see <see cref="TitleKey"/>.</summary>
    [Browsable(false)]
    public string? MessageKey { get; init; }

    /// <summary>Named arguments for <see cref="MessageKey"/> — see <see cref="TitleArgs"/>.</summary>
    [Browsable(false)]
    public ImmutableDictionary<string, object>? MessageArgs { get; init; }

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

    /// <summary>
    /// The ADDRESSEE — the partition this notification was delivered into: a person's user
    /// partition, or <c>Admin</c> for one addressed to the platform operators collectively.
    ///
    /// <para>The path already carries it (<c>{Recipient}/_Notification/{Id}</c>); recording it on
    /// the content is what makes the invariant CHECKABLE — a census test and a create-time
    /// validator can ask the node instead of parsing its path, and a repair pass can tell an
    /// already-addressed row from a legacy one. <c>null</c> on every row written before the
    /// addressed model (Doc/Architecture/AddressedNotifications).</para>
    /// </summary>
    [Browsable(false)]
    public string? Recipient { get; init; }
}
