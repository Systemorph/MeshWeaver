namespace MeshWeaver.Mesh;

/// <summary>
/// Sends outbound system email. Not hub-reachable — called from Blazor click actions, the
/// invitation flow, and mesh scripts (via <see cref="HubEmailExtensions.SendEmail(MeshWeaver.Messaging.IMessageHub,string,string,string)"/>) — so it
/// exposes a reactive shape (<see cref="IObservable{T}"/>) that composes cleanly with the rest of
/// the codebase's reactive pipelines. Implementations bridge their async leaf (e.g. Microsoft
/// Graph) through a bounded <c>IIoPool</c> — the portal's <c>GraphEmailSender</c> uses
/// <c>_http.Run(...)</c> — and <b>never</b> <c>Observable.FromAsync</c>, which is forbidden
/// outside <c>IoPool</c>: it runs the prologue on the subscribing thread and bounds nothing
/// (see <c>Doc/Architecture/ControlledIoPooling.md</c>).
///
/// <para>The abstraction lives in the framework so scripts and framework code can trigger mail
/// (e.g. notifications) without referencing the hosting app; the concrete sender is registered by
/// the host (see the portal's <c>GraphEmailSender</c> / <c>NoOpEmailSender</c>).</para>
/// </summary>
public interface IEmailSender
{
    /// <summary>
    /// Sends an HTML email. The returned observable is cold — the send runs on Subscribe and
    /// emits a single <c>true</c> on success, or surfaces the failure via OnError.
    /// </summary>
    IObservable<bool> SendEmail(string toAddress, string subject, string htmlBody);

    /// <summary>
    /// Sends an HTML email with one or more file <paramref name="attachments"/> (e.g. a deck/document
    /// exported to PDF via the platform's node ⇒ file pipeline). Additive overload — existing
    /// no-attachment callers are unaffected. Cold observable: the send runs on Subscribe and emits a
    /// single <c>true</c> on success, or surfaces the failure via OnError.
    /// </summary>
    /// <param name="toAddress">Recipient email address.</param>
    /// <param name="subject">Subject line.</param>
    /// <param name="htmlBody">HTML message body.</param>
    /// <param name="attachments">Files to attach. Empty is equivalent to the no-attachment overload.</param>
    IObservable<bool> SendEmail(
        string toAddress, string subject, string htmlBody, IReadOnlyCollection<EmailAttachment> attachments);

    /// <summary>
    /// Sends with an explicit sender identity — as the signed-in user (their own delegated
    /// credential) or from the shared mailbox with a reply-to. See <see cref="EmailDelivery"/>.
    ///
    /// <para>Default implementation ignores <paramref name="delivery"/> and sends the ordinary way,
    /// so a sender that cannot act on behalf of a user (the no-op sender, a test double, an SMTP
    /// implementation) keeps working unchanged. A caller that NEEDS the user's identity must first
    /// ask <see cref="CanSendAsUser"/> — that is what makes "we sent as the shared mailbox
    /// instead" a decision the user sees rather than a silent substitution.</para>
    /// </summary>
    IObservable<bool> SendEmail(
        string toAddress, string subject, string htmlBody,
        IReadOnlyCollection<EmailAttachment> attachments, EmailDelivery delivery)
        => SendEmail(toAddress, subject, htmlBody, attachments);

    /// <summary>
    /// Whether this sender can send AS <paramref name="userObjectId"/> right now — i.e. the user
    /// has connected their mailbox and the stored delegated credential carries <c>Mail.Send</c>.
    ///
    /// <para>Ask BEFORE composing, so the UI can state which identity will be used and offer to
    /// connect Microsoft 365 when it cannot. Defaults to <c>false</c>: a sender says it can act as
    /// a person only when it genuinely can.</para>
    /// </summary>
    IObservable<bool> CanSendAsUser(string userObjectId)
        => System.Reactive.Linq.Observable.Return(false);
}
