using System.Reactive.Linq;
using MeshWeaver.Messaging;

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
}
