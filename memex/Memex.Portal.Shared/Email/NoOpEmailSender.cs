using System.Reactive.Linq;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Email;

/// <summary>
/// No-op <see cref="IEmailSender"/> registered when <c>Email:Enabled</c> is false (local dev,
/// tests, deployments without M365 creds). Logs the would-be send and reports success so
/// callers' reactive chains complete normally without any mail leaving the process.
/// </summary>
public sealed class NoOpEmailSender(ILogger<NoOpEmailSender>? logger = null) : IEmailSender
{
    public IObservable<bool> SendEmail(string toAddress, string subject, string htmlBody)
        => SendEmail(toAddress, subject, htmlBody, []);

    public IObservable<bool> SendEmail(
        string toAddress, string subject, string htmlBody, IReadOnlyCollection<EmailAttachment> attachments)
    {
        logger?.LogInformation(
            "Email disabled (Email:Enabled=false) — skipping send to {To} (subject: {Subject}, attachments: {Attachments})",
            toAddress, subject, attachments.Count);
        return Observable.Return(true);
    }
}
