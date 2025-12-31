#nullable enable

using MeshWeaver.Graph.Configuration;

namespace MeshWeaver.AI;

/// <summary>
/// Display information for an agent, including auto-calculated indent level.
/// </summary>
public record AgentDisplayInfo
{
    /// <summary>
    /// The agent name (ID)
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The agent description
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// The group name for categorization
    /// </summary>
    public string? GroupName { get; init; }

    /// <summary>
    /// Display order within group (lower = first)
    /// </summary>
    public int DisplayOrder { get; init; }

    /// <summary>
    /// Auto-calculated indent level based on delegation hierarchy
    /// </summary>
    public int IndentLevel { get; init; }

    /// <summary>
    /// FluentUI icon name
    /// </summary>
    public string? IconName { get; init; }

    /// <summary>
    /// Custom SVG path override
    /// </summary>
    public string? CustomIconSvg { get; init; }

    /// <summary>
    /// Reference to the agent configuration
    /// </summary>
    public required AgentConfiguration AgentConfiguration { get; init; }
}
