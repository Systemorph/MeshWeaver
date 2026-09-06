using System.Collections.Immutable;

namespace MeshWeaver.Mesh;

/// <summary>
/// One outbound message, with everything that decides who receives it and who they can see —
/// <see cref="To"/>, <see cref="Cc"/>, <see cref="Bcc"/>, the subject, the HTML body, the
/// attachments and the sender identity.
///
/// <para>🚨 <b>Why this exists (#3473).</b> Every
/// <see cref="IEmailSender.SendEmail(string,string,string,IReadOnlyCollection{EmailAttachment},EmailDelivery)"/>
/// overload takes ONE address, so a caller with several recipients had exactly one option: send the
/// mail once per recipient. That is a different thing from the mail the sender approved. The
/// recipients cannot see each other, a Reply-All reaches nobody but the sender, the thread splits
/// into N unrelated conversations, and Bcc — whose entire definition is "on this message but not on
/// its visible header" — has no expressible form at all. A To with seven Cc is the ordinary shape
/// of a business mail; it is not N mails.</para>
///
/// <para><b>Copied recipients are a property of the MESSAGE, so they live on the message.</b> A
/// transport that can carry them (Microsoft Graph builds exactly this envelope) maps it directly; a
/// transport that cannot must REFUSE rather than quietly deliver something else — see
/// <see cref="IEmailSender.SendEmail(EmailMessage)"/>. Silently dropping a Cc is the same class of
/// defect as stamping an undelivered mail <c>Sent</c>: the sender is told the thing they asked for
/// happened.</para>
///
/// <para>Immutable throughout (<see cref="ImmutableArray{T}"/> per the collections policy), so a
/// message can be composed, logged and handed across hubs without a defensive copy.</para>
/// </summary>
public sealed record EmailMessage
{
    /// <summary>The subject line.</summary>
    public required string Subject { get; init; }

    /// <summary>The HTML body.</summary>
    public required string HtmlBody { get; init; }

    /// <summary>
    /// The primary recipients — the people the message is addressed TO. Visible to everyone who
    /// receives it. At least one of <see cref="To"/>, <see cref="Cc"/> or <see cref="Bcc"/> must be
    /// non-empty for the message to be deliverable.
    /// </summary>
    public ImmutableArray<string> To { get; init; } = [];

    /// <summary>
    /// Copied recipients. Visible to every other recipient — that visibility is the point, and it
    /// is what "one message" buys that N messages cannot.
    /// </summary>
    public ImmutableArray<string> Cc { get; init; } = [];

    /// <summary>
    /// Blind-copied recipients. They receive the message; nobody else — including the other
    /// blind-copied recipients — sees that they did.
    ///
    /// <para>🚨 There is no way to approximate this with per-recipient sends: sending separately to
    /// a Bcc address produces a message whose visible header does not match the one the other
    /// recipients got, and a Reply-All from it addresses people the sender never chose to reveal.
    /// Bcc is expressible only on a single message.</para>
    /// </summary>
    public ImmutableArray<string> Bcc { get; init; } = [];

    /// <summary>Files to attach, including <c>cid:</c> inline parts. Empty by default.</summary>
    public IReadOnlyCollection<EmailAttachment> Attachments { get; init; } = [];

    /// <summary>Who the message comes from and where replies go. Defaults to the shared mailbox.</summary>
    public EmailDelivery Delivery { get; init; } = EmailDelivery.AsSharedMailbox;

    /// <summary>
    /// The recipients every reader of the message can SEE — <see cref="To"/> then <see cref="Cc"/>,
    /// de-duplicated case-insensitively, in that order. <see cref="Bcc"/> is deliberately absent:
    /// this is the visible header, and a Bcc address appearing here would be the disclosure the
    /// field exists to prevent.
    /// </summary>
    public ImmutableArray<string> VisibleRecipients =>
        [.. To.Concat(Cc).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// Everyone who receives the message, visible and blind alike, de-duplicated
    /// case-insensitively. For "the send reached these addresses" reporting — never for anything a
    /// recipient sees.
    /// </summary>
    public ImmutableArray<string> AllRecipients =>
        [.. To.Concat(Cc).Concat(Bcc).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>True when the message carries copied recipients that only a single send can honour.</summary>
    public bool HasCopiedRecipients => Cc.Length > 0 || Bcc.Length > 0;

    /// <summary>True when nobody at all would receive this message.</summary>
    public bool HasNoRecipients => To.Length == 0 && Cc.Length == 0 && Bcc.Length == 0;

    /// <summary>
    /// The simple case: one recipient, no copies — exactly what the single-address overloads carry.
    /// </summary>
    /// <param name="toAddress">The one recipient.</param>
    /// <param name="subject">Subject line.</param>
    /// <param name="htmlBody">HTML body.</param>
    public static EmailMessage Addressed(string toAddress, string subject, string htmlBody) =>
        new() { To = [toAddress], Subject = subject, HtmlBody = htmlBody };

    /// <summary>
    /// True when this message is exactly what a single-address sender can carry: one <see cref="To"/>,
    /// no <see cref="Cc"/>, no <see cref="Bcc"/>. Used by
    /// <see cref="IEmailSender.SendEmail(EmailMessage)"/>'s default implementation to decide whether
    /// it may forward to the legacy overload or must refuse.
    /// </summary>
    public bool FitsASingleAddressSender => To.Length == 1 && Cc.Length == 0 && Bcc.Length == 0;

    /// <summary>
    /// The refusal a sender that cannot carry copied recipients gives — naming the counts, so the
    /// operator sees WHAT could not be honoured rather than a mail that quietly went to fewer
    /// people than it said.
    /// </summary>
    /// <param name="sender">The sender type that cannot carry this envelope.</param>
    public string DescribeUnsupportedEnvelope(Type sender) =>
        HasNoRecipients
            ? $"This message has no recipients at all (subject: \"{Subject}\"), so "
              + $"{sender.Name} has nobody to send it to. Nothing was sent."
            : $"{sender.Name} can only send to a single recipient, so it cannot deliver this "
              + $"message as ONE mail (To: {To.Length}, Cc: {Cc.Length}, Bcc: {Bcc.Length}; "
              + $"subject: \"{Subject}\"). Sending it once per recipient would be a DIFFERENT "
              + "message — the recipients could not see each other, Reply-All would reach nobody, "
              + "and a Bcc would not be blind. Nothing was sent. Use a sender that implements "
              + $"{nameof(IEmailSender)}.{nameof(IEmailSender.SendEmail)}({nameof(EmailMessage)}), "
              + "or address the message to one recipient.";
}
