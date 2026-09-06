using System.Reactive.Linq;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Email;

/// <summary>
/// The <see cref="IEmailSender"/> an install resolves when no real sender is registered — the
/// <c>TryAddSingleton</c> fallback in <c>MemexConfiguration</c>, taken whenever the
/// <c>MeshWeaver.Mail.MicrosoftGraph</c> module is not on this deployment.
///
/// <para>It reports <see cref="DeliversMail"/> <c>false</c>, and what it does on a send depends
/// entirely on which of TWO configurations reached it:</para>
///
/// <list type="bullet">
/// <item><description><c>Email:Enabled=false</c> — the intended case (local dev, tests, a
/// deployment with no mailbox). Nothing queues mail here, no watcher runs, and callers' reactive
/// chains complete normally: logs the would-be send and emits <c>true</c>.</description></item>
/// <item><description>🚨 <c>Email:Enabled=true</c> — a MISCONFIGURED install: it is set up to send
/// mail and has nothing to send it with. Emitting <c>true</c> here is a lie that
/// <see cref="OutboundEmailSender"/> converts into a durable one, stamping every queued mail
/// <c>Sent</c> while nothing leaves the process (#2023). So this path REFUSES: it logs the cause
/// at Error and surfaces the failure through <c>OnError</c>, which marks the mail
/// <c>Failed</c> — visible and re-queueable — instead of falsely
/// <c>Sent</c>.</description></item>
/// </list>
///
/// <para>The two are told apart by <see cref="EmailOptions.Enabled"/>, which is why this type takes
/// the options rather than defaulting them: a no-op that cannot tell which case it is in cannot
/// report the right thing, and its old log line ("Email disabled (Email:Enabled=false)") stated a
/// falsehood on the second path.</para>
/// </summary>
public sealed class NoOpEmailSender(EmailOptions options, ILogger<NoOpEmailSender>? logger = null) : IEmailSender
{
    /// <summary>Always false — this sender's entire contract is to not deliver. See
    /// <see cref="IEmailSender.DeliversMail"/> for why the flag exists at all.</summary>
    public bool DeliversMail => false;

    public IObservable<bool> SendEmail(string toAddress, string subject, string htmlBody)
        => SendEmail(toAddress, subject, htmlBody, []);

    public IObservable<bool> SendEmail(
        string toAddress, string subject, string htmlBody, IReadOnlyCollection<EmailAttachment> attachments)
        => SendEmail(new EmailMessage
        {
            To = [toAddress],
            Subject = subject,
            HtmlBody = htmlBody,
            Attachments = attachments,
        });

    /// <summary>
    /// 🚨 Overridden so a message with <b>Cc/Bcc</b> is handled by this sender rather than refused
    /// by the interface default (#3473).
    ///
    /// <para>The default refusal exists to stop a single-address transport from quietly delivering
    /// a DIFFERENT message than the one it was handed. That reasoning does not apply here: this
    /// sender delivers nothing at all, to anybody, and says so. Refusing copied recipients would
    /// break every local-dev and test send that carries one, while protecting a delivery that is
    /// not happening either way.</para>
    ///
    /// <para>What it does still do is name the FULL envelope in its log line — To, Cc and Bcc
    /// counts — so a developer reading the log sees the message that would have gone out, copies
    /// included, instead of a line that silently describes fewer recipients than were asked for.
    /// The <c>Email:Enabled=true</c> refusal (#2023) is unchanged and applies to the whole
    /// envelope.</para>
    /// </summary>
    /// <param name="message">The message, its recipients, its attachments and its sender identity.</param>
    public IObservable<bool> SendEmail(EmailMessage message)
    {
        var recipients = string.Join(", ", message.AllRecipients);

        if (options.Enabled)
        {
            // ExplainRefusal, not Explain: this install may have the module and be missing only a
            // credential key (#2510). Naming the module in that case sends the operator to fix
            // something that is not broken.
            var explanation = EmailDeliveryGuard.ExplainRefusal(
                options, "This send is REFUSED rather than reported as delivered.");
            logger?.LogError(
                "Refusing to send to {To} (subject: {Subject}, to: {ToCount}, cc: {CcCount}, "
                + "bcc: {BccCount}, attachments: {Attachments}). {Explanation}",
                recipients, message.Subject, message.To.Length, message.Cc.Length,
                message.Bcc.Length, message.Attachments.Count, explanation);
            return Observable.Throw<bool>(new InvalidOperationException(explanation));
        }

        logger?.LogInformation(
            "Email disabled (Email:Enabled=false) — skipping send to {To} (subject: {Subject}, "
            + "to: {ToCount}, cc: {CcCount}, bcc: {BccCount}, attachments: {Attachments})",
            recipients, message.Subject, message.To.Length, message.Cc.Length,
            message.Bcc.Length, message.Attachments.Count);
        return Observable.Return(true);
    }
}
