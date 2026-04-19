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
    public string Role { get; init; } = "assistant";
    public string AuthorName { get; init; } = "";
    public string? ModelName { get; init; }
    public string? Timestamp { get; init; }
    public string Text { get; init; } = "";
    public ImmutableList<ToolCallEntry> ToolCalls { get; init; } = [];
    /// <summary>
    /// Nodes this message's execution created / updated / deleted. The bubble cross-
    /// references tool-call target paths against this list to render inline Diff and
    /// Restore links next to each "Creating / Updating / Deleting X" chip.
    /// </summary>
    public ImmutableList<NodeChangeEntry> UpdatedNodes { get; init; } = [];

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
