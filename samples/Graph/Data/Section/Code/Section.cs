// <meshweaver>
// Id: Section
// DisplayName: Section Data Model
// </meshweaver>

using MeshWeaver.Domain;

/// <summary>
/// Represents a grouping container for organizing related nodes.
/// </summary>
public record Section
{
    /// <summary>
    /// Name of the section.
    /// </summary>
    [Required]
    [MeshNodeProperty(nameof(MeshNode.Name))]
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Brief description of the section.
    /// </summary>
    public string? Description { get; init; }
}
