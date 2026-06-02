using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.AI;                       // StartThread / SubmitMessage
using MeshWeaver.Graph.Configuration;      // TeamsConversationNodeType, UserNodeType
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;            // IMeshService, IMeshQueryCore, MeshQueryRequest
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
public sealed class TeamsInboundProcessor
{
    private const string Agent = "Assistant";

    private readonly IMessageHub hub;
    private readonly ITeamsClient teamsClient;
    private readonly ILogger<TeamsInboundProcessor>? logger;
    private readonly IMeshService meshService;
    private readonly AccessService accessService;
    private readonly IMeshQueryCore query;
    private readonly JsonSerializerOptions jsonOptions;

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

    /// <summary>Routes a parsed Teams message. Bot-Framework-free → unit-testable.</summary>
    public IObservable<Unit> Route(InboundTeamsMessage m)
    {
        if (string.IsNullOrWhiteSpace(m.Text) || string.IsNullOrEmpty(m.AadObjectId))
            return Observable.Return(Unit.Default);

        return query.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
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
        return Observable.FromAsync(ct => teamsClient.SendMessageAsync(m.ServiceUrl, m.ConversationId,
                "You don't have a Memex account linked to this Microsoft identity yet, so I can't act for you here.", ct))
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
        query.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
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
