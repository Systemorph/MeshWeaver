using System.Reactive;
using System.Reactive.Linq;
using Azure.Core;
using Azure.Identity;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Threading;
using Microsoft.Graph;
using Microsoft.Graph.Models;

namespace Memex.Portal.Shared.Email;

/// <summary>
/// Reactive wrapper over a Microsoft Graph client scoped to the portal mailbox, for the inbound side
/// (read messages, mark read, manage the inbox change-notification subscription). Outbound send lives
/// in <see cref="GraphEmailSender"/>. Both build the same app-only credential from <see cref="EmailOptions"/>.
///
/// <para>Every method returns a cold <see cref="IObservable{T}"/>; the genuine HTTP I/O runs through the
/// shared <c>Http</c> <see cref="IIoPool"/> — off the hub scheduler and bounded — never a bare
/// <c>await</c> on the calling thread (see <c>Doc/Architecture/ControlledIoPooling.md</c>).</para>
/// </summary>
public sealed class GraphMail
{
    private readonly EmailOptions _options;
    private readonly Lazy<GraphServiceClient> _graph;
    private readonly IIoPool _http;

    public GraphMail(EmailOptions options, IoPoolRegistry? ioPools = null)
    {
        _options = options;
        // Built lazily so the type is constructible without valid creds (unit tests that exercise
        // only the routing never touch Graph; the credential would otherwise throw on empty values).
        _graph = new Lazy<GraphServiceClient>(() =>
        {
            TokenCredential credential = options.UseManagedIdentity
                ? new DefaultAzureCredential()
                : new ClientSecretCredential(options.TenantId, options.ClientId, options.ClientSecret);
            return new GraphServiceClient(credential, ["https://graph.microsoft.com/.default"]);
        });
        _http = ioPools?.Get(IoPoolNames.Http) ?? IoPool.Unbounded;
    }

    private GraphServiceClient Client => _graph.Value;

    /// <summary>The mailbox the portal sends/receives as (e.g. <c>memex@systemorph.com</c>).</summary>
    public string Mailbox => _options.MailboxAddress;

    /// <summary>The Graph resource path for the mailbox inbox messages (app-only — no <c>/me</c>).</summary>
    public string InboxResource => $"users/{_options.MailboxAddress}/mailFolders('inbox')/messages";

    public IObservable<Message?> GetMessage(string messageId) =>
        _http.Invoke(ct => Client.Users[_options.MailboxAddress].Messages[messageId].GetAsync(r =>
            r.QueryParameters.Select =
                ["from", "subject", "body", "conversationId", "internetMessageId", "toRecipients", "isRead"], ct));

    public IObservable<Unit> MarkRead(string messageId) =>
        _http.Invoke(async ct =>
        {
            await Client.Users[_options.MailboxAddress].Messages[messageId]
                .PatchAsync(new Message { IsRead = true }, cancellationToken: ct);
            return Unit.Default;
        });

    public IObservable<Subscription?> CreateInboxSubscription(
        string notificationUrl, string clientState, DateTimeOffset expiration) =>
        _http.Invoke(ct => Client.Subscriptions.PostAsync(new Subscription
        {
            ChangeType = "created",
            NotificationUrl = notificationUrl,
            Resource = InboxResource,
            ExpirationDateTime = expiration,
            ClientState = clientState
        }, cancellationToken: ct));

    public IObservable<SubscriptionCollectionResponse?> ListSubscriptions() =>
        _http.Invoke(ct => Client.Subscriptions.GetAsync(cancellationToken: ct));

    public IObservable<Unit> RenewSubscription(string subscriptionId, DateTimeOffset expiration) =>
        _http.Invoke(async ct =>
        {
            await Client.Subscriptions[subscriptionId]
                .PatchAsync(new Subscription { ExpirationDateTime = expiration }, cancellationToken: ct);
            return Unit.Default;
        });

    public IObservable<Unit> DeleteSubscription(string subscriptionId) =>
        _http.Invoke(async ct =>
        {
            await Client.Subscriptions[subscriptionId].DeleteAsync(cancellationToken: ct);
            return Unit.Default;
        });
}
