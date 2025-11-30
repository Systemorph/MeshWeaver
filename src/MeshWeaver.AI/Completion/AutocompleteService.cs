#nullable enable

namespace MeshWeaver.AI.Completion;

/// <summary>
/// Service that aggregates and scores autocomplete items from agents and file providers.
/// </summary>
public class AutocompleteService
{
    private readonly IEnumerable<IAgentDefinition> _agentDefinitions;
    private readonly FuzzyScorer _fuzzyScorer;

    // Category priorities (higher = shown first)
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
    /// <param name="query">The search query (text after the @ trigger).</param>
    /// <param name="context">The current chat context.</param>
    /// <param name="maxResults">Maximum number of results to return.</param>
    /// <returns>Scored and sorted autocomplete items.</returns>
    public async Task<IReadOnlyList<AutocompleteResult>> GetCompletionsAsync(
        string query,
        AgentContext? context,
        int maxResults = 20)
    {
        var allItems = new List<AutocompleteItem>();

        // Add agent items
        foreach (var agent in _agentDefinitions)
        {
            allItems.Add(new AutocompleteItem(
                Label: agent.Name,
                InsertText: $"@{agent.Name} ",
                Description: agent.Description,
                Category: "Agents",
                Priority: AgentCategoryPriority
            ));
        }

        // Add file items from agents that support autocompletion
        var autocompletionAgents = _agentDefinitions.OfType<IAgentWithAutocompletion>();
        foreach (var agent in autocompletionAgents)
        {
            try
            {
                var items = await agent.GetAutocompletionItemsAsync(context);
                foreach (var item in items)
                {
                    // Ensure file items have lower priority than agents
                    allItems.Add(item with { Priority = FileCategoryPriority + item.Priority });
                }
            }
            catch
            {
                // Skip agents that fail to provide items
            }
        }

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
                s.MatchPositions
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
public record AutocompleteResult(
    string Label,
    string InsertText,
    string? Description,
    string Category,
    int Score,
    int[] MatchPositions
);
