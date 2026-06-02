using System.Reactive.Disposables;
using System.Reactive.Linq;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Email;

/// <summary>
/// Keeps a Microsoft Graph change-notification subscription alive on the mailbox inbox so inbound mail
/// is delivered to <c>/api/email</c>. Creates it on startup and renews on a timer (messages
/// subscriptions cap at ~3 days); deletes it on shutdown. Self-skips unless
/// <c>Email:Enabled &amp;&amp; Email:InboundEnabled</c> and a <see cref="EmailOptions.WebhookBaseUrl"/> is set.
///
/// <para>Reactive end-to-end: the Graph calls are <see cref="GraphMail"/>'s pooled observables; this
/// only Subscribes (the <see cref="IHostedService"/> boundary is Task-based by contract).</para>
/// </summary>
public sealed class GraphSubscriptionService(
    EmailOptions options,
    GraphMail graphMail,
    ILogger<GraphSubscriptionService>? logger = null) : IHostedService, IDisposable
{
    private static readonly TimeSpan RenewInterval = TimeSpan.FromHours(24);
    private readonly CompositeDisposable subscriptions = new();
    private string? subscriptionId;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Enabled || !options.InboundEnabled || string.IsNullOrEmpty(options.WebhookBaseUrl))
        {
            logger?.LogInformation("Email inbound disabled — no Graph subscription created");
            return Task.CompletedTask;
        }

        var url = $"{options.WebhookBaseUrl.TrimEnd('/')}/api/email";
        CreateOrRenew(url);
        subscriptions.Add(Observable.Interval(RenewInterval).Subscribe(_ => CreateOrRenew(url)));
        return Task.CompletedTask;
    }

    private void CreateOrRenew(string url)
    {
        var expiration = DateTimeOffset.UtcNow.AddDays(2);
        if (subscriptionId is { } id)
            graphMail.RenewSubscription(id, expiration).Subscribe(
                _ => logger?.LogInformation("EmailSubscription: renewed {Id}", id),
                ex => { logger?.LogWarning(ex, "EmailSubscription: renew failed — recreating"); subscriptionId = null; Create(url, expiration); });
        else
            Create(url, expiration);
    }

    private void Create(string url, DateTimeOffset expiration) =>
        graphMail.CreateInboxSubscription(url, options.SubscriptionClientState, expiration).Subscribe(
            sub => { subscriptionId = sub?.Id; logger?.LogInformation("EmailSubscription: created {Id} → {Url}", subscriptionId, url); },
            ex => logger?.LogWarning(ex, "EmailSubscription: create failed for {Url}", url));

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (subscriptionId is { } id)
            graphMail.DeleteSubscription(id).Subscribe(_ => { }, _ => { });
        return Task.CompletedTask;
    }

    public void Dispose() => subscriptions.Dispose();
}
