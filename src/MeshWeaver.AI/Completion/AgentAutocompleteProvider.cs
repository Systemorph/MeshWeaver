#nullable enable

using MeshWeaver.Data.Completion;
using MeshWeaver.Graph.Configuration;

namespace MeshWeaver.AI.Completion;

/// <summary>
/// Provides autocomplete items for agents.
/// Gets agents from IAgentChatFactoryProvider.
/// </summary>
public class AgentAutocompleteProvider(IAgentChatFactoryProvider factoryProvider) : IAutocompleteProvider
{
    /// <inheritdoc />
    public async Task<IEnumerable<AutocompleteItem>> GetItemsAsync(string query, CancellationToken ct = default)
    {
        var agents = await factoryProvider.GetAgentsAsync();

        return agents
            .Select(agent => new AutocompleteItem(
                Label: $"@agent/{agent.Id}",
                InsertText: $"@agent/{agent.Id} ",
                Description: agent.Description ?? string.Empty,
                Category: agent.GroupName ?? "Agents",
                Priority: agent.DisplayOrder,
                Kind: AutocompleteKind.Agent
            ));
    }
}
