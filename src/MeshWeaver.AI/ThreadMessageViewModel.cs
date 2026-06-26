using System.Collections.Immutable;
using MeshWeaver.Layout;

namespace MeshWeaver.AI;

/// <summary>
/// View model for ThreadMessage data binding in the bubble control.
/// Pushed to the layout data section via host.UpdateData.
/// The bubble control binds to properties via JsonPointerReference.
/// </summary>
public record ThreadMessageViewModel
{
    /// <summary>Message role: <c>user</c>, <c>assistant</c>, or <c>system</c>.</summary>
    public string Role { get; init; } = "assistant";
    /// <summary>Display name of the message author (e.g. <c>You</c> or the agent name).</summary>
    public string AuthorName { get; init; } = "";
    /// <summary>Model that generated the response, if known.</summary>
    public string? ModelName { get; init; }
    /// <summary>Formatted time-of-day (<c>HH:mm:ss</c>) the message was created.</summary>
    public string? Timestamp { get; init; }
    /// <summary>The message body text.</summary>
    public string Text { get; init; } = "";
    /// <summary>Tool calls made during this message's execution, shown as inline chips.</summary>
    public ImmutableList<ToolCallEntry> ToolCalls { get; init; } = [];
    /// <summary>
    /// Nodes this message's execution created / updated / deleted. The bubble cross-
    /// references tool-call target paths against this list to render inline Diff and
    /// Restore links next to each "Creating / Updating / Deleting X" chip.
    /// </summary>
    public ImmutableList<NodeChangeEntry> UpdatedNodes { get; init; } = [];

    /// <summary>
    /// Projects a persisted <see cref="ThreadMessage"/> into the data-bound view model,
    /// defaulting the author name from the role when unset and formatting the timestamp.
    /// </summary>
    /// <param name="msg">The source thread message.</param>
    /// <returns>The view model for binding in the bubble control.</returns>
    public static ThreadMessageViewModel FromMessage(ThreadMessage msg) => new()
    {
        Role = msg.Role,
        AuthorName = msg.AuthorName ?? (msg.Role == "user" ? "You" : msg.AgentName ?? "Assistant"),
        ModelName = msg.ModelName,
        Timestamp = msg.Timestamp.ToString("HH:mm:ss"),
        Text = msg.Text ?? "",
        ToolCalls = msg.ToolCalls,
        UpdatedNodes = msg.UpdatedNodes
    };

    /// <summary>
    /// Value equality over the rendered fields (role, author, model, text, tool calls,
    /// updated nodes) so the bound view skips redundant updates on re-emission.
    /// </summary>
    /// <param name="other">The view model to compare against.</param>
    /// <returns>True when the rendered content is equal.</returns>
    public virtual bool Equals(ThreadMessageViewModel? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Role == other.Role
               && AuthorName == other.AuthorName
               && ModelName == other.ModelName
               && Text == other.Text
               && ToolCalls.SequenceEqual(other.ToolCalls)
               && UpdatedNodes.SequenceEqual(other.UpdatedNodes);
    }

    /// <summary>Hash consistent with <see cref="Equals(ThreadMessageViewModel?)"/>.</summary>
    /// <returns>A hash combining role, text, and tool-call / updated-node counts.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Role);
        hash.Add(Text);
        hash.Add(ToolCalls.Count);
        hash.Add(UpdatedNodes.Count);
        return hash.ToHashCode();
    }
}
