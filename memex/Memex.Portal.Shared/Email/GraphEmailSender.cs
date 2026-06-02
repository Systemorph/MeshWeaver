using System.Reactive.Linq;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using MeshWeaver.Mesh;
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
/// it bridges to the codebase's reactive convention via <c>Observable.FromAsync</c> (same shape
/// as other HttpClient-based outbound integrations such as <c>LinkedInPublisher</c>).</para>
/// </summary>
public sealed class GraphEmailSender : IEmailSender
{
    private readonly EmailOptions _options;
    private readonly ILogger<GraphEmailSender>? _logger;
    private readonly GraphServiceClient _graph;

    public GraphEmailSender(EmailOptions options, ILogger<GraphEmailSender>? logger = null)
    {
        _options = options;
        _logger = logger;

        TokenCredential credential = options.UseManagedIdentity
            ? new DefaultAzureCredential()
            : new ClientSecretCredential(options.TenantId, options.ClientId, options.ClientSecret);

        _graph = new GraphServiceClient(credential, ["https://graph.microsoft.com/.default"]);
    }

    public IObservable<bool> SendEmail(string toAddress, string subject, string htmlBody) =>
        Observable.FromAsync(ct => SendAsync(toAddress, subject, htmlBody, ct));

    private async Task<bool> SendAsync(string toAddress, string subject, string htmlBody, CancellationToken ct)
    {
        var body = new SendMailPostRequestBody
        {
            Message = new Message
            {
                Subject = subject,
                Body = new ItemBody { ContentType = BodyType.Html, Content = htmlBody },
                ToRecipients =
                [
                    new Recipient { EmailAddress = new EmailAddress { Address = toAddress } }
                ]
            },
            SaveToSentItems = false
        };

        await _graph.Users[_options.NoReplyAddress].SendMail.PostAsync(body, cancellationToken: ct);
        _logger?.LogInformation("Sent email to {To} (subject: {Subject}) as {From}",
            toAddress, subject, _options.NoReplyAddress);
        return true;
    }
}
