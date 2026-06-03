using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.AI;                       // StartThread / SubmitMessage
using MeshWeaver.Graph.Configuration;      // TeamsConversationNodeType, UserNodeType
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;            // IMeshService, IMeshQueryCore, MeshQueryRequest
using MeshWeaver.Mesh.Threading;           // IoPool — bounded HTTP pool (replaces bare Observable.FromAsync)
using MeshWeaver.Messaging;                // IMessageHub, AccessService
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Teams;

/// <summary>A parsed inbound Teams message (Graph/Bot-Framework free, so it is unit-testable).</summary>
public record InboundTeamsMessage(
    string Text, string ConversationId, string ServiceUrl, string? AadObjectId, string? UserName);

/// <summary>
/// Turns an inbound Teams message into agent work, mirroring the email channel: map the Teams user to a
/// Memex user (by AAD object id), find-or-create one thread per Teams conversation (keyed by
/// <c>conversationId</c> via a <see cref="TeamsConversation"/> link node), and run the agent as that user.
/// The agent's reply is delivered back to Teams by <c>TeamsReplySender</c>. Unknown senders get a polite
/// "no account" reply.
/// </summary>
public sealed class TeamsInboundProcessor : IDisposable
{
    private const string Agent = "Assistant";

    // Bound to a sane cap so a burst of inbound Teams notifications can't open unbounded Graph calls.
    private const int HttpConcurrency = 8;

    private readonly IMessageHub hub;
    private readonly ITeamsClient teamsClient;
    private readonly ILogger<TeamsInboundProcessor>? logger;
    private readonly IMeshService meshService;
    private readonly AccessService accessService;
    private readonly IMeshQueryCore query;
    private readonly JsonSerializerOptions jsonOptions;

    // Dedicated bounded HTTP pool, ALWAYS created fresh and owned by this instance — never resolved
    // from the mesh-scoped IoPoolRegistry. The portal builds this processor from its OWN DI container
    // while activating hosted services; reaching across into the mesh hub's ServiceProvider at that
    // moment races that provider's internal service realization (a documented NRE crash-loop — see
    // GraphMail). A self-owned pool always resolves and is disposed with this singleton.
    private readonly IoPool _http = new(HttpConcurrency);

    public TeamsInboundProcessor(IMessageHub hub, ITeamsClient teamsClient, ILogger<TeamsInboundProcessor>? logger = null)
    {
        this.hub = hub;
        this.teamsClient = teamsClient;
        this.logger = logger;
        meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        query = hub.ServiceProvider.GetRequiredService<IMeshQueryCore>();
        jsonOptions = hub.JsonSerializerOptions;
    }

    public void Dispose() => _http.Dispose();

    /// <summary>Routes a parsed Teams message. Bot-Framework-free → unit-testable.</summary>
    public IObservable<Unit> Route(InboundTeamsMessage m)
    {
        if (string.IsNullOrWhiteSpace(m.Text) || string.IsNullOrEmpty(m.AadObjectId))
            return Observable.Return(Unit.Default);

        return query.Query<MeshNode>(MeshQueryRequest.FromQuery(
                    $"nodeType:{UserNodeType.NodeType} content.objectId:{m.AadObjectId} limit:1"), jsonOptions)
            .Take(1)
            .Select(change => change.Items.FirstOrDefault(n => n.State == MeshNodeState.Active))
            .SelectMany(userNode => userNode is not null
                ? HandleUser(userNode.Id, m)
                : HandleUnknown(m));
    }

    private IObservable<Unit> HandleUser(string username, InboundTeamsMessage m) =>
        FindThread(m.ConversationId).SelectMany(existingThreadPath =>
            Observable.Using(
                () => accessService.ImpersonateAsSystem(),
                _ =>
                {
                    if (!string.IsNullOrEmpty(existingThreadPath))
                    {
                        // Continue the conversation's thread.
                        hub.SubmitMessage(existingThreadPath, m.Text,
                            agentName: Agent, createdBy: username, authorName: m.UserName,
                            onError: err => logger?.LogWarning("TeamsInbound: SubmitMessage failed: {Err}", err));
                        return Observable.Return(Unit.Default);
                    }

                    // New conversation → start a thread and link it to the Teams conversation for replies.
                    hub.StartThread($"{username}/_Teams", m.Text,
                        agentName: Agent, createdBy: username, authorName: m.UserName,
                        onCreated: node => CreateLink(node.Path, m),
                        onError: err => logger?.LogWarning("TeamsInbound: StartThread failed: {Err}", err));
                    return Observable.Return(Unit.Default);
                }));

    private IObservable<Unit> HandleUnknown(InboundTeamsMessage m)
    {
        logger?.LogInformation("TeamsInbound: message from unknown Teams user {User}", m.AadObjectId);
        return _http.Run(async ct => await teamsClient.SendMessageAsync(m.ServiceUrl, m.ConversationId,
                "You don't have a Memex account linked to this Microsoft identity yet, so I can't act for you here.", ct)
                .ConfigureAwait(false))
            .Select(_ => Unit.Default)
            .Catch((Exception _) => Observable.Return(Unit.Default));
    }

    private void CreateLink(string threadPath, InboundTeamsMessage m)
    {
        var node = new MeshNode(TeamsConversationNodeType.NodeType, $"{threadPath}/{TeamsConversationNodeType.Segment}/{TeamsConversationNodeType.NodeType}")
        {
            Name = "Teams Conversation",
            MainNode = threadPath,
            Content = new TeamsConversation
            {
                ThreadPath = threadPath,
                ServiceUrl = m.ServiceUrl,
                ConversationId = m.ConversationId,
                TeamsUserId = m.AadObjectId
            }
        };
        meshService.CreateNode(node).Subscribe(
            _ => { },
            ex => logger?.LogWarning(ex, "TeamsInbound: failed to link thread {Thread} to Teams conversation", threadPath));
    }

    private IObservable<string?> FindThread(string conversationId) =>
        query.Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"nodeType:{TeamsConversationNodeType.NodeType} content.conversationId:{conversationId} limit:1"), jsonOptions)
            .Take(1)
            .Select(change => change.Items
                .Select(n => LinkOf(n)?.ThreadPath)
                .FirstOrDefault(p => !string.IsNullOrEmpty(p)));

    private TeamsConversation? LinkOf(MeshNode n) => n.Content switch
    {
        TeamsConversation c => c,
        JsonElement je => Safe(je),
        _ => null
    };

    private TeamsConversation? Safe(JsonElement je)
    {
        try { return JsonSerializer.Deserialize<TeamsConversation>(je.GetRawText(), jsonOptions); }
        catch { return null; }
    }
}
