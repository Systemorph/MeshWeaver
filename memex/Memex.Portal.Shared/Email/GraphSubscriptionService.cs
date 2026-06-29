using System.Reactive.Disposables;
using System.Reactive.Linq;
using MeshWeaver.Blazor.Infrastructure;    // PortalApplication
using MeshWeaver.Data;                       // IWorkspace, GetWorkspace, GetMeshNodeStream
using MeshWeaver.Graph.Configuration;        // GraphSubscriptionNodeType
using MeshWeaver.Mesh;                        // EmailOptions, GraphSubscriptionState, MeshNode
using MeshWeaver.Mesh.Security;               // ImpersonateAsSystem
using MeshWeaver.Mesh.Services;               // IMeshService
using MeshWeaver.Messaging;                   // AccessService
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Email;

/// <summary>
/// Keeps a Microsoft Graph change-notification subscription alive on the mailbox inbox so inbound mail is
/// delivered to <c>/api/email</c>. The subscription id is <b>persisted as a <see cref="GraphSubscriptionState"/>
/// node</b> (<c>Admin/_GraphSubscription/inbox</c>): on startup we read it and <b>renew/reuse</b> the
/// existing subscription rather than creating a new one — so a portal restart no longer leaves a duplicate
/// subscription behind (which would deliver every inbound email more than once). Renewed on a timer
/// (messages subscriptions cap at ~3 days). Self-skips unless <c>Email:Enabled &amp;&amp; Email:InboundEnabled</c>
/// and a <see cref="EmailOptions.WebhookBaseUrl"/> is set.
/// </summary>
public sealed class GraphSubscriptionService(
    IServiceProvider rootServices,
    EmailOptions options,
    GraphMail graphMail,
    IHostApplicationLifetime lifetime,
    ILogger<GraphSubscriptionService>? logger = null) : IHostedService, IDisposable
{
    private static readonly TimeSpan RenewInterval = TimeSpan.FromHours(24);
    private readonly CompositeDisposable subscriptions = new();
    private IServiceScope? scope;
    private IWorkspace? workspace;
    private IMeshService? meshService;
    private AccessService? access;
    private string? subscriptionId;
    private bool nodeExists;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Enabled || !options.InboundEnabled || string.IsNullOrEmpty(options.WebhookBaseUrl))
        {
            logger?.LogInformation("Email inbound disabled — no Graph subscription created");
            return Task.CompletedTask;
        }

        // Defer until the host is fully started: Graph validates the notificationUrl synchronously during
        // subscription creation, so the webhook endpoint must already be listening; and reading the
        // persisted state needs the mesh up. ApplicationStarted covers both.
        var url = $"{options.WebhookBaseUrl.TrimEnd('/')}/api/email";
        lifetime.ApplicationStarted.Register(() => Begin(url));
        return Task.CompletedTask;
    }

    private void Begin(string url)
    {
        try
        {
            scope = rootServices.CreateScope();
            var hub = scope.ServiceProvider.GetRequiredService<PortalApplication>().Hub;
            workspace = hub.GetWorkspace();
            meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
            access = hub.ServiceProvider.GetRequiredService<AccessService>();

            // Read the persisted subscription id so we RENEW the existing one instead of creating another.
            workspace.GetMeshNodeStream(GraphSubscriptionNodeType.InboxPath)
                .Select(n => n?.Content as GraphSubscriptionState)
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(15))
                .Subscribe(
                    state =>
                    {
                        nodeExists = state is not null;
                        subscriptionId = state?.SubscriptionId;
                        CreateOrRenew(url);
                    },
                    _ => CreateOrRenew(url));   // no stored state (or read timed out) → create fresh

            subscriptions.Add(Observable.Interval(RenewInterval).Subscribe(_ => CreateOrRenew(url)));
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "EmailSubscription: failed to start");
        }
    }

    private void CreateOrRenew(string url)
    {
        var expiration = DateTimeOffset.UtcNow.AddDays(2);
        if (subscriptionId is { } id)
            graphMail.RenewSubscription(id, expiration).Subscribe(
                _ => { logger?.LogInformation("EmailSubscription: renewed {Id}", id); Persist(id, url, expiration); },
                ex =>
                {
                    // Stale id (expired / deleted server-side) → forget it and create a fresh one.
                    logger?.LogWarning(ex, "EmailSubscription: renew failed — recreating");
                    subscriptionId = null;
                    Create(url, expiration);
                });
        else
            Create(url, expiration);
    }

    private void Create(string url, DateTimeOffset expiration) =>
        graphMail.CreateInboxSubscription(url, options.SubscriptionClientState, expiration).Subscribe(
            sub =>
            {
                subscriptionId = sub?.Id;
                logger?.LogInformation("EmailSubscription: created {Id} -> {Url}", subscriptionId, url);
                Persist(subscriptionId, url, expiration);
            },
            ex => logger?.LogWarning(ex, "EmailSubscription: create failed for {Url}", url));

    /// <summary>Persist the live subscription id/expiry so the next restart renews it instead of duplicating.</summary>
    private void Persist(string? subId, string url, DateTimeOffset expiration)
    {
        if (meshService is null || access is null || string.IsNullOrEmpty(subId)) return;
        // 🚨 Write to the SAME path the renewal read uses (InboxPath). The old
        // `new MeshNode(NodeType, InboxPath)` set id=NodeType + namespace=InboxPath →
        // path "Admin/_GraphSubscription/inbox/GraphSubscription", so the read of
        // InboxPath NEVER found it → every read NotFound-stormed the missing node (the
        // inbox read/write mismatch). FromPath splits InboxPath into
        // namespace="Admin/_GraphSubscription" + id="inbox" → path == InboxPath.
        var node = MeshNode.FromPath(GraphSubscriptionNodeType.InboxPath) with
        {
            NodeType = GraphSubscriptionNodeType.NodeType,
            Name = "Graph Subscription",
            Content = new GraphSubscriptionState
            {
                SubscriptionId = subId,
                Resource = graphMail.InboxResource,
                NotificationUrl = url,
                ExpiresAt = expiration
            }
        };
        // Idempotent persist: if CreateNode hits an already-existing node (the seeded
        // default, or a prior run whose startup read timed out → nodeExists=false), fall
        // back to UpdateNode so the live SubscriptionId IS saved. Without this the id is
        // lost and the next restart creates a DUPLICATE inbox subscription (mail delivered
        // twice). A genuine failure still surfaces via the inner warning.
        using (access.ImpersonateAsSystem())
            (nodeExists ? meshService.UpdateNode(node) : meshService.CreateNode(node)).Subscribe(
                _ => nodeExists = true,
                _ =>
                {
                    using (access.ImpersonateAsSystem())
                        meshService.UpdateNode(node).Subscribe(
                            _ => nodeExists = true,
                            ex2 => logger?.LogWarning(ex2, "EmailSubscription: failed to persist subscription state"));
                });
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose()
    {
        subscriptions.Dispose();
        scope?.Dispose();
    }
}
