using System.ComponentModel;
using System.Text.Json;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace MeshWeaver.Blazor.AI;

/// <summary>
/// MCP wrapper for MeshPlugin. Exposes mesh operations as MCP tools.
/// Reuses logic patterns from MeshWeaver.AI.MeshPlugin but adapts for MCP context.
/// </summary>
[McpServerToolType]
public class McpMeshPlugin
{
    private readonly IMessageHub hub;
    private readonly ILogger<McpMeshPlugin> logger;
    private readonly IPersistenceService? persistence;
    private readonly IMeshQuery? meshQuery;
    private readonly string baseUrl;

    public McpMeshPlugin(
        IMessageHub hub,
        IOptions<McpConfiguration>? config = null)
    {
        this.hub = hub;
        this.logger = hub.ServiceProvider.GetRequiredService<ILogger<McpMeshPlugin>>();
        this.persistence = hub.ServiceProvider.GetService<IPersistenceService>();
        this.meshQuery = hub.ServiceProvider.GetService<IMeshQuery>();
        this.baseUrl = config?.Value.BaseUrl ?? "http://localhost:5000";
    }

    /// <summary>
    /// Resolves @ prefix to full path. Example: @graph/org1 -> graph/org1
    /// </summary>
    private static string ResolvePath(string path)
    {
        if (path.StartsWith("@"))
            return path[1..];
        return path;
    }

    [McpServerTool]
    [Description("Gets data from the mesh by path. Supports @ prefix shorthand (e.g., @graph/org1) and /* for children queries.")]
    public async Task<string> Get(
        [Description("Path to data (e.g., @graph/org1, @pricing/MS-2024, @NodeType/*)")] string path)
    {
        logger.LogInformation("MCP Get called with path={Path}", path);

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

    [McpServerTool]
    [Description("Searches the mesh using GitHub-style query syntax. Examples: 'nodeType:Agent', 'laptop', 'path:graph scope:descendants'.")]
    public async Task<string> Search(
        [Description("Query string (e.g., 'nodeType:Agent', 'laptop', 'path:graph scope:descendants')")] string query,
        [Description("Base path to search from (e.g., @graph). Empty for all.")] string? basePath = null)
    {
        logger.LogInformation("MCP Search called with query={Query}, basePath={BasePath}", query, basePath);

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
                        node.NodeType
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

    [McpServerTool]
    [Description("Creates or updates a node at a path. Provide the path and a JSON object with fields to update.")]
    public async Task<string> Update(
        [Description("Path to update (e.g., @graph/neworg)")] string path,
        [Description("JSON object with fields to update (name, nodeType, content)")] string json)
    {
        logger.LogInformation("MCP Update called with path={Path}", path);

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
            string? nodeType = null;
            object? content = null;

            if (updates.TryGetProperty("name", out var nameProp))
                name = nameProp.GetString();
            if (updates.TryGetProperty("nodeType", out var typeProp))
                nodeType = typeProp.GetString();
            if (updates.TryGetProperty("content", out var contentProp))
                content = contentProp;

            node = node with
            {
                Name = name ?? existingNode?.Name ?? node.Id,
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

    [McpServerTool]
    [Description("Returns a URL to view a node in the MeshWeaver UI. Use this to provide links for users to open in their browser.")]
    public string NavigateTo(
        [Description("Path to navigate to (e.g., @graph/org1)")] string path)
    {
        logger.LogInformation("MCP NavigateTo called with path={Path}", path);

        var resolvedPath = ResolvePath(path);
        return $"{baseUrl}/node/{Uri.EscapeDataString(resolvedPath)}";
    }
}

/// <summary>
/// Configuration options for MCP integration.
/// </summary>
public class McpConfiguration
{
    /// <summary>
    /// Base URL for the MeshWeaver UI. Used for generating NavigateTo URLs.
    /// </summary>
    public string BaseUrl { get; set; } = "http://localhost:5000";
}
