#nullable enable

using MeshWeaver.AI.Services;
using MeshWeaver.Data.Completion;
using MeshWeaver.Graph.Configuration;

namespace MeshWeaver.AI.Completion;

/// <summary>
/// Provides autocomplete items for agents.
/// Gets agents from IAgentChatFactoryProvider or IAgentResolver.
/// </summary>
public class AgentAutocompleteProvider : IAutocompleteProvider
{
    private readonly IAgentChatFactoryProvider? _factoryProvider;
    private readonly IAgentResolver? _agentResolver;

    public AgentAutocompleteProvider(IAgentChatFactoryProvider factoryProvider)
    {
        _factoryProvider = factoryProvider;
    }

    public AgentAutocompleteProvider(IAgentResolver agentResolver)
    {
        _agentResolver = agentResolver;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<AutocompleteItem>> GetItemsAsync(string query, CancellationToken ct = default)
    {
        IReadOnlyList<AgentConfiguration> agents;

        if (_factoryProvider != null)
        {
            agents = await _factoryProvider.GetAgentsAsync();
        }
        else if (_agentResolver != null)
        {
            agents = await _agentResolver.GetAgentsForContextAsync(null, ct);
        }
        else
        {
            return [];
        }

        var items = agents
            .Select(agent => new AutocompleteItem(
                Label: $"@agent/{agent.Id}",
                InsertText: $"@agent/{agent.Id} ",
                Description: agent.Description ?? string.Empty,
                Category: agent.GroupName ?? "Agents",
                Priority: agent.DisplayOrder,
                Kind: AutocompleteKind.Agent
            ));

        return items;
    }
}
