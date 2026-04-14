using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using MeshWeaver.Messaging.Security;

namespace MeshWeaver.AI;

/// <summary>
/// Registers a user message id with the thread hub. Client posts this right after
/// (or in parallel with) the CreateNodeRequest for the user cell. The thread hub
/// appends the id to <c>Thread.Messages</c> and <c>Thread.UserMessageIds</c>, stores
/// Pending* settings for the next round, and the server watcher dispatches.
///
/// Going through a first-class message (not a remote-stream write) avoids the
/// patch-index race condition seen with <c>workspace.UpdateMeshNode(address:)</c>.
/// </summary>
[SubmitMessagePermission]
public record AppendUserMessageRequest : IRequest<AppendUserMessageResponse>
{
    public required string ThreadPath { get; init; }
    public required string UserMessageId { get; init; }
    public required string UserText { get; init; }
    public string? AgentName { get; init; }
    public string? ModelName { get; init; }
    public string? ContextPath { get; init; }
    public IReadOnlyList<string>? Attachments { get; init; }
}

public record AppendUserMessageResponse
{
    public bool Success { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Request to resubmit — truncates the thread after the user message id and
/// re-adds it to the queue so the server watcher dispatches a new round.
/// </summary>
[SubmitMessagePermission]
public record ResubmitUserMessageRequest : IRequest<AppendUserMessageResponse>
{
    public required string ThreadPath { get; init; }
    public required string UserMessageId { get; init; }
    public string? NewUserText { get; init; }
    public string? AgentName { get; init; }
    public string? ModelName { get; init; }
}
