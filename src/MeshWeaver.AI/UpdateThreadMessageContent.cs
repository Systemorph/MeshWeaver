using System.Collections.Immutable;
using MeshWeaver.Layout;

namespace MeshWeaver.AI;

/// <summary>
/// Message posted to the response message hub to update content during streaming.
/// Handled locally on the grain — updates workspace → sync stream → clients.
/// </summary>
public record UpdateThreadMessageContent
{
    public string? Text { get; init; }
    public ImmutableList<ToolCallEntry>? ToolCalls { get; init; }
    public string? AgentName { get; init; }
    public string? ModelName { get; init; }
    public string? DelegationPath { get; init; }
}
