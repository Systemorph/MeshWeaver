#nullable enable

using MeshWeaver.AI.Commands;

namespace MeshWeaver.AI.Completion;

/// <summary>
/// Service that aggregates and scores autocomplete items from agents and file providers.
/// </summary>
public class AutocompleteService
{
    private readonly IEnumerable<IAgentDefinition> _agentDefinitions;
    private readonly FuzzyScorer _fuzzyScorer;

    // Category priorities (higher = shown first)
    private const int CommandCategoryPriority = 2000;
    private const int ModelCategoryPriority = 1500;
    private const int AgentCategoryPriority = 1000;
    private const int FileCategoryPriority = 100;

    public AutocompleteService(
        IEnumerable<IAgentDefinition> agentDefinitions,
        FuzzyScorer fuzzyScorer)
    {
        _agentDefinitions = agentDefinitions;
        _fuzzyScorer = fuzzyScorer;
    }

    /// <summary>
    /// Gets autocomplete suggestions based on the query and current context.
    /// </summary>
    /// <param name="query">The search query (text after the @, /, or model: trigger).</param>
    /// <param name="context">The current chat context.</param>
    /// <param name="maxResults">Maximum number of results to return.</param>
    /// <param name="commandRegistry">Optional command registry for / completions.</param>
    /// <param name="availableModels">Optional list of available models for model: completions.</param>
    /// <returns>Scored and sorted autocomplete items.</returns>
    public async Task<IReadOnlyList<AutocompleteResult>> GetCompletionsAsync(
        string query,
        AgentContext? context,
        int maxResults = 20,
        ChatCommandRegistry? commandRegistry = null,
        IReadOnlyList<string>? availableModels = null)
    {
        var allItems = new List<AutocompleteItem>();

        // Determine the query type based on what trigger was used
        var isCommandQuery = query.StartsWith("/");
        var isModelQuery = query.StartsWith("@model:", StringComparison.OrdinalIgnoreCase);
        var isAgentQuery = query.StartsWith("@agent:", StringComparison.OrdinalIgnoreCase);
        var isGenericAtQuery = query.StartsWith("@") && !isModelQuery && !isAgentQuery;

        // Add command items if it's a / query
        if (isCommandQuery && commandRegistry != null)
        {
            foreach (var command in commandRegistry.GetAllCommands())
            {
                allItems.Add(new AutocompleteItem(
                    Label: $"/{command.Name}",
                    InsertText: $"/{command.Name} ",
                    Description: command.Description,
                    Category: "Commands",
                    Priority: CommandCategoryPriority,
                    Kind: AutocompleteKind.Command
                ));
            }
        }

        // Add model items if it's a @model: query or generic @ query
        if ((isModelQuery || isGenericAtQuery) && availableModels != null)
        {
            foreach (var model in availableModels)
            {
                allItems.Add(new AutocompleteItem(
                    Label: $"@model:{model}",
                    InsertText: $"@model:{model} ",
                    Description: $"AI Model",
                    Category: "Models",
                    Priority: ModelCategoryPriority,
                    Kind: AutocompleteKind.Other
                ));
            }
        }

        // Add agent items (for @agent: query or generic @ query)
        if (isAgentQuery || isGenericAtQuery)
        {
            foreach (var agent in _agentDefinitions)
            {
                allItems.Add(new AutocompleteItem(
                    Label: $"@agent:{agent.Name}",
                    InsertText: $"@agent:{agent.Name} ",
                    Description: agent.Description,
                    Category: "Agents",
                    Priority: AgentCategoryPriority,
                    Kind: AutocompleteKind.Agent
                ));
            }
        }

        // Add file items from agents that support autocompletion (only for @ queries or general queries)
        if (!isCommandQuery && !isModelQuery)
        {
            var autocompletionAgents = _agentDefinitions.OfType<IAgentWithAutocompletion>();
            foreach (var agent in autocompletionAgents)
            {
                try
                {
                    var items = await agent.GetAutocompletionItemsAsync(context);
                    foreach (var item in items)
                    {
                        // Ensure file items have lower priority than agents
                        // Set Kind to File if not already set
                        var kind = item.Kind == AutocompleteKind.Other ? AutocompleteKind.File : item.Kind;
                        allItems.Add(item with { Priority = FileCategoryPriority + item.Priority, Kind = kind });
                    }
                }
                catch
                {
                    // Skip agents that fail to provide items
                }
            }
        }

        // Deduplicate by InsertText (keep highest priority item)
        allItems = allItems
            .GroupBy(i => i.InsertText)
            .Select(g => g.OrderByDescending(i => i.Priority).First())
            .ToList();

        // Apply fuzzy scoring
        var scored = _fuzzyScorer.Score(
            allItems,
            query,
            item => item.Label
        );

        // Sort by: category priority (desc), then fuzzy score (desc)
        var results = scored
            .OrderByDescending(s => s.Item.Priority)
            .ThenByDescending(s => s.Score)
            .Take(maxResults)
            .Select(s => new AutocompleteResult(
                s.Item.Label,
                s.Item.InsertText,
                s.Item.Description,
                s.Item.Category,
                s.Score,
                s.MatchPositions,
                s.Item.Kind
            ))
            .ToList();

        return results;
    }
}

/// <summary>
/// Represents a scored autocomplete result ready for display.
/// </summary>
/// <param name="Label">Display text shown in the autocomplete dropdown.</param>
/// <param name="InsertText">Text that gets inserted when the item is selected.</param>
/// <param name="Description">Additional description shown in the dropdown.</param>
/// <param name="Category">Category for grouping (e.g., "Agents", "Files").</param>
/// <param name="Score">The fuzzy match score (higher is better).</param>
/// <param name="MatchPositions">Positions of matched characters for highlighting.</param>
/// <param name="Kind">The kind of item (Agent, File, Command) - determines icon.</param>
public record AutocompleteResult(
    string Label,
    string InsertText,
    string? Description,
    string Category,
    int Score,
    int[] MatchPositions,
    AutocompleteKind Kind
);
