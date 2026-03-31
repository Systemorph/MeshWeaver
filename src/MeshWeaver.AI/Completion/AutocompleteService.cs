#nullable enable

using System.Collections.Immutable;
using MeshWeaver.Data;
using MeshWeaver.Data.Completion;

namespace MeshWeaver.AI.Completion;

/// <summary>
/// Service that aggregates autocomplete results from registered providers and applies fuzzy matching.
/// This service no longer contains hard-coded completion logic - all completions are delegated to IAutocompleteProvider implementations.
/// </summary>
public class AutocompleteService(
    FuzzyScorer fuzzyScorer,
    IEnumerable<IAutocompleteProvider> providers)
{
    /// <summary>
    /// Gets autocomplete suggestions by aggregating results from all registered providers.
    /// </summary>
    /// <param name="request">The autocomplete request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Response containing autocomplete items with fuzzy scoring applied.</returns>
    public async Task<AutocompleteResponse> GetCompletionsAsync(
        AutocompleteRequest request,
        CancellationToken ct = default)
    {
        var results = await GetCompletionsInternalAsync(request.Query, request.Context, ct);

        // Convert AutocompleteResult to AutocompleteItem for the response
        var items = results.Select(r => new AutocompleteItem(
            Label: r.Label,
            InsertText: r.InsertText,
            Description: r.Description,
            Category: r.Category,
            Priority: r.Score,
            Kind: r.Kind,
            Icon: r.Icon,
            Path: r.Path
        )).ToList();

        return new AutocompleteResponse(items);
    }

    /// <summary>
    /// Gets autocomplete suggestions with scoring for display.
    /// </summary>
    /// <param name="query">The search query.</param>
    /// <param name="maxResults">Maximum number of results to return.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of scored autocomplete results.</returns>
    public async Task<IReadOnlyList<AutocompleteResult>> GetCompletionsAsync(
        string query,
        int maxResults = 20,
        CancellationToken ct = default)
    {
        return await GetCompletionsInternalAsync(query, null, ct, maxResults);
    }

    private async Task<IReadOnlyList<AutocompleteResult>> GetCompletionsInternalAsync(
        string query,
        string? contextPath,
        CancellationToken ct,
        int maxResults = 20)
    {
        var allItems = ImmutableList<AutocompleteItem>.Empty;

        // Collect items from all registered providers
        foreach (var provider in providers)
        {
            try
            {
                await foreach (var item in provider.GetItemsAsync(query, contextPath, ct))
                {
                    allItems = allItems.Add(item);
                }
            }
            catch
            {
                // Skip providers that fail
            }
        }

        // Deduplicate by InsertText (keep highest priority item)
        allItems = allItems
            .GroupBy(i => i.InsertText)
            .Select(g => g.OrderByDescending(i => i.Priority).First())
            .ToImmutableList();

        // Apply fuzzy scoring
        var scored = fuzzyScorer.Score(
            allItems,
            query,
            item => item.Label
        );

        // Sort by: priority (desc), then fuzzy score (desc)
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
                s.Item.Kind,
                s.Item.Icon,
                s.Item.Path
            ))
            .ToList();

        return results;
    }
}

/// <summary>
/// Represents a scored autocomplete result ready for display.
/// </summary>
public record AutocompleteResult(
    string Label,
    string InsertText,
    string? Description,
    string Category,
    int Score,
    int[] MatchPositions,
    AutocompleteKind Kind,
    string? Icon = null,
    string? Path = null
);
