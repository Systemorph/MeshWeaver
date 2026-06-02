using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace MeshWeaver.Mesh;

/// <summary>
/// Persisted state of the inbound-mail Graph change-notification subscription, so the subscription
/// survives a portal restart and is <b>renewed/reused</b> instead of re-created. Without this, every
/// restart calls Graph <c>POST /subscriptions</c> afresh and leaves an extra live subscription behind —
/// so each inbound email is delivered (and processed) once per stale subscription. One per mailbox inbox,
/// stored at a stable path (<c>Admin/_GraphSubscription/inbox</c>).
/// </summary>
public record GraphSubscriptionState
{
    [Browsable(false)]
    [Key]
    public string Id { get; init; } = "inbox";   // one per inbox → stable id

    /// <summary>The Graph subscription id to renew (or recreate if it has expired/been deleted).</summary>
    public string? SubscriptionId { get; init; }

    /// <summary>The watched resource (e.g. <c>users/memex@…/mailFolders('inbox')/messages</c>).</summary>
    public string? Resource { get; init; }

    /// <summary>The notification URL Graph calls back (e.g. <c>https://…/api/email</c>).</summary>
    public string? NotificationUrl { get; init; }

    /// <summary>Current expiry; the renew timer extends it well before this.</summary>
    [Browsable(false)]
    public DateTimeOffset? ExpiresAt { get; init; }
}
