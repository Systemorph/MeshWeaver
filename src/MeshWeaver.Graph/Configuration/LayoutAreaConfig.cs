namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Defines a layout area configuration.
/// Stored in _config/layoutAreas/{id}.json
/// </summary>
public record LayoutAreaConfig
{
    /// <summary>
    /// Unique identifier for the layout area.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// The area name for routing (e.g., "Details", "Thumbnail").
    /// </summary>
    public required string Area { get; init; }

    /// <summary>
    /// Display title for the area.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Group for organizing areas (e.g., "primary", "secondary").
    /// </summary>
    public string? Group { get; init; }

    /// <summary>
    /// Display order within the group.
    /// </summary>
    public int Order { get; init; }

    /// <summary>
    /// Whether the area is invisible in navigation.
    /// </summary>
    public bool IsInvisible { get; init; }
}
