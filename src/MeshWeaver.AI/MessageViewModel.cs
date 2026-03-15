namespace MeshWeaver.AI;

/// <summary>
/// Denormalized view model for thread message cells.
/// Contains enough data to render the cell and reconstruct chat history for the agent.
/// Stored in the Thread hub's DataSource, keyed by Path.
/// </summary>
public record MessageViewModel
{
    public required string Id { get; init; }
    public required string Path { get; init; }
    public int Order { get; init; }
    public string Role { get; init; } = "user";
    public string Text { get; init; } = "";
    public string? AgentName { get; init; }
    public string? ModelName { get; init; }
    public ThreadMessageType Type { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}
