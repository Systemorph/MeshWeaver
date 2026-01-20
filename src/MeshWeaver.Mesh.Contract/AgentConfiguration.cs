using System.ComponentModel.DataAnnotations;

namespace MeshWeaver.Mesh;

/// <summary>
/// Represents an agent configuration stored in the graph.
/// Agents are stored as MeshNodes with nodeType="Agent" and Content=AgentConfiguration.
/// Supports hierarchical resolution - agents at lower namespaces override parent namespaces.
/// </summary>
public record AgentConfiguration
{
    /// <summary>
    /// Unique identifier for this agent.
    /// </summary>
    [Key]
    public required string Id { get; init; }

    /// <summary>
    /// Display name for UI (defaults to Id if not set).
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Description of what this agent does.
    /// Used for delegation decisions and UI display.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// System prompt / instructions for the agent.
    /// Supports markdown formatting.
    /// </summary>
    public string? Instructions { get; init; }

    /// <summary>
    /// Icon URL or identifier for the agent.
    /// </summary>
    public string? Icon { get; init; }

    /// <summary>
    /// Custom SVG path for icon (optional override).
    /// </summary>
    public string? CustomIconSvg { get; init; }

    /// <summary>
    /// Group name for UI categorization (e.g., "Insurance", "Todo").
    /// </summary>
    public string? GroupName { get; init; }

    /// <summary>
    /// Whether this is the default/entry-point agent.
    /// Only one agent should have IsDefault=true at root level.
    /// </summary>
    public bool IsDefault { get; init; }

    /// <summary>
    /// Whether this agent is exposed for delegation from the default agent.
    /// </summary>
    public bool ExposedInNavigator { get; init; }

    /// <summary>
    /// List of agents this agent can delegate to.
    /// Paths can be absolute ("/pricing/RiskImportAgent") or relative ("RiskImportAgent").
    /// </summary>
    public List<AgentDelegation>? Delegations { get; init; }

    /// <summary>
    /// Preferred model name from available models.
    /// Null means use the factory default.
    /// </summary>
    public string? PreferredModel { get; init; }

    /// <summary>
    /// RSQL pattern for context matching.
    /// If set, agent activates when context matches this pattern.
    /// Example: "address.type==pricing" or "address.path=like=*Todo*"
    /// </summary>
    public string? ContextMatchPattern { get; init; }

    /// <summary>
    /// Display order for sorting agents in the UI.
    /// Lower values appear first.
    /// </summary>
    public int DisplayOrder { get; init; }
}

/// <summary>
/// Describes a delegation target with routing instructions.
/// </summary>
public record AgentDelegation
{
    /// <summary>
    /// Path to the target agent (relative or absolute).
    /// Examples: "TodoAgent", "/pricing/RiskImportAgent"
    /// </summary>
    public required string AgentPath { get; init; }

    /// <summary>
    /// Instructions for when to delegate to this agent.
    /// Used by the LLM to decide when to call this delegation.
    /// </summary>
    public string? Instructions { get; init; }
}
