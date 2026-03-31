using Microsoft.Extensions.AI;

namespace MeshWeaver.AI.Plugins;

/// <summary>
/// Interface for custom agent plugins that provide AI tools.
/// Plugins are resolved by name from DI when agents declare them in frontmatter.
/// Built-in plugins (Mesh, Data, LayoutArea, Chat) are handled directly by the factory.
/// </summary>
public interface IAgentPlugin
{
    /// <summary>
    /// Unique plugin name used in agent frontmatter (e.g., "WebSearch").
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Creates all AI tools for this plugin.
    /// Method filtering is handled by the factory based on the agent's plugin declaration.
    /// </summary>
    IEnumerable<AITool> CreateTools();
}
