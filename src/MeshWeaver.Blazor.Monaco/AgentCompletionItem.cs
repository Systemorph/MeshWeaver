namespace MeshWeaver.Blazor.Monaco;

/// <summary>
/// Represents an agent for autocomplete suggestions in the Monaco editor.
/// </summary>
public class AgentCompletionItem
{
    /// <summary>
    /// The name of the agent (without the @ prefix).
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The description of the agent shown in the autocomplete dropdown.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Creates an AgentCompletionItem from an object that has Name and Description properties.
    /// </summary>
    public static AgentCompletionItem From<T>(T agent) where T : class
    {
        var type = typeof(T);
        var nameProperty = type.GetProperty("Name") ?? throw new ArgumentException("Agent must have a Name property");
        var descriptionProperty = type.GetProperty("Description") ?? throw new ArgumentException("Agent must have a Description property");

        return new AgentCompletionItem
        {
            Name = nameProperty.GetValue(agent)?.ToString() ?? string.Empty,
            Description = descriptionProperty.GetValue(agent)?.ToString() ?? string.Empty
        };
    }
}
