using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace MeshWeaver.Mesh;

/// <summary>Direction of an <see cref="Email"/> relative to the portal.</summary>
public enum EmailDirection
{
    /// <summary>Received by the portal mailbox.</summary>
    Inbound,
    /// <summary>Sent by the portal.</summary>
    Outbound
}

/// <summary>Triage state of an inbound <see cref="Email"/> in the admin inbox.</summary>
public enum EmailStatus
{
    /// <summary>Newly received, not yet seen by an admin.</summary>
    New,
    /// <summary>An admin has read it.</summary>
    Read,
    /// <summary>Filed away.</summary>
    Archived
}

/// <summary>
/// A single email message, persisted as a MeshNode so the portal keeps a full record of mail it
/// receives and sends. Inbound mail from a known user is stored under that user's partition
/// (<c>{username}/_Email/{id}</c>) and drives an agent thread; inbound mail from a non-user is stored
/// in the admin inbox (<c>Admin/Inbox/{id}</c>) for triage. Outbound replies/sends are recorded too.
///
/// <para>Email nodes deliberately have <b>no</b> global query-routing rule (unlike
/// <see cref="Invitation"/>): they live across many partitions, so normal first-segment partition
/// routing applies.</para>
/// </summary>
public record Email
{
    /// <summary>Unique identifier for this email node.</summary>
    [Browsable(false)]
    [Key]
    public string Id { get; init; } = Guid.NewGuid().ToString();

    /// <summary>Inbound (received) or Outbound (sent).</summary>
    public EmailDirection Direction { get; init; } = EmailDirection.Inbound;

    /// <summary>Sender address (for inbound) — the routing key matched against <c>User.Email</c>.</summary>
    public string From { get; init; } = string.Empty;

    /// <summary>Sender display name, when provided.</summary>
    [Browsable(false)]
    public string? FromName { get; init; }

    /// <summary>Recipient address (for outbound; the portal mailbox for inbound).</summary>
    public string? To { get; init; }

    /// <summary>Subject line.</summary>
    public string Subject { get; init; } = string.Empty;

    /// <summary>Message body (HTML or plain text).</summary>
    public string Body { get; init; } = string.Empty;

    /// <summary>Graph conversation id — used to match a reply back to its thread.</summary>
    [Browsable(false)]
    public string? ConversationId { get; init; }

    /// <summary>RFC 5322 Message-Id — used as <c>In-Reply-To</c>/<c>References</c> when replying.</summary>
    [Browsable(false)]
    public string? InternetMessageId { get; init; }

    /// <summary>When the mail was received/sent.</summary>
    [Browsable(false)]
    public DateTimeOffset ReceivedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Triage state (inbox only).</summary>
    public EmailStatus Status { get; init; } = EmailStatus.New;

    /// <summary>Path of the agent thread this email is bound to, when it drove one.</summary>
    [Browsable(false)]
    public string? ThreadPath { get; init; }
}
