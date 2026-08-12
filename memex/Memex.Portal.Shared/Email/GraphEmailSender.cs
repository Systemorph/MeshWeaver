using System.Linq;
using System.Reactive.Linq;
using Azure.Core;
using Azure.Identity;
using Memex.Portal.Shared.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Threading;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.SendMail;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;

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

    // Resolved PER SEND rather than injected: IEaGraphAuth is a typed HttpClient (transient), and
    // holding one on this singleton would pin a single HttpMessageHandler for the process lifetime.
    private readonly IServiceProvider _services;

    public GraphEmailSender(
        EmailOptions options, IServiceProvider services, ILogger<GraphEmailSender>? logger = null)
    {
        _options = options;
        _services = services;
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
        SendEmail(toAddress, subject, htmlBody, attachments, EmailDelivery.AsSharedMailbox);

    public IObservable<bool> SendEmail(
        string toAddress, string subject, string htmlBody,
        IReadOnlyCollection<EmailAttachment> attachments, EmailDelivery delivery) =>
        _http.Run(ct => SendAsync(toAddress, subject, htmlBody, attachments, delivery, ct));

    /// <summary>
    /// True when the user has connected their mailbox. The stored credential is minted with
    /// <see cref="EaGraphAuth.Scopes"/>, which already includes delegated <c>Mail.Send</c> — so
    /// "connected" and "can send as themselves" are the same condition, with no extra consent step.
    /// </summary>
    public IObservable<bool> CanSendAsUser(string userObjectId) =>
        string.IsNullOrEmpty(userObjectId)
            ? Observable.Return(false)
            : _http.Run(async ct =>
            {
                var auth = _services.GetService<IEaGraphAuth>();
                if (auth is null || !auth.IsConfigured) return false;
                return await auth.IsConnectedAsync(userObjectId, ct).ConfigureAwait(false);
            });

    private async Task<bool> SendAsync(
        string toAddress, string subject, string htmlBody,
        IReadOnlyCollection<EmailAttachment> attachments, EmailDelivery delivery, CancellationToken ct)
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
                    ContentBytes = a.Content,
                    // An INLINE part is drawn where the body's <img src="cid:…"> points, instead
                    // of being listed as a file to open. Both fields are required: IsInline alone
                    // still lists it, and a ContentId alone is never matched to the img.
                    ContentId = a.ContentId,
                    IsInline = a.IsInline
                })
            ];
        }

        // Replies must reach a HUMAN. When the message goes out from the shared mailbox on a
        // person's behalf, the From is generic, so without this a client's reply lands in a system
        // inbox nobody answers from.
        if (!string.IsNullOrWhiteSpace(delivery.ReplyToAddress))
            message.ReplyTo =
            [
                new Recipient { EmailAddress = new EmailAddress { Address = delivery.ReplyToAddress } }
            ];

        if (delivery.IsUserIdentity)
            return await SendAsUserAsync(message, toAddress, subject, attachments.Count, delivery, ct)
                .ConfigureAwait(false);

        var body = new SendMailPostRequestBody { Message = message, SaveToSentItems = false };

        await _graph.Users[_options.MailboxAddress].SendMail.PostAsync(body, cancellationToken: ct).ConfigureAwait(false);
        _logger?.LogInformation("Sent email to {To} (subject: {Subject}, attachments: {Attachments}) as {From}",
            toAddress, subject, attachments.Count, _options.MailboxAddress);
        return true;
    }

    /// <summary>
    /// Sends the message as the USER, with their own delegated token — Graph <c>/me/sendMail</c>.
    ///
    /// <para>The recipient sees the person, and <c>SaveToSentItems</c> is <b>true</b> here (unlike
    /// the system path): a message a person sent belongs in their own Sent Items, which is also
    /// where they will look for it.</para>
    ///
    /// <para>Failure is NOT silently downgraded to a shared-mailbox send. The caller asked to send
    /// as this person; quietly sending as something else is exactly the surprise this feature
    /// exists to prevent, so it surfaces and the UI can offer to reconnect.</para>
    /// </summary>
    private async Task<bool> SendAsUserAsync(
        Message message, string toAddress, string subject, int attachmentCount,
        EmailDelivery delivery, CancellationToken ct)
    {
        var auth = _services.GetService<IEaGraphAuth>()
            ?? throw new InvalidOperationException(
                "Sending as the signed-in user needs the delegated Microsoft 365 connection, which is "
                + "not configured on this deployment.");

        var token = await auth.GetAccessTokenAsync(delivery.SendAsUserObjectId!, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException(
                "This user has not connected their Microsoft 365 mailbox, so the message cannot be sent "
                + "in their name. Connect Microsoft 365, or send from the shared mailbox instead.");

        // A per-send client bound to THIS user's token. Reusing the app-credential _graph would send
        // as the shared mailbox — the precise mistake we are preventing.
        using var adapter = new HttpClientRequestAdapter(new StaticTokenAuthenticationProvider(token));
        var graph = new GraphServiceClient(adapter);

        // The user's own mailbox keeps the record of what they sent. (/me/sendMail has its own
        // generated request-body type — distinct from the /users/{id}/sendMail one above.)
        var body = new Microsoft.Graph.Me.SendMail.SendMailPostRequestBody
        {
            Message = message,
            SaveToSentItems = true
        };
        await graph.Me.SendMail.PostAsync(body, cancellationToken: ct).ConfigureAwait(false);

        _logger?.LogInformation(
            "Sent email to {To} (subject: {Subject}, attachments: {Attachments}) as the signed-in user {User}",
            toAddress, subject, attachmentCount, delivery.SendAsUserObjectId);
        return true;
    }
}

/// <summary>
/// Presents an already-minted delegated access token to Graph. The token is obtained and rotated by
/// <see cref="EaGraphAuth"/> (which holds the encrypted refresh token); this only carries it onto
/// the request.
/// </summary>
internal sealed class StaticTokenAuthenticationProvider(string accessToken) : IAuthenticationProvider
{
    public Task AuthenticateRequestAsync(
        RequestInformation request,
        Dictionary<string, object>? additionalAuthenticationContext = null,
        CancellationToken cancellationToken = default)
    {
        request.Headers.Add("Authorization", $"Bearer {accessToken}");
        return Task.CompletedTask;
    }
}
