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
    {
        if (options.Enabled)
        {
            var explanation = EmailDeliveryGuard.Explain(
                "This send is REFUSED rather than reported as delivered.");
            logger?.LogError(
                "Refusing to send to {To} (subject: {Subject}, attachments: {Attachments}). {Explanation}",
                toAddress, subject, attachments.Count, explanation);
            return Observable.Throw<bool>(new InvalidOperationException(explanation));
        }

        logger?.LogInformation(
            "Email disabled (Email:Enabled=false) — skipping send to {To} (subject: {Subject}, attachments: {Attachments})",
            toAddress, subject, attachments.Count);
        return Observable.Return(true);
    }
}
