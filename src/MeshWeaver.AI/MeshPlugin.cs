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
    private readonly IMeshCatalog? meshCatalog = hub.ServiceProvider.GetService<IMeshCatalog>();

    /// <summary>
    /// Resolves @ prefix to full path. Example: @graph/org1 -> graph/org1
    /// </summary>
    private static string ResolvePath(string path)
    {
        if (path.StartsWith("@"))
            return path[1..];
        return path;
    }

    [Description("Gets data from the mesh by path. " +
                 "@@MeshWeaver/Documentation/AI/Tools/MeshPlugin#Get")]
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

    [Description("Creates or updates a node at a path. " +
                 "@@MeshWeaver/Documentation/AI/Tools/MeshPlugin#Update")]
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

    [Description("Displays a node's view in the chat UI. " +
                 "@@MeshWeaver/Documentation/AI/Tools/MeshPlugin#NavigateTo")]
    public string NavigateTo(
        [Description("Path to navigate to (e.g., @graph/org1)")] string path)
    {
        logger.LogInformation("NavigateTo called with path={Path}", path);

        var resolvedPath = ResolvePath(path);
        var address = new Address(resolvedPath);
        var layoutControl = Controls.LayoutArea(address, string.Empty);

        chat.DisplayLayoutArea(layoutControl);
        return $"Navigating to: {resolvedPath}";
    }

    [Description("Sets the name for the current conversation thread. " +
                 "Call this to give the thread a meaningful name based on the conversation topic.")]
    public async Task<string> SetThreadName(
        [Description("Descriptive name for the thread (3-8 words)")] string name,
        [Description("PascalCase identifier derived from the name (alphanumeric only). If omitted, auto-generated from name.")] string? id = null)
    {
        logger.LogInformation("SetThreadName called with name={Name}, id={Id}", name, id);

        if (meshCatalog == null)
            return "Mesh catalog not available.";

        try
        {
            // Generate id from name if not provided
            if (string.IsNullOrWhiteSpace(id))
                id = GenerateIdFromName(name);

            // Determine namespace from current context
            var contextAddress = chat.Context?.Address?.ToString();
            var ns = contextAddress?.Split('/').FirstOrDefault(s => !string.IsNullOrEmpty(s)) ?? "";
            var threadPath = string.IsNullOrEmpty(ns) ? id : $"{ns}/{id}";

            var threadContent = new Thread
            {
                ParentPath = string.IsNullOrEmpty(ns) ? null : ns
            };

            var newNode = new MeshNode(threadPath)
            {
                Name = name,
                NodeType = ThreadNodeType.NodeType,
                Content = threadContent
            };

            var createdNode = await meshCatalog.CreateNodeAsync(newNode);
            chat.SetThreadId(createdNode.Path);

            logger.LogInformation("SetThreadName created thread at path={Path}", createdNode.Path);
            return $"Thread created: {createdNode.Path} (Name: {name})";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error in SetThreadName");
            return $"Error: {ex.Message}";
        }
    }

    private static string GenerateIdFromName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Thread" + DateTime.UtcNow.Ticks;

        var words = System.Text.RegularExpressions.Regex.Split(name, @"[\s\-_]+")
            .Where(w => !string.IsNullOrEmpty(w))
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant());

        var pascalCase = string.Join("", words);
        pascalCase = System.Text.RegularExpressions.Regex.Replace(pascalCase, @"[^a-zA-Z0-9]", "");

        return string.IsNullOrEmpty(pascalCase) ? "Thread" + DateTime.UtcNow.Ticks : pascalCase;
    }

    [Description("Searches the mesh using query syntax. " +
                 "@@MeshWeaver/Documentation/AI/Tools/MeshPlugin#Search")]
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

    /// <summary>
    /// Creates the standard tools for this plugin (read-only operations + thread naming).
    /// </summary>
    public IList<AITool> CreateTools()
    {
        return
        [
            AIFunctionFactory.Create(Get),
            AIFunctionFactory.Create(Search),
            AIFunctionFactory.Create(NavigateTo),
            AIFunctionFactory.Create(SetThreadName)
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
            AIFunctionFactory.Create(Update),
            AIFunctionFactory.Create(SetThreadName)
        ];
    }
}
