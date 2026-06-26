using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Blazor.AI;

/// <summary>
/// Provides autocomplete suggestions for mesh paths.
/// Used by MCP to suggest completions when users type mesh:// URIs or @ references.
/// </summary>
public class McpCompletionProvider
{
    private readonly IMeshService meshQuery;
    private readonly ILogger<McpCompletionProvider> logger;

    /// <summary>
    /// Initializes a new instance of the <c>McpCompletionProvider</c>.
    /// </summary>
    /// <param name="meshQuery">The mesh service used to run autocomplete queries.</param>
    /// <param name="logger">The logger for diagnostics.</param>
    public McpCompletionProvider(IMeshService meshQuery, ILogger<McpCompletionProvider> logger)
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
    public Task<IReadOnlyList<string>> GetSuggestionsAsync(
        string prefix,
        int limit = 100,
        CancellationToken ct = default)
    {
        logger.LogDebug("Getting autocomplete suggestions for prefix={Prefix}", prefix);

        // Compose the Autocomplete observable — never await-foreach a hub query (deadlock).
        // This is the MCP/SDK boundary, the one sanctioned single-point Task bridge
        // (.FirstAsync().ToTask(); see AsynchronousCalls.md). The first snapshot of
        // suggestions is projected to paths.
        return meshQuery
            .Autocomplete(basePath: "", prefix: prefix, mode: AutocompleteMode.RelevanceFirst, limit: limit)
            .TakeLast(1)
            .Select(rows => (IReadOnlyList<string>)rows.Select(r => r.Path).ToList())
            .Catch((Exception ex) =>
            {
                logger.LogWarning(ex, "Error getting autocomplete suggestions for prefix={Prefix}", prefix);
                return Observable.Return<IReadOnlyList<string>>([]);
            })
            .FirstAsync()
            .ToTask(ct);
    }
}
