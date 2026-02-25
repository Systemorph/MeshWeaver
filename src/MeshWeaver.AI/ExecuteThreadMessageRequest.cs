using MeshWeaver.Messaging;

namespace MeshWeaver.AI;

/// <summary>
/// Request to execute an agent response for a thread message on the hub side.
/// Posted from the GUI to the thread hub so execution survives component disposal.
/// </summary>
public record ExecuteThreadMessageRequest : IRequest<ExecuteThreadMessageResponse>
{
    public required string ThreadPath { get; init; }
    public required string UserMessageText { get; init; }
    public string? AgentName { get; init; }
    public string? ModelName { get; init; }
    public string? ContextPath { get; init; }
    public IReadOnlyList<string>? Attachments { get; init; }
}

public record ExecuteThreadMessageResponse
{
    public bool Success { get; init; }
    public string? Error { get; init; }
}
