using MeshWeaver.Blazor.Components.Monaco;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Blazor.Services;

/// <summary>
/// Centralized autocomplete service for Blazor components.
/// Provides @ autocomplete for unified content references across all editors.
/// </summary>
public class BlazorAutocompleteService(IMeshQuery meshQuery)
{
    /// <summary>
    /// Gets completions for a query starting with @.
    /// Used by SearchBar, Monaco editors, and any component needing @ autocomplete.
    /// </summary>
    public async Task<CompletionItem[]> GetCompletionsAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        // Handle @ reference mode
        if (query.StartsWith("@"))
        {
            return await GetReferenceCompletionsAsync(query[1..]);
        }

        // Standard search - delegate to mesh query
        return await SearchNodesAsync(query);
    }

    /// <summary>
    /// Gets completions for @ references (without the @ prefix).
    /// </summary>
    public async Task<CompletionItem[]> GetReferenceCompletionsAsync(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            // Just "@" - show top-level nodes
            return await GetTopLevelNodesAsync();
        }

        // Check for path with trailing slash for sub-completions
        if (reference.EndsWith("/"))
        {
            var basePath = reference.TrimEnd('/');
            return await GetChildNodesAsync(basePath);
        }

        // Partial match like "@Sys" -> Systemorph
        return await GetNodesMatchingPrefixAsync(reference);
    }

    private async Task<CompletionItem[]> GetTopLevelNodesAsync()
    {
        var suggestions = await meshQuery.AutocompleteAsync("", "", 20).ToArrayAsync();
        return suggestions.Select(s => new CompletionItem
        {
            Label = s.Path,
            InsertText = $"@{s.Path}",
            Description = s.NodeType ?? s.Name,
            Detail = s.Name,
            Category = "Addresses"
        }).ToArray();
    }

    private async Task<CompletionItem[]> GetChildNodesAsync(string basePath)
    {
        var suggestions = await meshQuery.AutocompleteAsync(basePath, "", 20).ToArrayAsync();
        return suggestions.Select(s => new CompletionItem
        {
            Label = s.Path,
            InsertText = $"@{s.Path}",
            Description = s.NodeType ?? "",
            Detail = s.Name,
            Category = ""
        }).ToArray();
    }

    private async Task<CompletionItem[]> GetNodesMatchingPrefixAsync(string prefix)
    {
        // Split prefix into path and name parts
        // E.g., "Systemorph/Mark" -> basePath="Systemorph", namePrefix="Mark"
        // E.g., "System" -> basePath="", namePrefix="System"
        var lastSlash = prefix.LastIndexOf('/');
        string basePath;
        string namePrefix;

        if (lastSlash >= 0)
        {
            basePath = prefix[..lastSlash];
            namePrefix = prefix[(lastSlash + 1)..];
        }
        else
        {
            basePath = "";
            namePrefix = prefix;
        }

        var suggestions = await meshQuery.AutocompleteAsync(basePath, namePrefix, 20).ToArrayAsync();
        return suggestions.Select(s => new CompletionItem
        {
            Label = s.Path,
            InsertText = $"@{s.Path}",
            Description = s.NodeType ?? s.Name,
            Detail = s.Name,
            Category = "Addresses"
        }).ToArray();
    }

    private async Task<CompletionItem[]> SearchNodesAsync(string query)
    {
        // Use wildcard search for general queries
        var suggestions = await meshQuery.AutocompleteAsync("", query, 20).ToArrayAsync();
        return suggestions.Select(s => new CompletionItem
        {
            Label = s.Path,
            InsertText = s.Path,
            Description = s.NodeType ?? "",
            Detail = s.Name,
            Category = ""
        }).ToArray();
    }
}
