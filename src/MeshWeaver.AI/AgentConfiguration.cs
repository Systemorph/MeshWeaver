using System.ComponentModel.DataAnnotations;

namespace MeshWeaver.AI;

/// <summary>
/// Represents an agent configuration stored in the graph.
/// Agents are stored as MeshNodes with nodeType="Agent" and Content=AgentConfiguration.
/// Supports hierarchical resolution - agents at lower namespaces override parent namespaces.
///
/// <para>🚨 Node-level metadata is NOT duplicated here. The display name, description,
/// icon, group, and ordering all live on the owning <see cref="MeshWeaver.Mesh.MeshNode"/>
/// (<c>Name</c>, <c>Description</c>, <c>Icon</c>, <c>Category</c>, <c>Order</c>) and are
/// edited through the standard node settings — never replicated on the agent content.
/// This record carries only what's specific to the agent's behaviour. (<see cref="Id"/>
/// is the runtime identity key and equals the owning node's <c>Id</c> by construction;
/// <see cref="Description"/> is kept because the agent runtime feeds it to the model as
/// delegation metadata where only the detached configuration is in hand.)</para>
/// </summary>
public record AgentConfiguration
{
    /// <summary>
    /// Unique identifier for this agent. Equals the owning <see cref="MeshWeaver.Mesh.MeshNode.Id"/>
    /// by construction; used as the runtime identity key for agent creation, delegation
    /// resolution, and the created-agents map. Display reads the node's Id, not this field.
    /// </summary>
    [Key]
    public required string Id { get; init; }

    /// <summary>
    /// Description of what this agent does. Mirrors the owning node's
    /// <see cref="MeshWeaver.Mesh.MeshNode.Description"/>; kept on the configuration because
    /// the agent runtime feeds it to the model (delegation/hand-off catalogue) where only
    /// the detached <see cref="AgentConfiguration"/> is available. UI/picker read the node.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// System prompt / instructions for the agent.
    /// Supports markdown formatting.
    /// </summary>
    public string? Instructions { get; init; }

    /// <summary>
    /// Custom SVG path for icon (optional override).
    /// </summary>
    public string? CustomIconSvg { get; init; }

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
    /// List of agents this agent can hand off to.
    /// Handoff transfers control entirely to the target agent on the shared thread.
    /// Unlike delegation, the source agent stops and the target agent takes over.
    /// </summary>
    public List<AgentHandoff>? Handoffs { get; init; }

    /// <summary>
    /// RSQL pattern for context matching.
    /// If set, agent activates when context matches this pattern.
    /// Example: "address.type==pricing" or "address.path=like=*Todo*"
    /// </summary>
    public string? ContextMatchPattern { get; init; }

    /// <summary>
    /// OPTIONAL hint: abstract model tier this agent prefers — "heavy", "standard", "light",
    /// or "utility". Never required: when unset (the normal case), or when the deployment has
    /// no <c>ModelTier:*</c> config, model selection is entirely unaffected. When set AND
    /// configured, it only fills the gap where nobody picked a model (headless flows like
    /// notification triage or icon/description micro-jobs) — an explicit composer selection
    /// always wins. Declared only on the built-in background micro-agents; interactive agents
    /// should leave it unset.
    /// </summary>
    public string? ModelTier { get; init; }

    /// <summary>
    /// Optional list of additional plugins this agent should load.
    /// Standard plugins (Chat, Mesh, LayoutArea, Data) are always loaded.
    /// Additional plugins are resolved by name from DI-registered IAgentPlugin services.
    /// </summary>
    public List<AgentPluginReference>? Plugins { get; init; }

    /// <summary>
    /// Optional per-round cap on the number of tool-call iterations the agent may run in a
    /// single turn. Maps directly onto
    /// <c>Microsoft.Extensions.AI.FunctionInvokingChatClient.MaximumIterationsPerRequest</c>
    /// (wired in <see cref="ChatClientAgentFactory"/>). When <c>null</c> (the default) the
    /// Microsoft.Extensions.AI default applies — high enough that a high-volume agent can
    /// issue hundreds of tool calls in one round before it engages. Set a small value
    /// (e.g. 20–30) for such agents to force natural break points: on reaching the cap the
    /// framework strips tools on the final iteration (its
    /// <c>PrepareOptionsForLastIteration</c> path) so the model returns a graceful final
    /// answer rather than truncating, and the agent is additionally instructed to invite the
    /// user to reply "continue" to run the next batch.
    /// </summary>
    public int? MaxToolCallsPerRound { get; init; }
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

/// <summary>
/// References an optional plugin by name, with optional method filtering.
/// Used in agent frontmatter to declare which additional plugins an agent needs.
/// </summary>
public record AgentPluginReference
{
    /// <summary>
    /// Plugin name (e.g., "WebSearch").
    /// Must match a registered IAgentPlugin.Name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Optional list of method names to expose.
    /// If null or empty, all plugin methods are included.
    /// </summary>
    public List<string>? Methods { get; init; }

    /// <summary>
    /// Parses the frontmatter string form: "PluginName" or "PluginName:Method1,Method2".
    /// Shared by every agent-definition parser so the syntax stays uniform.
    /// </summary>
    public static AgentPluginReference Parse(string s)
    {
        var colonIndex = s.IndexOf(':');
        if (colonIndex < 0)
            return new AgentPluginReference { Name = s.Trim() };

        return new AgentPluginReference
        {
            Name = s[..colonIndex].Trim(),
            Methods = s[(colonIndex + 1)..].Split(',').Select(m => m.Trim()).ToList()
        };
    }
}

/// <summary>
/// Describes a handoff target with routing instructions.
/// </summary>
public record AgentHandoff
{
    /// <summary>
    /// Path to the target agent (relative or absolute).
    /// </summary>
    public required string AgentPath { get; init; }

    /// <summary>
    /// Instructions for when to hand off to this agent.
    /// </summary>
    public string? Instructions { get; init; }
}
