using System.ComponentModel;
using System.Text.Json;
using MeshWeaver.AI.Services;
using MeshWeaver.Data;
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
    private readonly IPersistenceService? persistence = hub.ServiceProvider.GetService<IMeshCatalog>()?.Persistence;

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
                await foreach (var node in persistence.GetChildrenAsync(parentPath))
                {
                    result.Add(new
                    {
                        node.Path,
                        node.Name,
                        node.NodeType,
                        node.Description,
                        node.IconName
                    });
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

    [Description("Searches the mesh using RSQL query or text search. " +
                 "RSQL examples: 'nodeType==Organization', 'name=like=*acme*', 'status==active;price=gt=100'. " +
                 "Use $search=text for fuzzy search, $scope=descendants to include children.")]
    public async Task<string> Search(
        [Description("RSQL query or search text (e.g., 'nodeType==Agent', '$search=laptop')")] string query,
        [Description("Base path to search from (e.g., @graph). Empty for all.")] string? basePath = null)
    {
        logger.LogInformation("Search called with query={Query}, basePath={BasePath}", query, basePath);

        if (persistence == null)
            return "Persistence service not available.";

        var resolvedBase = basePath != null ? ResolvePath(basePath) : "";

        try
        {
            var results = new List<object>();
            await foreach (var item in persistence.QueryAsync(query, resolvedBase))
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

                if (results.Count >= 50)
                    break; // Limit results
            }

            return JsonSerializer.Serialize(results, hub.JsonSerializerOptions);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error searching with query {Query}", query);
            return $"Error: {ex.Message}";
        }
    }

    [Description("Deletes a node at the specified path.")]
    public async Task<string> Delete(
        [Description("Path to delete (e.g., @graph/org1)")] string path,
        [Description("If true, also deletes all child nodes")] bool recursive = false)
    {
        logger.LogInformation("Delete called with path={Path}, recursive={Recursive}", path, recursive);

        if (persistence == null)
            return "Persistence service not available.";

        var resolvedPath = ResolvePath(path);

        try
        {
            if (!await persistence.ExistsAsync(resolvedPath))
                return $"Not found: {resolvedPath}";

            await persistence.DeleteNodeAsync(resolvedPath, recursive);
            return $"Deleted: {resolvedPath}" + (recursive ? " (including children)" : "");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error deleting at path {Path}", resolvedPath);
            return $"Error: {ex.Message}";
        }
    }

    [Description("Test agent discovery - finds agents in the namespace hierarchy for a given context path. " +
                 "Returns agents ordered by proximity (closest first). " +
                 "Use this to verify which agent would be selected for a given context.")]
    public async Task<string> TestAgentDiscovery(
        [Description("Context path to search from (e.g., ACME/Projects/Alpha, @graph/org1)")] string contextPath)
    {
        logger.LogInformation("TestAgentDiscovery called with contextPath={ContextPath}", contextPath);

        var agentResolver = hub.ServiceProvider.GetService<IAgentResolver>();
        if (agentResolver == null)
            return "IAgentResolver service not available.";

        var resolvedPath = ResolvePath(contextPath);

        try
        {
            // Get the closest agent
            var closestAgent = await agentResolver.GetClosestAgentAsync(resolvedPath);

            // Get all agents in hierarchy ordered by depth (closest first)
            var hierarchyAgents = await agentResolver.GetHierarchyAgentsAsync(resolvedPath);

            var result = new
            {
                contextPath = resolvedPath,
                closestAgent = closestAgent != null ? new
                {
                    closestAgent.Id,
                    closestAgent.DisplayName,
                    closestAgent.Description,
                    closestAgent.IsDefault
                } : null,
                hierarchyAgents = hierarchyAgents.Select(a => new
                {
                    a.Id,
                    a.DisplayName,
                    a.Description,
                    a.IsDefault,
                    delegationCount = a.Delegations?.Count ?? 0
                }).ToList()
            };

            return JsonSerializer.Serialize(result, hub.JsonSerializerOptions);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error testing agent discovery for context {ContextPath}", resolvedPath);
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Creates all tools for this plugin.
    /// </summary>
    public IList<AITool> CreateTools()
    {
        return
        [
            AIFunctionFactory.Create(Get),
            AIFunctionFactory.Create(Update),
            AIFunctionFactory.Create(NavigateTo),
            AIFunctionFactory.Create(Search),
            AIFunctionFactory.Create(Delete),
            AIFunctionFactory.Create(TestAgentDiscovery)
        ];
    }
}
