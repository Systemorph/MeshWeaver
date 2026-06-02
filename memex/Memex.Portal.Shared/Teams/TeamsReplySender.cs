using System.Collections.Concurrent;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.AI;                       // ThreadFlow, ThreadMessage
using MeshWeaver.Blazor.Infrastructure;    // PortalApplication
using MeshWeaver.Data;                      // IWorkspace, GetWorkspace, GetMeshNodeStream
using MeshWeaver.Graph.Configuration;      // TeamsConversationNodeType
using MeshWeaver.Mesh;                      // TeamsConversation, MeshNode
using MeshWeaver.Mesh.Security;             // ImpersonateAsSystem
using MeshWeaver.Mesh.Services;             // IMeshQueryCore, MeshQueryRequest
using MeshWeaver.Messaging;                 // IMessageHub, AccessService
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Teams;

/// <summary>
/// Delivers agent replies back into Teams. Watches the <see cref="TeamsConversation"/> link nodes; for
/// each, observes its thread the <b>exact same way the GUI and tests do</b>
/// (<c>workspace.GetMeshNodeStream(threadPath)</c> → <see cref="MeshThread"/>, wait for
/// <c>!IsExecuting</c> with a new <c>Messages[^1]</c>), reads that response message node at
/// <c>{threadPath}/{messageId}</c> (<see cref="ThreadMessage"/>, public <c>Text</c>), and posts it via
/// <see cref="ITeamsClient"/>. Send-once is tracked by <see cref="TeamsConversation.LastDeliveredMessageId"/>
/// (persisted → restart-safe). Inert unless the Teams bot is configured.
/// </summary>
public sealed class TeamsReplySender(
    IServiceProvider rootServices,
    IHostApplicationLifetime lifetime,
    ILogger<TeamsReplySender>? logger = null) : IHostedService, IDisposable
{
    private readonly CompositeDisposable subscriptions = new();
    private readonly ConcurrentDictionary<string, byte> watched = new();   // threadPath → subscribed (instance)
    private readonly ConcurrentDictionary<string, string?> lastSent = new();
    private IServiceScope? scope;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lifetime.ApplicationStarted.Register(Begin);
        return Task.CompletedTask;
    }

    private void Begin()
    {
        try
        {
            scope = rootServices.CreateScope();
            var hub = scope.ServiceProvider.GetRequiredService<PortalApplication>().Hub;
            var teams = hub.ServiceProvider.GetService<ITeamsClient>();
            if (teams is null || !teams.IsConfigured) return;   // Teams bot off → inert
            var query = hub.ServiceProvider.GetRequiredService<IMeshQueryCore>();
            var access = hub.ServiceProvider.GetRequiredService<AccessService>();
            var jsonOptions = hub.JsonSerializerOptions;

            // Watch the Teams conversation links; subscribe to each link's thread exactly once.
            subscriptions.Add(query
                .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
                    $"nodeType:{TeamsConversationNodeType.NodeType}"), jsonOptions)
                .Select(change => change.Items)
                .Subscribe(
                    items =>
                    {
                        foreach (var linkNode in items)
                        {
                            var link = LinkOf(linkNode, jsonOptions);
                            if (link is null || string.IsNullOrEmpty(link.ThreadPath)) continue;
                            if (!watched.TryAdd(link.ThreadPath, 0)) continue;       // already watching
                            lastSent[link.ThreadPath] = link.LastDeliveredMessageId; // restart-safe baseline
                            WatchThread(hub, teams, access, linkNode.Path, link);
                        }
                    },
                    ex => logger?.LogWarning(ex, "TeamsReply: link query failed")));
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "TeamsReply: failed to start");
        }
    }

    private void WatchThread(
        IMessageHub hub, ITeamsClient teams, AccessService access, string linkPath, TeamsConversation link)
    {
        var workspace = hub.GetWorkspace();
        // Reuse the shared read-side abstraction — the SAME one the GUI/tests use to read agent replies.
        // No bespoke thread observing here (see ThreadFlow.ObserveResponses / ThreadOperations.md).
        subscriptions.Add(ThreadFlow.ObserveResponses(hub, link.ThreadPath)
            .Where(r => r.MessageId != lastSent.GetValueOrDefault(link.ThreadPath))
            .SelectMany(r => Observable
                .FromAsync(ct => teams.SendMessageAsync(link.ServiceUrl, link.ConversationId, r.Message.Text, ct))
                .Select(ok => (r.MessageId, ok)))
            .Subscribe(
                res =>
                {
                    if (!res.ok) return;
                    lastSent[link.ThreadPath] = res.MessageId;
                    // Persist send-once across restarts on the link node.
                    using (access.ImpersonateAsSystem())
                        workspace.GetMeshNodeStream(linkPath)
                            .Update(node => node with { Content = link with { LastDeliveredMessageId = res.MessageId } })
                            .Subscribe(_ => { }, ex => logger?.LogWarning(ex, "TeamsReply: persist mark failed"));
                },
                ex => logger?.LogWarning(ex, "TeamsReply: delivery failed for {Thread}", link.ThreadPath)));
    }

    private static TeamsConversation? LinkOf(MeshNode n, JsonSerializerOptions opts) => n.Content switch
    {
        TeamsConversation c => c,
        JsonElement je => Safe(je, opts),
        _ => null
    };

    private static TeamsConversation? Safe(JsonElement je, JsonSerializerOptions opts)
    {
        try { return JsonSerializer.Deserialize<TeamsConversation>(je.GetRawText(), opts); }
        catch { return null; }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose()
    {
        subscriptions.Dispose();
        scope?.Dispose();
    }
}
