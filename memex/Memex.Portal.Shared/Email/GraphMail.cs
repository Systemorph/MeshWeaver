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
public sealed class GraphMail : IDisposable
{
    // Bound to a sane cap so a burst of inbound notifications can't open unbounded Graph calls.
    private const int HttpConcurrency = 8;

    private readonly EmailOptions _options;
    private readonly Lazy<GraphServiceClient> _graph;

    // Dedicated bounded HTTP pool, ALWAYS created fresh and owned by this instance — never resolved
    // from the mesh-scoped IoPoolRegistry. The portal builds GraphMail from its OWN DI container
    // while activating hosted services; reaching across into the mesh hub's ServiceProvider at that
    // moment races that provider's internal service realization and surfaced as an NRE inside
    // ConcurrentDictionary.GetOrAdd, crash-looping the portal. A self-owned pool always resolves and
    // is disposed with this singleton when the container tears down.
    private readonly IoPool _http = new(HttpConcurrency);

    public GraphMail(EmailOptions options)
    {
        _options = options;
        // Built lazily so the type is constructible without valid creds (unit tests that exercise
        // only the routing never touch Graph; the credential would otherwise throw on empty values).
        _graph = new Lazy<GraphServiceClient>(() =>
        {
            // Azure SDK credentials are server-only; CA1416's browser-reachability
            // analysis doesn't apply (this client never runs in WASM/browser).
#pragma warning disable CA1416
            TokenCredential credential = options.UseManagedIdentity
                ? new DefaultAzureCredential()
                : new ClientSecretCredential(options.TenantId, options.ClientId, options.ClientSecret);
#pragma warning restore CA1416
            return new GraphServiceClient(credential, ["https://graph.microsoft.com/.default"]);
        });
    }

    public void Dispose() => _http.Dispose();

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
