using System.ComponentModel;
using System.Text.Json;
using MeshWeaver.Data;
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
    private readonly IMeshCatalog? meshCatalog;
    private readonly string baseUrl;

    public McpMeshPlugin(
        IMessageHub hub,
        IOptions<McpConfiguration>? config = null)
    {
        this.hub = hub;
        this.logger = hub.ServiceProvider.GetRequiredService<ILogger<McpMeshPlugin>>();
        this.persistence = hub.ServiceProvider.GetService<IPersistenceService>();
        this.meshQuery = hub.ServiceProvider.GetService<IMeshQuery>();
        this.meshCatalog = hub.ServiceProvider.GetService<IMeshCatalog>();
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
    [Description("Retrieves a node from the mesh by path. Supports @ prefix shorthand, /* for children, and Unified Path prefixes (path/schema:, path/model:, path/metadata:).")]
    public async Task<string> Get(
        [Description("Path to data (e.g., @graph/org1, @Agent/*, @Cornerstone/schema:, @Cornerstone/schema:TypeName, @Cornerstone/model:)")] string path)
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

            // Check for Unified Path prefix (e.g., "ACME/schema:", "ACME/schema:TypeName", "ACME/model:")
            var unifiedResult = await TryResolveUnifiedPathAsync(resolvedPath);
            if (unifiedResult != null)
                return unifiedResult;

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

    /// <summary>
    /// Tries to resolve a path as a Unified Path with prefix (schema:, model:, metadata:).
    /// Uses meshCatalog.ResolvePathAsync to split into address and remainder,
    /// then routes data request to the resolved address.
    /// Returns null if the path is not a Unified Path.
    /// </summary>
    private async Task<string?> TryResolveUnifiedPathAsync(string resolvedPath)
    {
        if (meshCatalog == null || !resolvedPath.Contains(':'))
            return null;

        var resolution = await meshCatalog.ResolvePathAsync(resolvedPath);
        if (resolution?.Remainder == null || !resolution.Remainder.Contains(':'))
            return null;

        var reference = new UnifiedReference(resolution.Remainder);
        var address = new Address(resolution.Prefix);
        logger.LogInformation("MCP Resolving Unified Path: address={Address}, remainder={Remainder}",
            resolution.Prefix, resolution.Remainder);

        var response = await hub.AwaitResponse(
            new GetDataRequest(reference),
            o => o.WithTarget(address));

        if (response.Message.Error != null)
            return $"Error: {response.Message.Error}";

        return JsonSerializer.Serialize(response.Message.Data, hub.JsonSerializerOptions);
    }

    [McpServerTool]
    [Description("Searches the mesh using GitHub-style query syntax. Returns up to 50 matching nodes.")]
    public async Task<string> Search(
        [Description("Query string (e.g., 'nodeType:Agent', 'laptop', 'path:ACME scope:descendants', 'name:*sales*')")] string query,
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
    [Description("Creates a new node in the mesh. Pass a JSON MeshNode object with id, namespace, name, nodeType, and content fields.")]
    public async Task<string> Create(
        [Description("JSON MeshNode object to create (e.g., {\"id\": \"NewOrg\", \"namespace\": \"ACME\", \"name\": \"New Org\", \"nodeType\": \"Organization\", \"content\": {}})")] string node)
    {
        logger.LogInformation("MCP Create called");

        if (meshCatalog == null)
            return "Mesh catalog service not available.";

        try
        {
            var meshNode = JsonSerializer.Deserialize<MeshNode>(node, hub.JsonSerializerOptions);
            if (meshNode == null)
                return "Invalid node: deserialized to null.";

            var created = await meshCatalog.CreateNodeAsync(meshNode);
            return $"Created: {created.Path}";
        }
        catch (JsonException ex)
        {
            return $"Invalid JSON: {ex.Message}";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error creating node");
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool]
    [Description("Updates existing nodes in the mesh. Pass a JSON array of complete MeshNode objects. Always Get before Update — the entire node is replaced, not merged.")]
    public async Task<string> Update(
        [Description("JSON array of MeshNode objects with all fields (get existing node first, modify, then pass here)")] string nodes)
    {
        logger.LogInformation("MCP Update called");

        if (persistence == null)
            return "Persistence service not available.";

        try
        {
            var nodeList = JsonSerializer.Deserialize<List<MeshNode>>(nodes, hub.JsonSerializerOptions);
            if (nodeList == null || nodeList.Count == 0)
                return "No nodes provided.";

            var results = new List<string>();
            foreach (var node in nodeList)
            {
                var saved = await persistence.SaveNodeAsync(node);
                results.Add($"Updated: {saved.Path}");
            }

            return string.Join("\n", results);
        }
        catch (JsonException ex)
        {
            return $"Invalid JSON: {ex.Message}";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error updating nodes");
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool]
    [Description("Deletes one or more nodes from the mesh by path.")]
    public async Task<string> Delete(
        [Description("JSON array of path strings to delete (e.g., [\"ACME/OldProject\", \"ACME/ArchivedTask\"])")] string paths)
    {
        logger.LogInformation("MCP Delete called");

        if (meshCatalog == null)
            return "Mesh catalog service not available.";

        try
        {
            var pathList = JsonSerializer.Deserialize<List<string>>(paths, hub.JsonSerializerOptions);
            if (pathList == null || pathList.Count == 0)
                return "No paths provided.";

            var results = new List<string>();
            foreach (var path in pathList)
            {
                var resolvedPath = ResolvePath(path);
                await meshCatalog.DeleteNodeAsync(resolvedPath);
                results.Add($"Deleted: {resolvedPath}");
            }

            return string.Join("\n", results);
        }
        catch (JsonException ex)
        {
            return $"Invalid JSON: {ex.Message}";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error deleting nodes");
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
