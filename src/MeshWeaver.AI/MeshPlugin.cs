using System.ComponentModel;
using System.Text.Json;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI;

/// <summary>
/// Simplified plugin providing mesh operations for AI agents.
/// Uses unified paths with GetDataRequest/DataChangeRequest.
/// Supports @ prefix shorthand (e.g., @graph/org1 -> graph/org1).
/// </summary>
public class MeshPlugin(IMessageHub hub, IAgentChat chat)
{
    private readonly ILogger<MeshPlugin> logger = hub.ServiceProvider.GetRequiredService<ILogger<MeshPlugin>>();
    private readonly IPersistenceService? persistence = hub.ServiceProvider.GetService<IPersistenceService>();
    private readonly IMeshQuery? meshQuery = hub.ServiceProvider.GetService<IMeshQuery>();

    private const string NodesArea = "_Nodes";

    /// <summary>
    /// Resolves @ prefix to full path. Example: @graph/org1 -> graph/org1
    /// </summary>
    private static string ResolvePath(string path)
    {
        if (path.StartsWith("@"))
            return path[1..];
        return path;
    }

    [Description("Gets data from the mesh by path. Returns JSON. " +
                 "Use @ prefix for shorthand (e.g., @graph/org1). " +
                 "Add /* suffix to get children (e.g., @graph/*).")]
    public async Task<string> Get(
        [Description("Path to data (e.g., @graph/org1, @pricing/MS-2024, @NodeType/*)")] string path)
    {
        logger.LogInformation("Get called with path={Path}", path);

        if (persistence == null)
            return "Persistence service not available.";

        var resolvedPath = ResolvePath(path);

        try
        {
            // Handle children query (path/*)
            if (resolvedPath.EndsWith("/*"))
            {
                var parentPath = resolvedPath[..^2];
                var result = new List<object>();
                if (meshQuery != null)
                {
                    var query = $"path:{parentPath} scope:children";
                    await foreach (var node in meshQuery.QueryAsync<MeshNode>(MeshQueryRequest.FromQuery(query)))
                    {
                        result.Add(new
                        {
                            node.Path,
                            node.Name,
                            node.NodeType,
                            node.Description,
                            node.Icon
                        });
                    }
                }
                return JsonSerializer.Serialize(result, hub.JsonSerializerOptions);
            }

            // Get single node
            var meshNode = await persistence.GetNodeAsync(resolvedPath);
            if (meshNode == null)
                return $"Not found: {resolvedPath}";

            return JsonSerializer.Serialize(meshNode, hub.JsonSerializerOptions);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error getting data at path {Path}", resolvedPath);
            return $"Error: {ex.Message}";
        }
    }

    [Description("Updates or creates data at the specified path. " +
                 "Provide a JSON object with the fields to update. " +
                 "For nodes: {\"name\": \"...\", \"description\": \"...\", \"nodeType\": \"...\", \"content\": {...}}")]
    public async Task<string> Update(
        [Description("Path to update (e.g., @graph/neworg)")] string path,
        [Description("JSON object with fields to update")] string json)
    {
        logger.LogInformation("Update called with path={Path}", path);

        if (persistence == null)
            return "Persistence service not available.";

        var resolvedPath = ResolvePath(path);

        try
        {
            // Parse the update JSON
            var updates = JsonSerializer.Deserialize<JsonElement>(json, hub.JsonSerializerOptions);

            // Get existing node or create new one
            var existingNode = await persistence.GetNodeAsync(resolvedPath);
            var isCreate = existingNode == null;

            // Build the node from updates
            var node = MeshNode.FromPath(resolvedPath);

            // Apply updates from JSON
            string? name = null;
            string? description = null;
            string? nodeType = null;
            object? content = null;

            if (updates.TryGetProperty("name", out var nameProp))
                name = nameProp.GetString();
            if (updates.TryGetProperty("description", out var descProp))
                description = descProp.GetString();
            if (updates.TryGetProperty("nodeType", out var typeProp))
                nodeType = typeProp.GetString();
            if (updates.TryGetProperty("content", out var contentProp))
                content = contentProp;

            node = node with
            {
                Name = name ?? existingNode?.Name ?? node.Id,
                Description = description ?? existingNode?.Description,
                NodeType = nodeType ?? existingNode?.NodeType,
                Content = content ?? existingNode?.Content
            };

            await persistence.SaveNodeAsync(node);
            return isCreate
                ? $"Created: {resolvedPath}"
                : $"Updated: {resolvedPath}";
        }
        catch (JsonException ex)
        {
            return $"Invalid JSON: {ex.Message}";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error updating data at path {Path}", resolvedPath);
            return $"Error: {ex.Message}";
        }
    }

    [Description("Navigates to a path in the UI. Displays the node's view.")]
    public string NavigateTo(
        [Description("Path to navigate to (e.g., @graph/org1)")] string path)
    {
        logger.LogInformation("NavigateTo called with path={Path}", path);

        var resolvedPath = ResolvePath(path);
        var address = new Address(resolvedPath);
        var layoutControl = Controls.LayoutArea(address, NodesArea);

        chat.DisplayLayoutArea(layoutControl);
        return $"Navigating to: {resolvedPath}";
    }

    [Description("Searches the mesh using GitHub-style query syntax. " +
                 "Examples: 'nodeType:Organization', 'name:*acme*', 'status:active'. " +
                 "Use 'text' for text search, 'scope:descendants' to include children.")]
    public async Task<string> Search(
        [Description("Query string (e.g., 'nodeType:Agent', 'laptop', 'path:graph scope:descendants')")] string query,
        [Description("Base path to search from (e.g., @graph). Empty for all.")] string? basePath = null)
    {
        logger.LogInformation("Search called with query={Query}, basePath={BasePath}", query, basePath);

        if (meshQuery == null)
            return "Query service not available.";

        // Build the full query with path prefix if basePath is provided
        var resolvedBase = basePath != null ? ResolvePath(basePath) : null;
        var fullQuery = string.IsNullOrEmpty(resolvedBase) ? query : $"path:{resolvedBase} {query}";

        try
        {
            var results = new List<object>();
            await foreach (var item in meshQuery.QueryAsync(new MeshQueryRequest { Query = fullQuery, Limit = 50 }))
            {
                if (item is MeshNode node)
                {
                    results.Add(new
                    {
                        node.Path,
                        node.Name,
                        node.NodeType,
                        node.Description
                    });
                }
                else
                {
                    results.Add(item);
                }
            }

            return JsonSerializer.Serialize(results, hub.JsonSerializerOptions);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error searching with query {Query}", query);
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Creates the standard tools for this plugin (read-only operations).
    /// </summary>
    public IList<AITool> CreateTools()
    {
        return
        [
            AIFunctionFactory.Create(Get),
            AIFunctionFactory.Create(Search),
            AIFunctionFactory.Create(NavigateTo)
        ];
    }

    /// <summary>
    /// Creates all tools including write operations (for Executor agent).
    /// </summary>
    public IList<AITool> CreateAllTools()
    {
        return
        [
            AIFunctionFactory.Create(Get),
            AIFunctionFactory.Create(Search),
            AIFunctionFactory.Create(NavigateTo),
            AIFunctionFactory.Create(Update)
        ];
    }
}
