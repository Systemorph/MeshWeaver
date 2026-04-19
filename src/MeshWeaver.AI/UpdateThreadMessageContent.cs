using System.Collections.Immutable;
using MeshWeaver.Layout;

namespace MeshWeaver.AI;

/// <summary>
/// Message posted to the response message hub to update content during streaming.
/// Handled locally on the grain — updates workspace → sync stream → clients.
/// </summary>
public record UpdateThreadMessageContent
{
    /// <summary>
    /// Incremental text chunk to APPEND to the current message Text. The preferred shape
    /// for streaming — each chunk just carries the new bytes since the previous update.
    /// </summary>
    public string? TextDelta { get; init; }

    /// <summary>
    /// Full text replacement. Only set for final-state writes (completion text, error text,
    /// cancel markers). Streaming should use <see cref="TextDelta"/> instead.
    /// </summary>
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
