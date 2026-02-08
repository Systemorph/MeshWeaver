using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Blazor.AI;

/// <summary>
/// Provides autocomplete suggestions for mesh paths.
/// Used by MCP to suggest completions when users type mesh:// URIs or @ references.
/// </summary>
public class McpCompletionProvider
{
    private readonly IMeshQuery meshQuery;
    private readonly ILogger<McpCompletionProvider> logger;

    public McpCompletionProvider(IMeshQuery meshQuery, ILogger<McpCompletionProvider> logger)
    {
        this.meshQuery = meshQuery;
        this.logger = logger;
    }

    /// <summary>
    /// Gets autocomplete suggestions for a given prefix.
    /// </summary>
    /// <param name="prefix">The prefix to match (partial path)</param>
    /// <param name="limit">Maximum number of suggestions to return</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of path suggestions</returns>
    public async Task<IReadOnlyList<string>> GetSuggestionsAsync(
        string prefix,
        int limit = 100,
        CancellationToken ct = default)
    {
        logger.LogDebug("Getting autocomplete suggestions for prefix={Prefix}", prefix);

        var suggestions = new List<string>();

        try
        {
            await foreach (var item in meshQuery.AutocompleteAsync(
                basePath: "",
                prefix: prefix,
                mode: AutocompleteMode.RelevanceFirst,
                limit: limit,
                ct: ct))
            {
                suggestions.Add(item.Path);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error getting autocomplete suggestions for prefix={Prefix}", prefix);
        }

        return suggestions;
    }
}
