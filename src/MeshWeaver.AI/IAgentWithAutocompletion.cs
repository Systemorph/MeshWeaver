#nullable enable

using MeshWeaver.AI.Completion;

namespace MeshWeaver.AI;

/// <summary>
/// Interface for agents that can provide autocomplete items (e.g., files from content collections).
/// </summary>
public interface IAgentWithAutocompletion : IAgentDefinition
{
    /// <summary>
    /// Returns autocomplete items for this agent based on the current context.
    /// </summary>
    /// <param name="context">The current chat context containing address and layout area information.</param>
    /// <returns>A collection of autocomplete items (e.g., files, commands).</returns>
    Task<IEnumerable<AutocompleteItem>> GetAutocompletionItemsAsync(AgentContext? context);
}
