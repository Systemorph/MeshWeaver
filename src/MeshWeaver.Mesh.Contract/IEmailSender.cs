using System.Reactive.Linq;

namespace MeshWeaver.Mesh;

/// <summary>
/// Sends outbound system email. Not hub-reachable — called from Blazor click actions, the
/// invitation flow, and mesh scripts (via <see cref="HubEmailExtensions.SendEmail(MeshWeaver.Messaging.IMessageHub,string,string,string)"/>) — so it
/// exposes a reactive shape (<see cref="IObservable{T}"/>) that composes cleanly with the rest of
/// the codebase's reactive pipelines. Implementations bridge their async leaf (e.g. Microsoft
/// Graph) through a bounded <c>IIoPool</c> — the <c>MeshWeaver.Mail.MicrosoftGraph</c> module's <c>GraphEmailSender</c> uses
/// <c>_http.Run(...)</c> — and <b>never</b> <c>Observable.FromAsync</c>, which is forbidden
/// outside <c>IoPool</c>: it runs the prologue on the subscribing thread and bounds nothing
/// (see <c>Doc/Architecture/ControlledIoPooling.md</c>).
///
/// <para>The abstraction lives in the framework so scripts and framework code can trigger mail
/// (e.g. notifications) without referencing the hosting app; the concrete sender is registered by
/// the host's <c>NoOpEmailSender</c>, or the <c>MeshWeaver.Mail.MicrosoftGraph</c> module's <c>GraphEmailSender</c> when listed).</para>
/// </summary>
public interface IEmailSender
{
    /// <summary>
    /// Whether this sender ACTUALLY DELIVERS mail — false for a sender whose contract is to
    /// report success without sending (the host's no-op).
    ///
    /// <para>🚨 This exists because "a sender is registered" and "mail can be delivered" were the
    /// same question for exactly as long as the only real sender was compiled in. Once the Graph
    /// sender moved into the <c>MeshWeaver.Mail.MicrosoftGraph</c> module, a deployment could have
    /// <c>Email:Enabled=true</c> AND resolve the no-op — and every queued mail was then stamped
    /// <c>Sent</c> while nothing left the process. Undeliverable mail marked delivered is silent
    /// data loss, and the only thing that separates it from a working install is this flag
    /// (#2023).</para>
    ///
    /// <para>Defaults to <c>true</c>: a sender says it cannot deliver only when it genuinely
    /// cannot, so a third-party implementation that forgets this member is treated as a real
    /// sender — the safe direction, because a real sender wrongly reported as a no-op would refuse
    /// mail that would have gone out.</para>
    /// </summary>
    bool DeliversMail => true;

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
    /// 🚨 <b>Deprecated in favour of <see cref="ObserveSendAsCapability"/> — two answers for three
    /// states of the world (#3450).</b> A check that never completed and a check that found no
    /// credential both arrive here as <c>false</c>, and the send dialog rendered that as a
    /// user-visible <i>"your Microsoft 365 mailbox is not connected yet"</i>. On a transient fault
    /// that statement is untrue, and a connected user was told to connect.
    ///
    /// <para>Kept as the two-state shim over the three-state probe so every existing implementer
    /// and caller keeps working: <see cref="ObserveSendAsCapability"/>'s default folds this member,
    /// and <c>HubEmailExtensions.CanSendAsUser</c> folds the other way. Deliberately NOT
    /// <c>[Obsolete]</c>, for the reason spelled out on <see cref="IEaGraphAuth"/>'s retiring
    /// surface: MeshWeaver.Plugins builds <c>-warnaserror</c> against a core CHECKOUT at a pinned
    /// ref, so the attribute would red that repo before its own half could land. Do NOT add a new
    /// caller — ask <see cref="ObserveSendAsCapability"/>.</para>
    ///
    /// <para>Whether this sender can send AS <paramref name="userObjectId"/> right now — i.e. the
    /// user has connected their mailbox and the stored delegated credential carries
    /// <c>Mail.Send</c>. Defaults to <c>false</c>: a sender says it can act as a person only when
    /// it genuinely can.</para>
    /// </summary>
    /// <param name="userObjectId">Directory object id of the user to check.</param>
    IObservable<bool> CanSendAsUser(string userObjectId)
        => Observable.Return(false);

    /// <summary>
    /// What is KNOWN about this sender's ability to send as <paramref name="userObjectId"/> — the
    /// three-state answer <see cref="CanSendAsUser"/> cannot express (#3450).
    ///
    /// <para>Ask BEFORE composing, so the UI can state which identity will be used. The state
    /// decides what the UI may claim, and the claim rule lives on the answer:
    /// <see cref="EmailSendAsCapability.OffersConnect"/> is true for
    /// <see cref="EmailSendAs.Unavailable"/> ALONE, so "you have not connected" is never rendered
    /// for a check that simply did not complete.</para>
    ///
    /// <para><b>The default folds <see cref="CanSendAsUser"/>, which makes this purely additive.</b>
    /// A sender written before this member — including every implementation in a repository that
    /// pins an older core — keeps answering exactly what it answered before, in two states. Only a
    /// sender that can genuinely tell an unanswered check from a negative one overrides this; it
    /// then gets <see cref="EmailSendAs.Undetermined"/> for free at every consumer, because
    /// <c>HubEmailExtensions.CanSendAsUser</c> folds this member rather than calling the old one.
    /// The two defaults deliberately point in opposite directions and never recurse.</para>
    ///
    /// <para>🚨 An implementation must NOT collapse a fault into
    /// <see cref="EmailSendAs.Unavailable"/> — that is the #3450 swallow written inside the sender
    /// instead of at the call site. Surface the fault, or answer
    /// <see cref="EmailSendAsCapability.Unknown(Exception)"/>.</para>
    /// </summary>
    /// <param name="userObjectId">Directory object id of the user to check.</param>
    IObservable<EmailSendAsCapability> ObserveSendAsCapability(string userObjectId)
        => CanSendAsUser(userObjectId)
            .Select(can => can
                ? EmailSendAsCapability.Available
                : EmailSendAsCapability.Unavailable(
                    $"{GetType().Name} answers send-as with yes/no only, so this negative is what "
                    + "it knows — it cannot report a check that failed to complete."));

    /// <summary>
    /// Sends ONE message, with all of its recipients — <see cref="EmailMessage.To"/>,
    /// <see cref="EmailMessage.Cc"/> and <see cref="EmailMessage.Bcc"/> — as a single mail (#3473).
    /// Cold observable: the send runs on Subscribe and emits a single <c>true</c> on success, or
    /// surfaces the failure via OnError.
    ///
    /// <para>🚨 <b>The default REFUSES what it cannot honour; it never silently drops it.</b> A
    /// message that fits a single-address sender (one To, no copies) is forwarded to the existing
    /// overload unchanged — so every sender written before this member keeps working for every call
    /// it could already serve. A message with Cc, Bcc, or several To addresses cannot be delivered
    /// as ONE mail by a sender that only knows one address, and fanning it out would deliver a
    /// DIFFERENT message: recipients unable to see each other, Reply-All reaching nobody, and a Bcc
    /// that is not blind. So it faults with a message naming the counts.</para>
    ///
    /// <para>Refusing is the same principle as <see cref="DeliversMail"/>: a sender that reports
    /// success for something it did not do converts a capability gap into silent data loss.</para>
    /// </summary>
    /// <param name="message">The message, its recipients, its attachments and its sender identity.</param>
    IObservable<bool> SendEmail(EmailMessage message)
        => message.FitsASingleAddressSender
            ? SendEmail(
                message.To[0], message.Subject, message.HtmlBody,
                message.Attachments, message.Delivery)
            : Observable.Throw<bool>(
                new NotSupportedException(message.DescribeUnsupportedEnvelope(GetType())));

    /// <summary>
    /// Where a user connects their own mailbox on THIS deployment, as an app-relative path — or
    /// <c>null</c> when the deployment offers no such flow, in which case the UI must show no
    /// connect affordance at all rather than a link that goes nowhere.
    ///
    /// <para>The route belongs to the host that registers the endpoint, so the host is what names
    /// it. Framework UI (e.g. the send-document dialog) asks here instead of hard-coding a path it
    /// cannot verify: <c>MeshWeaver.Markdown.Export</c> has no way to know whether
    /// <c>/auth/ea/connect</c> exists — on a non-memex host it does not — and a hard-coded copy is
    /// exactly how a button starts 404-ing without anything failing to compile.</para>
    ///
    /// <para>🚨 This is a SERVER-side endpoint. In-app navigation to it must be a full browser load
    /// (<c>ctx.NavigateTo(href, forceLoad: true)</c>); a client-side Blazor navigation is swallowed
    /// by the router's catch-all and reported as an unresolvable mesh path.</para>
    /// </summary>
    string? ConnectAsUserHref => null;
}
