using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace MeshWeaver.Mesh;

/// <summary>Lifecycle state of an <see cref="Invitation"/>.</summary>
public enum InvitationStatus
{
    /// <summary>Outstanding — the invited email may onboard.</summary>
    Pending,
    /// <summary>The invited email signed in and completed onboarding.</summary>
    Accepted,
    /// <summary>Withdrawn by an admin — the email may no longer onboard.</summary>
    Revoked
}

/// <summary>
/// An invitation that authorises a specific email address to complete onboarding when
/// the deployment runs in invitation-only mode
/// (<c>Features:Onboarding:InvitationOnly = true</c>).
///
/// <para>Invitations are stored as MeshNodes in the always-present <b>Admin</b> partition
/// (path <c>Admin/Invitation/{slug}</c>) and made globally queryable by the onboarding
/// flow via a <c>nodeType:Invitation → Admin</c> query-routing rule (see
/// <c>MeshWeaver.Graph.Configuration.InvitationNodeType</c>). The acceptance model
/// is a verified-email allowlist: the IdP proves the user owns the email, and a
/// <see cref="InvitationStatus.Pending"/> invitation matching that email unlocks onboarding,
/// after which it flips to <see cref="InvitationStatus.Accepted"/>.</para>
/// </summary>
public record Invitation
{
    /// <summary>Unique identifier for the invitation.</summary>
    [Browsable(false)]
    [Key]
    public string Id { get; init; } = Guid.NewGuid().ToString();

    /// <summary>
    /// The invited email — the allowlist key. The onboarding gate matches against this via
    /// the synced query <c>nodeType:Invitation content.email:{email}</c>.
    /// </summary>
    public string Email { get; init; } = string.Empty;

    /// <summary>ObjectId of the admin who issued the invitation.</summary>
    [Browsable(false)]
    public string? InvitedBy { get; init; }

    /// <summary>When the invitation was issued.</summary>
    [Browsable(false)]
    public DateTimeOffset InvitedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Current lifecycle state.</summary>
    [Browsable(false)]
    public InvitationStatus Status { get; init; } = InvitationStatus.Pending;

    /// <summary>When the invited user completed onboarding (set on acceptance).</summary>
    [Browsable(false)]
    public DateTimeOffset? AcceptedAt { get; init; }

    /// <summary>Optional free-text note shown to the admin.</summary>
    public string? Note { get; init; }

    /// <summary>
    /// Optional path of the Space this person was invited to (set by
    /// <c>SpaceInviteService</c> when inviting a not-yet-onboarded email to a specific Space).
    /// When set, the invitation email addresses the Space by name and links straight to it
    /// (<c>{baseUrl}/{SpacePath}</c>) instead of the generic "invited to Memex" wording; the
    /// access grant itself lands on sign-up via the matching <c>EventSubscription</c>.
    /// <c>null</c> = a deployment-wide invitation (the Invitations settings tab).
    /// </summary>
    [Browsable(false)]
    public string? SpacePath { get; init; }

    /// <summary>
    /// When the invitation email was sent (set by the node-driven
    /// <c>InvitationEmailSender</c> hosted service). <c>null</c> = not yet emailed —
    /// the sender stamps this so it never re-sends, regardless of how the invitation was
    /// created (Invitations settings tab, MCP, REST). The email is decoupled from the
    /// creation entry point: any Pending invitation with a null value gets emailed once.
    /// </summary>
    [Browsable(false)]
    public DateTimeOffset? EmailSentAt { get; init; }
}
