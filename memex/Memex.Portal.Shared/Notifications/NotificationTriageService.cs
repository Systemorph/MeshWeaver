using System.Collections.Concurrent;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.AI;                       // StartThread
using MeshWeaver.Blazor.Infrastructure;    // PortalApplication
using MeshWeaver.Graph.Configuration;      // Notification* NodeType segment consts
using MeshWeaver.Mesh;                      // Notification, MeshNode
using MeshWeaver.Mesh.Security;             // ImpersonateAsSystem
using MeshWeaver.Mesh.Services;             // IMeshQueryCore, MeshQueryRequest
using MeshWeaver.Messaging;                 // IMessageHub, AccessService
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Notifications;

/// <summary>
/// Watches for new in-app <see cref="Notification"/>s and, <b>only for recipients who authored routing
/// rules</b>, invokes the cheap <c>Notification Triage</c> agent to decide whether to ALSO escalate the
/// notification to that recipient's other channels (email today, Teams next). The in-app bell is the
/// always-on default; this service never duplicates it — it only escalates per the recipient's
/// <see cref="NotificationRule"/>s.
///
/// <para>Cost/safety guards: deferred to <c>ApplicationStarted</c> (mesh must be up); only notifications
/// created <i>after</i> startup are considered (no back-routing history, no double-route across restart);
/// each is processed once (instance dedup set); the triage agent is invoked <i>only</i> when the recipient
/// has at least one rule, so it is free for everyone else; all failures are logged, never fatal.</para>
///
/// <para><b>Scope note (v1):</b> the notification watch is mesh-wide and the dedup set is unbounded over the
/// process lifetime — fine for current volumes, but scope/bounding is the obvious next hardening step before
/// relying on this at scale.</para>
/// </summary>
public sealed class NotificationTriageService(
    IServiceProvider rootServices,
    IHostApplicationLifetime lifetime,
    ILogger<NotificationTriageService>? logger = null) : IHostedService, IDisposable
{
    private const string TriageAgent = "NotificationTriage";

    private readonly CompositeDisposable subscriptions = new();
    private readonly ConcurrentDictionary<string, byte> processed = new();   // instance, not static
    private IServiceScope? scope;
    private DateTimeOffset startedAt;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lifetime.ApplicationStarted.Register(Begin);
        return Task.CompletedTask;
    }

    private void Begin()
    {
        try
        {
            startedAt = DateTimeOffset.UtcNow;
            scope = rootServices.CreateScope();
            var hub = scope.ServiceProvider.GetRequiredService<PortalApplication>().Hub;
            var sp = hub.ServiceProvider;
            var query = sp.GetRequiredService<IMeshQueryCore>();
            var access = sp.GetRequiredService<AccessService>();
            var jsonOptions = hub.JsonSerializerOptions;

            subscriptions.Add(query
                .Query<MeshNode>(MeshQueryRequest.FromQuery(
                    $"nodeType:{NotificationNodeType.NodeType}"), jsonOptions)
                .Select(change => change.Items)
                .Subscribe(
                    items =>
                    {
                        foreach (var node in items)
                            TryRoute(node, hub, query, access, jsonOptions);
                    },
                    ex => logger?.LogWarning(ex, "NotificationTriage: notification query failed")));
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "NotificationTriage: failed to start");
        }
    }

    private void TryRoute(
        MeshNode node, IMessageHub hub, IMeshQueryCore query, AccessService access, JsonSerializerOptions jsonOptions)
    {
        if (string.IsNullOrEmpty(node.Path)) return;
        var notification = NotificationOf(node, jsonOptions);
        if (notification is null) return;

        // Only notifications raised after we started: avoids back-routing the whole history on first run
        // and avoids double-routing across a restart (startedAt resets, older ones are skipped).
        if (notification.CreatedAt <= startedAt) return;
        // Process each notification exactly once even though the live query re-emits the full set on change.
        if (!processed.TryAdd(node.Path, 0)) return;

        var recipient = node.Path.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrEmpty(recipient)) return;

        // Cost gate: invoke the (cheap) triage agent ONLY when the recipient actually authored rules.
        // No rules → the in-app bell default stands, and we never spend a model call.
        query.Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"nodeType:{NotificationRuleNodeType.NodeType} " +
                $"namespace:{recipient}/{NotificationRuleNodeType.UserSegment} limit:1"), jsonOptions)
            .Select(change => change.Items)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(10))
            .Subscribe(
                rules =>
                {
                    if (rules.Count == 0) return;   // no rules → in-app only (default), nothing to do
                    Escalate(hub, access, recipient, node.Path, notification);
                },
                _ => { /* timeout / error: leave at the in-app default */ });
    }

    private void Escalate(
        IMessageHub hub, AccessService access, string recipient, string notificationPath, Notification n)
    {
        var prompt =
            $"A new notification for user '{recipient}':\n" +
            $"Title: {n.Title}\n" +
            $"Message: {n.Message}\n" +
            $"Type: {n.NotificationType}\n" +
            $"From: {n.CreatedBy}\n" +
            $"Notification node: {notificationPath}\n" +
            $"Related node: {n.TargetNodePath}\n\n" +
            "The in-app bell notification is already shown. Per this recipient's NotificationRules and " +
            "NotificationChannels, decide whether to ALSO escalate to email (or Teams) and create the " +
            "delivery node(s). If their rules don't call for escalation, do nothing.";
        try
        {
            using (access.ImpersonateAsSystem())
                hub.StartThread(
                    namespacePath: $"{recipient}/_Triage",
                    userText: prompt,
                    agentName: TriageAgent,
                    mainNode: notificationPath,
                    contextPath: notificationPath,
                    createdBy: "system",
                    onError: err => logger?.LogWarning("NotificationTriage: StartThread failed: {Err}", err));
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "NotificationTriage: escalation failed for {Path}", notificationPath);
        }
    }

    private static Notification? NotificationOf(MeshNode node, JsonSerializerOptions opts) => node.Content switch
    {
        Notification x => x,
        JsonElement je => Safe(je, opts),
        _ => null
    };

    private static Notification? Safe(JsonElement je, JsonSerializerOptions opts)
    {
        try { return JsonSerializer.Deserialize<Notification>(je.GetRawText(), opts); }
        catch { return null; }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose()
    {
        subscriptions.Dispose();
        scope?.Dispose();
    }
}
