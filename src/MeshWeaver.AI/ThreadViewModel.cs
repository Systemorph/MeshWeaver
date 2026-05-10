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

    /// <summary>
    /// Texts of user messages queued during the in-flight turn — i.e. entries
    /// in <see cref="Thread.PendingUserMessages"/> that the agent has NOT yet
    /// drained via the <c>check_inbox</c> tool. Rendered in the chat below the
    /// "Generating response..." progress strip so the user can see what's
    /// "still in the outbox" before the agent acknowledges it.
    /// Empty when the queue has been drained or no follow-ups were typed.
    /// Order: <see cref="Thread.UserMessageIds"/> submission order.
    /// </summary>
    public IReadOnlyList<string> PendingMessageTexts { get; init; } = [];

    /// <summary>Total tokens used in the current execution.</summary>
    public int TokensUsed { get; init; }

    /// <summary>When the current execution started (for elapsed time display).</summary>
    public DateTime? ExecutionStartedAt { get; init; }

    /// <summary>
    /// Sticky agent selection from <see cref="Thread.SelectedAgentName"/>.
    /// On thread load the chat picker pre-selects this so the user's choice
    /// survives a page reload. Null on a brand-new thread.
    /// </summary>
    public string? SelectedAgentName { get; init; }

    /// <summary>
    /// Sticky model selection from <see cref="Thread.SelectedModelName"/>.
    /// Same persistence shape as <see cref="SelectedAgentName"/>.
    /// </summary>
    public string? SelectedModelName { get; init; }

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
               && SelectedAgentName == other.SelectedAgentName
               && SelectedModelName == other.SelectedModelName
               && Messages.SequenceEqual(other.Messages)
               && (StreamingToolCalls ?? []).SequenceEqual(other.StreamingToolCalls ?? [])
               && PendingMessageTexts.SequenceEqual(other.PendingMessageTexts);
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
        hash.Add(SelectedAgentName);
        hash.Add(SelectedModelName);
        foreach (var msg in Messages)
            hash.Add(msg);
        if (StreamingToolCalls != null)
            foreach (var tc in StreamingToolCalls)
                hash.Add(tc);
        foreach (var txt in PendingMessageTexts)
            hash.Add(txt);
        return hash.ToHashCode();
    }
}
