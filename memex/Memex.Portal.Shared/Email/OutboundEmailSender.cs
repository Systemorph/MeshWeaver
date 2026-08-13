using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Blazor.Infrastructure;
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
/// <see cref="IMeshQueryCore"/>, claims each (New → Sending, the optimistic guard against double-send),
/// sends it through <see cref="IEmailSender"/>, and flips it to <see cref="EmailStatus.Sent"/> (or
/// <see cref="EmailStatus.Failed"/>). Dedup + restart-safety live entirely in the node's status.
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
    /// unfiltered form). Status is additionally re-checked IN CODE by <c>Send</c>, and the
    /// New → Sending claim guards double-send. <c>content.direction:Outbound</c> is a safe
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
            var hub = scope.ServiceProvider.GetRequiredService<PortalApplication>().Hub;
            var sp = hub.ServiceProvider;
            var query = sp.GetRequiredService<IMeshQueryCore>();
            var meshService = sp.GetRequiredService<IMeshService>();
            var accessService = sp.GetRequiredService<AccessService>();
            var emailSender = sp.GetRequiredService<IEmailSender>();
            var jsonOptions = hub.JsonSerializerOptions;

            // Live query: any outbound mail. Emits the current set on change; Send filters to
            // New and claims (New → Sending) before dispatching, so already-sent nodes are no-ops.
            subscriptions.Add(query
                .Query<MeshNode>(MeshQueryRequest.FromQuery(WatchQuery), jsonOptions)
                .Select(change => change.Items)
                .Subscribe(
                    items =>
                    {
                        foreach (var node in items)
                            Send(node, meshService, accessService, emailSender, jsonOptions);
                    },
                    ex => logger?.LogWarning(ex, "OutboundEmailSender: query failed")));
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "OutboundEmailSender: failed to start watching outbound mail");
        }
    }

    private void Send(
        MeshNode node, IMeshService meshService, AccessService accessService,
        IEmailSender emailSender, JsonSerializerOptions jsonOptions)
    {
        var email = EmailOf(node, jsonOptions);
        if (email is null || email.Direction != EmailDirection.Outbound || email.Status != EmailStatus.New)
            return;
        if (string.IsNullOrEmpty(email.To))
        {
            logger?.LogWarning("OutboundEmailSender: outbound {Path} has no recipient — marking Failed", node.Path);
            SetStatus(node, email, EmailStatus.Failed, meshService, accessService).Subscribe(_ => { }, _ => { });
            return;
        }

        // Claim: New → Sending (only if still New). The CAS lives in SetStatus's lambda, so a duplicate
        // emission that already flipped it is a no-op.
        SetStatus(node, email, EmailStatus.Sending, meshService, accessService)
            .SelectMany(claimed =>
            {
                // SetStatus returns the unchanged node when the CAS failed (already claimed) — skip.
                if ((EmailOf(claimed, jsonOptions)?.Status) != EmailStatus.Sending)
                    return Observable.Empty<bool>();
                var subject = email.Subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase)
                    ? email.Subject : $"Re: {email.Subject}";
                return emailSender.SendEmail(email.To!, subject, email.Body)
                    .SelectMany(ok => SetStatus(claimed, EmailOf(claimed, jsonOptions)!,
                        ok ? EmailStatus.Sent : EmailStatus.Failed, meshService, accessService).Select(_ => ok));
            })
            .Subscribe(
                ok => logger?.LogInformation("OutboundEmailSender: {Path} → {To} sent={Sent}", node.Path, email.To, ok),
                ex =>
                {
                    logger?.LogWarning(ex, "OutboundEmailSender: send failed for {Path}", node.Path);
                    SetStatus(node, email, EmailStatus.Failed, meshService, accessService).Subscribe(_ => { }, _ => { });
                });
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
