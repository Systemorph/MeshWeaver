#nullable enable

using MeshWeaver.Data.Completion;

namespace MeshWeaver.AI.Completion;

/// <summary>
/// Provides autocomplete items for agents.
/// Gets agents from IAgentChatFactoryProvider when available, or IEnumerable&lt;IAgentDefinition&gt;.
/// </summary>
public class AgentAutocompleteProvider : IAutocompleteProvider
{
    private readonly IAgentChatFactoryProvider? _factoryProvider;
    private readonly IEnumerable<IAgentDefinition>? _agentDefinitions;

    public AgentAutocompleteProvider(IAgentChatFactoryProvider factoryProvider)
    {
        _factoryProvider = factoryProvider;
    }

    public AgentAutocompleteProvider(IEnumerable<IAgentDefinition> agentDefinitions)
    {
        _agentDefinitions = agentDefinitions;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<AutocompleteItem>> GetItemsAsync(string query, CancellationToken ct = default)
    {
        IEnumerable<IAgentDefinition> agents;

        if (_factoryProvider != null)
        {
            var agentDict = await _factoryProvider.GetAgentsAsync();
            agents = agentDict.Values;
        }
        else if (_agentDefinitions != null)
        {
            agents = _agentDefinitions;
        }
        else
        {
            return [];
        }

        var items = agents
            .Select(agent => new AutocompleteItem(
                Label: $"@agent/{agent.Name}",
                InsertText: $"@agent/{agent.Name} ",
                Description: agent.Description,
                Category: agent.GroupName ?? "Agents",
                Priority: agent.DisplayOrder,
                Kind: AutocompleteKind.Agent
            ));

        return items;
    }
}
