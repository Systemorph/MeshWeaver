namespace MeshWeaver.Mesh;

/// <summary>
/// Who an outbound message comes FROM, and where replies should go.
///
/// <para>Two identities exist, and the difference is visible to the recipient:</para>
/// <list type="bullet">
/// <item><description><b>The user</b> (<see cref="AsUser"/>) — sent with the signed-in person's
/// own <b>delegated</b> Graph credential. The recipient sees the person, replies reach them
/// naturally, and the message lands in the sender's own Sent Items. This is what a personal act
/// like sharing a document should do.</description></item>
/// <item><description><b>The shared mailbox</b> (<see cref="AsSharedMailbox"/>) — the configured
/// system mailbox, sent with the application credential. Right for notifications, invitations and
/// automation; wrong as a silent default for a person sharing a document with a client.</description></item>
/// </list>
///
/// <para>The choice is never made implicitly: a caller asks for one, and the UI shows which was
/// used. When the shared mailbox is used on a person's behalf, set <see cref="ReplyToAddress"/> to
/// that person so replies still reach a human.</para>
/// </summary>
public sealed record EmailDelivery
{
    /// <summary>
    /// Entra object id of the user to send AS, using their delegated credential. Null = send with
    /// the application credential from the configured shared mailbox.
    /// </summary>
    public string? SendAsUserObjectId { get; init; }

    /// <summary>
    /// Address replies should go to. Set this whenever a message is sent from the shared mailbox on
    /// a person's behalf, so a reply reaches the human rather than a generic inbox.
    /// </summary>
    public string? ReplyToAddress { get; init; }

    /// <summary>The system default: shared mailbox, no reply-to override.</summary>
    public static EmailDelivery AsSharedMailbox { get; } = new();

    /// <summary>Send from the shared mailbox but route replies to <paramref name="replyTo"/>.</summary>
    public static EmailDelivery AsSharedMailboxReplyingTo(string? replyTo) =>
        new() { ReplyToAddress = replyTo };

    /// <summary>
    /// Send as the user themselves (delegated). Requires a connected personal-assistant credential
    /// with the delegated <c>Mail.Send</c> scope; probe with
    /// <see cref="IEmailSender.CanSendAsUser"/> first and never fall back silently.
    /// </summary>
    public static EmailDelivery AsUser(string userObjectId) =>
        new() { SendAsUserObjectId = userObjectId };

    /// <summary>True when this asks for a delegated, send-as-the-person message.</summary>
    public bool IsUserIdentity => !string.IsNullOrEmpty(SendAsUserObjectId);
}
