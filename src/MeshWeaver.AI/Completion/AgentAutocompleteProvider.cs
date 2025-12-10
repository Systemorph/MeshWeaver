#nullable enable

using MeshWeaver.Data.Completion;

namespace MeshWeaver.AI.Completion;

/// <summary>
/// Provides autocomplete items for agents.
/// This is a local provider (no address routing) for the agent/ prefix.
/// </summary>
public class AgentAutocompleteProvider(IEnumerable<IAgentDefinition> agentDefinitions) : IAutocompleteProvider
{
    /// <inheritdoc />
    public Task<IEnumerable<AutocompleteItem>> GetItemsAsync(string query, CancellationToken ct = default)
    {
        var items = agentDefinitions
            .Select(agent => new AutocompleteItem(
                Label: $"@agent/{agent.Name}",
                InsertText: $"@agent/{agent.Name} ",
                Description: agent.Description,
                Category: agent.GroupName ?? "Agents",
                Priority: agent.DisplayOrder,
                Kind: AutocompleteKind.Agent
            ));

        return Task.FromResult(items);
    }
}
