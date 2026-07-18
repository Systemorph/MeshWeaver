namespace MeshWeaver.Mesh;

/// <summary>
/// Sends outbound system email. Not hub-reachable — called from Blazor click actions, the
/// invitation flow, and mesh scripts (via <see cref="HubEmailExtensions.SendEmail(MeshWeaver.Messaging.IMessageHub,string,string,string)"/>) — so it
/// exposes a reactive shape (<see cref="IObservable{T}"/>) that composes cleanly with the rest of
/// the codebase's reactive pipelines. Implementations bridge their async leaf (e.g. Microsoft
/// Graph) via <c>Observable.FromAsync</c>.
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
}
