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
    public ImmutableList<NodeChangeEntry>? UpdatedNodes { get; init; }
    public string? AgentName { get; init; }
    public string? ModelName { get; init; }

    /// <summary>Token usage from the model provider. Set on the final update of a round.</summary>
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    public int? TotalTokens { get; init; }

    /// <summary>Wall-clock completion timestamp. Set on the final update of a round.</summary>
    public DateTime? CompletedAt { get; init; }
}
