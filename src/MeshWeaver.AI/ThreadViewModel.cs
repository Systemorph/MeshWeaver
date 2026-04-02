using System.Collections.Immutable;
using MeshWeaver.Layout;

namespace MeshWeaver.AI;

/// <summary>
/// View model for thread data binding in the ThreadChatControl.
/// Wraps all thread state needed by the Blazor view:
/// - Messages: ordered list of child message node IDs
/// - ThreadPath: the thread node's full path
/// - InitialContext: the context path for agent initialization
/// - IsSubmitting: true while a message is being submitted
/// - IsLoading: true while initial thread data is being loaded
///
/// Custom equality: uses SequenceEqual for Messages to prevent redundant UI updates
/// when the stream re-emits with a new list instance containing the same elements.
/// </summary>
public record ThreadViewModel
{
    public IReadOnlyList<string> Messages { get; init; } = [];
    public string? ThreadPath { get; init; }
    public string? InitialContext { get; init; }
    public string? InitialContextDisplayName { get; init; }

    /// <summary>True while a message is being submitted (cells not yet created).</summary>
    public bool IsSubmitting { get; init; }

    /// <summary>True while initial thread data is loading.</summary>
    public bool IsLoading { get; init; }

    /// <summary>True while the thread is executing (agent generating response).</summary>
    public bool IsExecuting { get; init; }

    /// <summary>Current execution activity description.</summary>
    public string? ExecutionStatus { get; init; }

    /// <summary>Streaming text from the active response (updated at 2/sec on Thread node).</summary>
    public string? StreamingText { get; init; }

    /// <summary>Streaming tool calls from the active response.</summary>
    public ImmutableList<ToolCallEntry>? StreamingToolCalls { get; init; }

    /// <summary>Total tokens used in the current execution.</summary>
    public int TokensUsed { get; init; }

    /// <summary>When the current execution started (for elapsed time display).</summary>
    public DateTime? ExecutionStartedAt { get; init; }

    public virtual bool Equals(ThreadViewModel? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return ThreadPath == other.ThreadPath
               && InitialContext == other.InitialContext
               && InitialContextDisplayName == other.InitialContextDisplayName
               && IsSubmitting == other.IsSubmitting
               && IsLoading == other.IsLoading
               && IsExecuting == other.IsExecuting
               && ExecutionStatus == other.ExecutionStatus
               && StreamingText == other.StreamingText
               && TokensUsed == other.TokensUsed
               && ExecutionStartedAt == other.ExecutionStartedAt
               && Messages.SequenceEqual(other.Messages);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(ThreadPath);
        hash.Add(InitialContext);
        hash.Add(InitialContextDisplayName);
        hash.Add(IsSubmitting);
        hash.Add(IsLoading);
        hash.Add(IsExecuting);
        hash.Add(ExecutionStatus);
        hash.Add(StreamingText);
        hash.Add(TokensUsed);
        hash.Add(ExecutionStartedAt);
        foreach (var msg in Messages)
            hash.Add(msg);
        return hash.ToHashCode();
    }
}
