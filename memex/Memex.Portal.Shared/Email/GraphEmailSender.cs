using System.Linq;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Threading;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.SendMail;

namespace Memex.Portal.Shared.Email;

/// <summary>
/// Sends email via Microsoft Graph <c>/users/{noReply}/sendMail</c> using the <c>Mail.Send</c>
/// application permission. Credentials come from <see cref="EmailOptions"/>: a managed identity
/// (<c>DefaultAzureCredential</c>) in production, or a client secret for self-host.
///
/// <para>The Graph call is genuinely async; since this sender is not invoked on a hub scheduler
/// it bridges to the codebase's reactive convention via a bounded HTTP <see cref="IIoPool"/>
/// (<c>_http.Run(...)</c>) rather than a bare <c>Observable.FromAsync</c> — the latter deadlocks
/// under a blocking subscriber (see <c>Doc/Architecture/ControlledIoPooling.md</c>).</para>
/// </summary>
public sealed class GraphEmailSender : IEmailSender, IDisposable
{
    // Bound to a sane cap so a burst of outbound sends can't open unbounded Graph calls.
    private const int HttpConcurrency = 8;

    private readonly EmailOptions _options;
    private readonly ILogger<GraphEmailSender>? _logger;
    private readonly GraphServiceClient _graph;

    // Dedicated bounded HTTP pool, ALWAYS created fresh and owned by this instance — never resolved
    // from the mesh-scoped IoPoolRegistry. The portal builds this sender from its OWN DI container
    // while activating hosted services; reaching across into the mesh hub's ServiceProvider at that
    // moment races that provider's internal service realization (a documented NRE crash-loop — see
    // GraphMail). A self-owned pool always resolves and is disposed with this singleton.
    private readonly IoPool _http = new(HttpConcurrency);

    public GraphEmailSender(EmailOptions options, ILogger<GraphEmailSender>? logger = null)
    {
        _options = options;
        _logger = logger;

        // Azure SDK credentials are server-only; CA1416's browser-reachability
        // analysis doesn't apply (this sender never runs in WASM/browser).
#pragma warning disable CA1416
        TokenCredential credential = options.UseManagedIdentity
            ? new DefaultAzureCredential()
            : new ClientSecretCredential(options.TenantId, options.ClientId, options.ClientSecret);
#pragma warning restore CA1416

        _graph = new GraphServiceClient(credential, ["https://graph.microsoft.com/.default"]);
    }

    public void Dispose() => _http.Dispose();

    public IObservable<bool> SendEmail(string toAddress, string subject, string htmlBody) =>
        SendEmail(toAddress, subject, htmlBody, []);

    public IObservable<bool> SendEmail(
        string toAddress, string subject, string htmlBody, IReadOnlyCollection<EmailAttachment> attachments) =>
        _http.Run(ct => SendAsync(toAddress, subject, htmlBody, attachments, ct));

    private async Task<bool> SendAsync(
        string toAddress, string subject, string htmlBody,
        IReadOnlyCollection<EmailAttachment> attachments, CancellationToken ct)
    {
        var message = new Message
        {
            Subject = subject,
            Body = new ItemBody { ContentType = BodyType.Html, Content = htmlBody },
            ToRecipients =
            [
                new Recipient { EmailAddress = new EmailAddress { Address = toAddress } }
            ]
        };

        if (attachments.Count > 0)
        {
            // Graph carries small attachments (< 3 MB total) inline as base64 fileAttachments on the
            // message; ContentBytes takes the raw byte[] and the SDK base64-encodes it on the wire.
            message.Attachments =
            [
                .. attachments.Select(a => (Attachment)new FileAttachment
                {
                    OdataType = "#microsoft.graph.fileAttachment",
                    Name = a.FileName,
                    ContentType = a.MimeType,
                    ContentBytes = a.Content
                })
            ];
        }

        var body = new SendMailPostRequestBody { Message = message, SaveToSentItems = false };

        await _graph.Users[_options.MailboxAddress].SendMail.PostAsync(body, cancellationToken: ct).ConfigureAwait(false);
        _logger?.LogInformation("Sent email to {To} (subject: {Subject}, attachments: {Attachments}) as {From}",
            toAddress, subject, attachments.Count, _options.MailboxAddress);
        return true;
    }
}
