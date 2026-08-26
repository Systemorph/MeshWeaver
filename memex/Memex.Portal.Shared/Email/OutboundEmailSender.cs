using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Hosting.AspNetCore.Portal;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Email;

/// <summary>
/// Mesh-driven outbound sender — <b>no in-memory state</b>. The <c>Email Router</c> agent emits its
/// reply as an outbound <see cref="MeshWeaver.Mesh.Email"/> node (<c>Direction=Outbound, Status=New</c>)
/// in the parent email's namespace; this single hosted service watches for those via
/// <see cref="IMeshQueryCore"/> and hands each to its serialized <see cref="OutboundSendQueue"/>,
/// which re-reads the mail's authoritative state, claims it (New → Sending), sends it through
/// <see cref="IEmailSender"/>, and flips it to <see cref="EmailStatus.Sent"/> (or
/// <see cref="EmailStatus.Failed"/>). Restart-safety lives in the node's status; DEDUP lives in
/// the queue's serialize-then-re-read gate — the watch legitimately emits the same New snapshot
/// more than once, and a plain status write is not a claim (the double-delivery this fixed is
/// documented on the queue).
///
/// <para>Reactive; the only Task boundary is the <see cref="IHostedService"/> contract. Self-skips
/// unless <c>Email:Enabled</c>.</para>
/// </summary>
public sealed class OutboundEmailSender(
    IServiceProvider rootServices,
    IHostApplicationLifetime lifetime,
    EmailOptions options,
    ILogger<OutboundEmailSender>? logger = null) : IHostedService, IDisposable
{
    /// <summary>
    /// The live watch query. 🚨 It must NOT match <c>content.status:New</c> POSITIVELY:
    /// <see cref="EmailStatus.New"/> is the enum DEFAULT (0) and the serializer OMITS it from the
    /// stored JSON, so that filter never matches a freshly queued email — the exact trap
    /// <see cref="InvitationEmailSender"/> documents for <c>content.status:Pending</c>, and the
    /// reason the Store contact form's notification sat queued forever on memex (2026-08-12: the
    /// query with the positive status clause returned 0 rows, without it 1).
    ///
    /// <para>NEGATIONS are the shape that both avoids the trap and keeps the live set BOUNDED:
    /// a negation on an omitted field never excludes (verified live — <c>-content.status:New</c>
    /// still returned the status-omitted email), so New-queued mail always matches, while
    /// explicitly stamped <c>Sending</c>/<c>Sent</c>/<c>Failed</c> mail drops out of the set as it
    /// is processed instead of accumulating forever (the Copilot review's growth concern on the
    /// unfiltered form). Status is additionally re-checked IN CODE — serialized and against the
    /// AUTHORITATIVE node stream — by <see cref="OutboundSendQueue"/>, which is what actually
    /// guards double-send (duplicate emissions of the same New snapshot are a documented property
    /// of the change feed). <c>content.direction:Outbound</c> is a safe
    /// positive match: Outbound is not the default, so it always serializes — see
    /// <c>OutboundEmailWatchQueryTest</c>.</para>
    /// </summary>
    public const string WatchQuery =
        $"nodeType:{EmailNodeType.NodeType} content.direction:Outbound "
        + "-content.status:Sending -content.status:Sent -content.status:Failed";

    private readonly CompositeDisposable subscriptions = new();
    private IServiceScope? scope;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Enabled)
        {
            logger?.LogInformation("Email disabled — OutboundEmailSender idle");
            return Task.CompletedTask;
        }

        // 🚨 Enabled, but is there anything that can actually DELIVER? Asked HERE, in StartAsync,
        // for two reasons. It is inside the startup window, so StartupErrorNotifier folds the
        // refusal into the Admin bell that reports a degraded boot — whereas ApplicationStarted
        // callbacks run after that buffer has already drained. And refusing before the watch is
        // even armed is what keeps queued mail at New: a watcher that starts and then declines
        // per-item has already claimed (New → Sending) the first one. See EmailDeliveryGuard.
        if (EmailDeliveryGuard.RefuseToStart(rootServices, options, logger, nameof(OutboundEmailSender)))
            return Task.CompletedTask;

        // Defer ALL mesh access until the host is fully started. The Orleans client and the mesh
        // hub come up as hosted services too; touching the hub here (or constructing
        // PortalApplication, whose ctor registers an Orleans stream) races that startup and NREs
        // in OrleansRoutingService.RegisterStream / PersistentStreamProvider. ApplicationStarted
        // fires once every hosted service (Orleans included) has started, so the mesh is ready.
        lifetime.ApplicationStarted.Register(BeginWatching);
        return Task.CompletedTask;
    }

    private void BeginWatching()
    {
        try
        {
            // Resolve a fresh PortalApplication in its own scope now that the mesh is up — the
            // instance DI built at host-construction time may have captured a not-yet-ready hub.
            scope = rootServices.CreateScope();
            // Portal hub when the Blazor shell registered one; the mesh root hub otherwise.
            var hub = scope.ServiceProvider.GetService<PortalApplication>()?.Hub
                      ?? scope.ServiceProvider.GetRequiredService<IMessageHub>();
            var sp = hub.ServiceProvider;
            var query = sp.GetRequiredService<IMeshQueryCore>();
            var meshService = sp.GetRequiredService<IMeshService>();
            var accessService = sp.GetRequiredService<AccessService>();
            var emailSender = sp.GetRequiredService<IEmailSender>();
            var jsonOptions = hub.JsonSerializerOptions;

            // The serialized send queue: re-reads each mail's CURRENT state through the shared
            // per-node stream handle (authoritative — never the eventually-consistent query) before
            // claiming and sending, so duplicate emissions of the same New snapshot send ONCE.
            var sendQueue = new OutboundSendQueue(
                readCurrent: path => hub.GetMeshNodeStream(path)
                    .Where(n => n is not null)
                    .Select(n => EmailOf(n, jsonOptions)),
                writeStatus: (node, current, status) =>
                    SetStatus(node, current, status, meshService, accessService),
                send: email =>
                {
                    var subject = email.Subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase)
                        ? email.Subject : $"Re: {email.Subject}";
                    return emailSender.SendEmail(email.To!, subject, email.Body);
                },
                logger: logger);
            subscriptions.Add(sendQueue);

            // Live query: any outbound mail. Emits the current set on change; the pre-screen keeps
            // the queue to plausible candidates, the queue's own re-read gate decides the send.
            subscriptions.Add(query
                .Query<MeshNode>(MeshQueryRequest.FromQuery(WatchQuery), jsonOptions)
                .Select(change => change.Items)
                .Subscribe(
                    items =>
                    {
                        foreach (var node in items)
                        {
                            var email = EmailOf(node, jsonOptions);
                            if (email is null
                                || email.Direction != EmailDirection.Outbound
                                || email.Status != EmailStatus.New)
                                continue;
                            if (string.IsNullOrEmpty(email.To))
                            {
                                logger?.LogWarning(
                                    "OutboundEmailSender: outbound {Path} has no recipient — marking Failed",
                                    node.Path);
                                SetStatus(node, email, EmailStatus.Failed, meshService, accessService)
                                    .Subscribe(_ => { }, _ => { });
                                continue;
                            }
                            sendQueue.Enqueue(node, email);
                        }
                    },
                    ex => logger?.LogWarning(ex, "OutboundEmailSender: query failed")));
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "OutboundEmailSender: failed to start watching outbound mail");
        }
    }

    private static IObservable<MeshNode> SetStatus(
        MeshNode node, MeshWeaver.Mesh.Email current, EmailStatus to,
        IMeshService meshService, AccessService accessService) =>
        Observable.Using(
            () => accessService.ImpersonateAsSystem(),
            _ => meshService.UpdateNode(node with { Content = current with { Status = to } }));

    private static MeshWeaver.Mesh.Email? EmailOf(MeshNode n, JsonSerializerOptions? options) => n.Content switch
    {
        MeshWeaver.Mesh.Email e => e,
        JsonElement je => Safe(je, options),
        _ => null
    };

    private static MeshWeaver.Mesh.Email? Safe(JsonElement je, JsonSerializerOptions? options)
    {
        try { return JsonSerializer.Deserialize<MeshWeaver.Mesh.Email>(je.GetRawText(), options); }
        catch { return null; }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public void Dispose() => subscriptions.Dispose();
}
