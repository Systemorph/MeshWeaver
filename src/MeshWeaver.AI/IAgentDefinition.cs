#nullable enable

namespace MeshWeaver.AI;

/// <summary>
/// Interface for providing agent definitions
/// </summary>
public interface IAgentDefinition
{
    /// <summary>
    /// Gets the name of the agent
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the description of the agent
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Gets the instructions for the agent
    /// </summary>
    string Instructions { get; }

    /// <summary>
    /// Gets the group name for UI categorization (e.g., "Insurance", "Northwind", "Todo", "Documentation")
    /// </summary>
    string? GroupName => null;

    /// <summary>
    /// Gets the display order within the group (lower = first)
    /// </summary>
    int DisplayOrder => 0;

    /// <summary>
    /// Gets the FluentUI icon name (e.g., "Shield", "Document", "Database")
    /// </summary>
    string? IconName => null;

    /// <summary>
    /// Gets custom SVG path override for the icon (optional)
    /// </summary>
    string? CustomIconSvg => null;
}
