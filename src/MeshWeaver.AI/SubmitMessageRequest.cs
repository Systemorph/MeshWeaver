using MeshWeaver.Messaging;

namespace MeshWeaver.AI;

/// <summary>
/// Request to submit a user message to a thread.
/// The thread hub creates the user message node, response node, and streams the agent response.
/// Thread must exist before submitting — create via IMeshService.CreateNodeAsync.
/// </summary>
public record SubmitMessageRequest : IRequest<SubmitMessageResponse>
{
    public required string ThreadPath { get; init; }
    public required string UserMessageText { get; init; }
    public string? AgentName { get; init; }
    public string? ModelName { get; init; }
    public string? ContextPath { get; init; }
    public IReadOnlyList<string>? Attachments { get; init; }

    /// <summary>
    /// Set by HandleSubmitMessage after creating the response node.
    /// The execution hub uses this to post streaming progress updates.
    /// </summary>
    public string? ResponsePath { get; init; }
}

public record SubmitMessageResponse
{
    public bool Success { get; init; }
    public string? Error { get; init; }
}
