using System.Reactive.Linq;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Mesh;

/// <summary>
/// Hub-level convenience for sending email from anywhere that has an <see cref="IMessageHub"/> —
/// app code, handlers, and especially <b>mesh scripts</b> (the <c>Mesh</c> global). Resolves the
/// registered <see cref="IEmailSender"/> from the hub's service provider so callers never name the
/// concrete sender or its package.
///
/// <para>Example — trigger a notification mail from a Code node / interactive cell:</para>
/// <code>
/// Mesh.SendEmail("alice@example.com", "Build finished", "&lt;p&gt;Your export is ready.&lt;/p&gt;")
///     .Subscribe(ok =&gt; Log.LogInformation("mail sent: {Ok}", ok),
///                ex =&gt; Log.LogError(ex, "mail failed"));
/// </code>
///
/// <para>Reactive end-to-end: the returned observable is cold — the send runs on Subscribe. Returns
/// an observable yielding <c>false</c> (rather than throwing) when no <see cref="IEmailSender"/> is
/// registered, so a script on a deployment without email configured degrades gracefully.</para>
/// </summary>
public static class HubEmailExtensions
{
    /// <summary>
    /// Sends an HTML email via the registered <see cref="IEmailSender"/>. Cold observable —
    /// subscribe to drive. Emits <c>false</c> if no sender is registered.
    /// </summary>
    public static IObservable<bool> SendEmail(
        this IMessageHub hub, string toAddress, string subject, string htmlBody)
    {
        var sender = hub.ServiceProvider.GetService(typeof(IEmailSender)) as IEmailSender;
        return sender is null
            ? Observable.Return(false)
            : sender.SendEmail(toAddress, subject, htmlBody);
    }

    /// <summary>
    /// Sends an HTML email with file <paramref name="attachments"/> via the registered
    /// <see cref="IEmailSender"/> (e.g. a deck/document exported to PDF via the node ⇒ file pipeline).
    /// Cold observable — subscribe to drive. Emits <c>false</c> if no sender is registered.
    /// </summary>
    public static IObservable<bool> SendEmail(
        this IMessageHub hub, string toAddress, string subject, string htmlBody,
        IReadOnlyCollection<EmailAttachment> attachments)
    {
        var sender = hub.ServiceProvider.GetService(typeof(IEmailSender)) as IEmailSender;
        return sender is null
            ? Observable.Return(false)
            : sender.SendEmail(toAddress, subject, htmlBody, attachments);
    }

    /// <summary>
    /// Sends with an explicit sender identity — as the signed-in user (their own delegated
    /// credential) or from the shared mailbox with a reply-to. See <see cref="EmailDelivery"/>.
    /// Cold observable — subscribe to drive. Emits <c>false</c> if no sender is registered.
    /// </summary>
    public static IObservable<bool> SendEmail(
        this IMessageHub hub, string toAddress, string subject, string htmlBody,
        IReadOnlyCollection<EmailAttachment> attachments, EmailDelivery delivery)
    {
        var sender = hub.ServiceProvider.GetService(typeof(IEmailSender)) as IEmailSender;
        return sender is null
            ? Observable.Return(false)
            : sender.SendEmail(toAddress, subject, htmlBody, attachments, delivery);
    }

    /// <summary>
    /// Sends ONE message with all of its recipients — To, Cc and Bcc — as a single mail (#3473).
    /// Cold observable: subscribe to drive. Emits <c>false</c> if no sender is registered.
    ///
    /// <para>A sender that cannot carry copied recipients FAULTS rather than dropping them; see
    /// <see cref="IEmailSender.SendEmail(EmailMessage)"/>. That fault is deliberately not caught
    /// here — a caller that asked for a Cc must learn it did not happen.</para>
    /// </summary>
    /// <param name="hub">Hub whose service provider holds the registered sender.</param>
    /// <param name="message">The message, its recipients, its attachments and its sender identity.</param>
    public static IObservable<bool> SendEmail(this IMessageHub hub, EmailMessage message)
    {
        var sender = hub.ServiceProvider.GetService(typeof(IEmailSender)) as IEmailSender;
        return sender is null
            ? Observable.Return(false)
            : sender.SendEmail(message);
    }

    /// <summary>
    /// What is KNOWN about whether the registered sender can send AS <paramref name="userObjectId"/>
    /// right now — the three-state probe (#3450). Ask before composing so the UI can STATE which
    /// identity will be used, never discover it at send time.
    ///
    /// <para>🚨 <b>A faulted or silent probe becomes <see cref="EmailSendAs.Undetermined"/> here,
    /// and is LOGGED at Warning naming the user and the diagnostic.</b> Every consumer used to
    /// write <c>.Catch(_ =&gt; Observable.Return(false))</c> at its own call site, which made a
    /// transport fault indistinguishable from "this person never connected" — with nothing logged,
    /// so the third state was not even greppable. Routing the fault into the modelled state, once,
    /// where the sender is resolved, is what stops each consumer having to remember. It is not a
    /// swallow: the information is carried, not discarded.</para>
    ///
    /// <para>A probe that completes without emitting is the same non-answer as a faulted one and
    /// gets the same state — an empty observable is exactly how a dropped mesh read looks.</para>
    /// </summary>
    /// <param name="hub">Hub whose service provider holds the registered sender.</param>
    /// <param name="userObjectId">Directory object id of the user to check.</param>
    public static IObservable<EmailSendAsCapability> ObserveSendAsCapability(
        this IMessageHub hub, string userObjectId)
    {
        if (hub.ServiceProvider.GetService(typeof(IEmailSender)) is not IEmailSender sender)
            return Observable.Return(EmailSendAsCapability.Unavailable(
                "No IEmailSender is registered on this deployment, so nothing can send as a user. "
                + "This is a completed check with a negative answer, not an unknown."));

        return sender.ObserveSendAsCapability(userObjectId)
            .Take(1)
            .Catch((Exception ex) =>
            {
                Logger(hub)?.LogWarning(
                    ex,
                    "Send-as capability check FAULTED for user {UserObjectId} on {Sender}: {Diagnostic}. "
                    + "Reported as Undetermined — the UI must not tell this user they have not "
                    + "connected, because nothing is known either way (#3450).",
                    userObjectId, sender.GetType().Name, ex.Message);
                return Observable.Return(EmailSendAsCapability.Unknown(ex));
            })
            .DefaultIfEmpty(EmailSendAsCapability.Unknown(
                $"the send-as check on {sender.GetType().Name} completed without answering"));
    }

    /// <summary>
    /// Whether the registered sender can send AS <paramref name="userObjectId"/> right now (the
    /// user has connected their Microsoft 365 mailbox).
    ///
    /// <para>The two-state fold of <see cref="ObserveSendAsCapability"/>, kept because a decision
    /// that must go one way or the other — <i>send as the person, or ask which mailbox</i> — is
    /// genuinely binary. That is the send-time consumer, and folding is CORRECT there: an unknown
    /// answers <c>false</c>, so the user is asked which mailbox rather than assumed connected.
    /// Anything that RENDERS a claim about the mailbox must ask
    /// <see cref="ObserveSendAsCapability"/> instead, because <c>false</c> here still cannot tell
    /// "not connected" from "we could not check".</para>
    /// </summary>
    /// <param name="hub">Hub whose service provider holds the registered sender.</param>
    /// <param name="userObjectId">Directory object id of the user to check.</param>
    public static IObservable<bool> CanSendAsUser(this IMessageHub hub, string userObjectId) =>
        hub.ObserveSendAsCapability(userObjectId).Select(capability => capability.IsAvailable);

    private static ILogger? Logger(IMessageHub hub) =>
        (hub.ServiceProvider.GetService(typeof(ILoggerFactory)) as ILoggerFactory)
            ?.CreateLogger(typeof(HubEmailExtensions).FullName!);

    /// <summary>
    /// The app-relative path where a user connects their own mailbox on this deployment, or
    /// <c>null</c> when no such flow exists (no sender registered, or a sender that cannot act as a
    /// person). UI must render a connect affordance ONLY when this returns non-null — and must
    /// navigate to it with <c>forceLoad: true</c>, since it is a server-side endpoint.
    /// </summary>
    public static string? ConnectAsUserHref(this IMessageHub hub) =>
        (hub.ServiceProvider.GetService(typeof(IEmailSender)) as IEmailSender)?.ConnectAsUserHref;
}
