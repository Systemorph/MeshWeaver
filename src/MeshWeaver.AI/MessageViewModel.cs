namespace MeshWeaver.AI;

/// <summary>
/// Denormalized view model for thread message cells.
/// Contains enough data to render the cell and reconstruct chat history for the agent.
/// Stored in the Thread hub's DataSource, keyed by Path.
/// </summary>
public record MessageViewModel
{
    /// <summary>Stable id of the message cell.</summary>
    public required string Id { get; init; }
    /// <summary>Mesh-node path of the message cell (the key it is stored under).</summary>
    public required string Path { get; init; }
    /// <summary>Sort order of this cell within the thread.</summary>
    public int Order { get; init; }
    /// <summary>Author role — e.g. <c>"user"</c> or <c>"assistant"</c>.</summary>
    public string Role { get; init; } = "user";
    /// <summary>The message text rendered in the cell.</summary>
    public string Text { get; init; } = "";
    /// <summary>Name of the agent that produced the message; <c>null</c> for user messages.</summary>
    public string? AgentName { get; init; }
    /// <summary>Name of the model that produced the message; <c>null</c> when not applicable.</summary>
    public string? ModelName { get; init; }
    /// <summary>The kind of thread message (text, tool call, etc.).</summary>
    public ThreadMessageType Type { get; init; }
    /// <summary>UTC timestamp when the message was created.</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}
