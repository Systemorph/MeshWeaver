using MeshWeaver.Messaging;

namespace MeshWeaver.AI;

/// <summary>
/// Request sent from Thread hub to _Exec sub-hub to start agent streaming.
/// The sub-hub processes this in its own execution queue — no Task.Run needed.
/// </summary>
public record StartStreamingRequest : IRequest<StartStreamingResponse>
{
    public required string ThreadPath { get; init; }
    public required string UserMessageText { get; init; }
    public required string UserMessagePath { get; init; }
    public required string ResponsePath { get; init; }
    public int ResponseOrder { get; init; }
    public string? ContextPath { get; init; }
    public string? AgentName { get; init; }
    public string? ModelName { get; init; }
    public IReadOnlyList<string>? Attachments { get; init; }
}

public record StartStreamingResponse
{
    public bool Success { get; init; }
    public string? Error { get; init; }
}
