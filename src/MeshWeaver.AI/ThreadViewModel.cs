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
    /// <summary>Ordered list of the thread's message node IDs to render.</summary>
    public IReadOnlyList<string> Messages { get; init; } = [];
    /// <summary>Full path of the thread node this view binds to.</summary>
    public string? ThreadPath { get; init; }

    /// <summary>The thread's title (<see cref="MeshNode.Name"/>). Rendered as the hero-header
    /// title; the full-page chat view binds + inline-edits it (writes back to the node's Name).</summary>
    public string? Name { get; init; }

    /// <summary>The thread's description (<see cref="MeshNode.Description"/>) — shown as the smaller
    /// abstract under the title and inline-edited the same way.</summary>
    public string? Description { get; init; }
    /// <summary>Context path used to initialize the agent for this thread.</summary>
    public string? InitialContext { get; init; }
    /// <summary>Human-readable display name for <see cref="InitialContext"/>.</summary>
    public string? InitialContextDisplayName { get; init; }

    /// <summary>True while a message is being submitted (cells not yet created).</summary>
    public bool IsSubmitting { get; init; }

    /// <summary>True while initial thread data is loading.</summary>
    public bool IsLoading { get; init; }

    /// <summary>True while the thread is executing (agent generating response).</summary>
    public bool IsExecuting { get; init; }

    /// <summary>True when the thread's status is <see cref="ThreadExecutionStatus.Done"/> — drives the
    /// header's Mark Done / Reopen toggle.</summary>
    public bool IsDone { get; init; }

    /// <summary>Current execution activity description.</summary>
    public string? ExecutionStatus { get; init; }

    /// <summary>Streaming text from the active response (updated at 2/sec on Thread node).</summary>
    public string? StreamingText { get; init; }

    /// <summary>Streaming tool calls from the active response.</summary>
    public ImmutableList<ToolCallEntry>? StreamingToolCalls { get; init; }

    /// <summary>
    /// Texts of user messages currently in <see cref="Thread.PendingUserMessages"/>
    /// — submitted via <see cref="ThreadInput.AppendUserInput"/> but not yet
    /// drained into <see cref="Thread.Messages"/>. Rendered inline at the end
    /// of the chat history as "queued" cells so the user sees their submission
    /// immediately, even before round dispatch materialises the satellite cell.
    /// Disappears for an id once the inbox drain promotes it into Messages.
    /// Order: <see cref="Thread.UserMessageIds"/> submission order.
    /// </summary>
    public IReadOnlyList<string> PendingMessageTexts { get; init; } = [];

    /// <summary>When the current execution started (for elapsed time display).</summary>
    public DateTime? ExecutionStartedAt { get; init; }

    /// <summary>
    /// Identity (ObjectId / email) of the user who created the thread, from
    /// <see cref="MeshNode.CreatedBy"/>. The chat view shows the input + edit ops
    /// only when this matches the current user — other users' threads are read-only.
    /// </summary>
    public string? CreatedBy { get; init; }

    /// <summary>
    /// Value equality over all bound thread state (using <c>SequenceEqual</c> for the
    /// message, tool-call and pending-text lists) so the view skips redundant updates.
    /// </summary>
    /// <param name="other">The view model to compare against.</param>
    /// <returns>True when all bound state is equal.</returns>
    public virtual bool Equals(ThreadViewModel? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return ThreadPath == other.ThreadPath
               && Name == other.Name
               && Description == other.Description
               && InitialContext == other.InitialContext
               && InitialContextDisplayName == other.InitialContextDisplayName
               && IsSubmitting == other.IsSubmitting
               && IsLoading == other.IsLoading
               && IsExecuting == other.IsExecuting
               && IsDone == other.IsDone
               && ExecutionStatus == other.ExecutionStatus
               && StreamingText == other.StreamingText
               && ExecutionStartedAt == other.ExecutionStartedAt
               && CreatedBy == other.CreatedBy
               && Messages.SequenceEqual(other.Messages)
               && (StreamingToolCalls ?? []).SequenceEqual(other.StreamingToolCalls ?? [])
               && PendingMessageTexts.SequenceEqual(other.PendingMessageTexts);
    }

    /// <summary>Hash consistent with <see cref="Equals(ThreadViewModel?)"/>.</summary>
    /// <returns>A hash combining the thread's bound state and list elements.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(ThreadPath);
        hash.Add(Name);
        hash.Add(Description);
        hash.Add(InitialContext);
        hash.Add(InitialContextDisplayName);
        hash.Add(IsSubmitting);
        hash.Add(IsLoading);
        hash.Add(IsExecuting);
        hash.Add(IsDone);
        hash.Add(ExecutionStatus);
        hash.Add(StreamingText);
        hash.Add(ExecutionStartedAt);
        hash.Add(CreatedBy);
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
