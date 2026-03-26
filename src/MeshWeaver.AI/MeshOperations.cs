using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI;

/// <summary>
/// Shared mesh operations for AI agents and MCP tools.
/// All operations go through Hub messaging to enforce security via validators.
/// </summary>
public class MeshOperations
{
    private readonly IMessageHub hub;
    private readonly ILogger<MeshOperations> logger;
    private readonly IMeshService mesh;

    public MeshOperations(IMessageHub hub)
    {
        this.hub = hub;
        this.logger = hub.ServiceProvider.GetRequiredService<ILogger<MeshOperations>>();
        this.mesh = hub.ServiceProvider.GetRequiredService<IMeshService>();
    }

    /// <summary>
    /// Resolves @ prefix to full path. Example: @graph/org1 -> graph/org1
    /// </summary>
    public static string ResolvePath(string path)
    {
        if (path.StartsWith("@"))
            return path[1..];
        return path;
    }

    public async Task<string> Get(string path)
    {
        logger.LogInformation("Get called with path={Path}", path);

        var resolvedPath = ResolvePath(path);

        try
        {
            // Handle children query (path/*)
            if (resolvedPath.EndsWith("/*"))
            {
                var parentPath = resolvedPath[..^2];
                var result = new List<object>();
                var query = $"namespace:{parentPath}";
                await foreach (var node in mesh.QueryAsync<MeshNode>(MeshQueryRequest.FromQuery(query)))
                {
                    result.Add(new
                    {
                        node.Path,
                        node.Name,
                        node.NodeType,
                        node.Icon
                    });
                }
                return JsonSerializer.Serialize(result, hub.JsonSerializerOptions);
            }

            // Check for Unified Path prefix (e.g., "ACME/schema:", "ACME/data:Collection/id")
            var unifiedResult = await TryResolveUnifiedPathAsync(resolvedPath);
            if (unifiedResult != null)
                return unifiedResult;

            // Get single node via query (reads from persistence, not cached)
            await foreach (var node in mesh.QueryAsync<MeshNode>(
                MeshQueryRequest.FromQuery($"path:{resolvedPath}")))
            {
                return JsonSerializer.Serialize(node, hub.JsonSerializerOptions);
            }

            return $"Not found: {resolvedPath}";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error getting data at path {Path}", resolvedPath);
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Tries to resolve a path as a Unified Path with prefix (schema:, model:, data:).
    /// Parses the path to find the colon separator, splits into address and remainder,
    /// then routes data request to the resolved address.
    /// Returns null if the path is not a Unified Path.
    /// </summary>
    private async Task<string?> TryResolveUnifiedPathAsync(string resolvedPath)
    {
        var colonIndex = resolvedPath.IndexOf(':');
        if (colonIndex < 0)
            return null;

        // Find the last '/' before the colon — separates address from prefix:path
        var slashBeforeColon = resolvedPath.LastIndexOf('/', colonIndex);
        if (slashBeforeColon < 0)
            return null; // No address part

        var addressPart = resolvedPath[..slashBeforeColon];
        var remainder = resolvedPath[(slashBeforeColon + 1)..];

        var reference = new UnifiedReference(remainder);
        var address = new Address(addressPart);
        logger.LogInformation("Resolving Unified Path: address={Address}, remainder={Remainder}",
            addressPart, remainder);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var delivery = hub.Post(
            new GetDataRequest(reference),
            o => o.WithTarget(address))!;
        var callbackResponse = await hub.RegisterCallback(delivery, (d, _) => Task.FromResult(d), cts.Token);
        var responseMsg = ((IMessageDelivery<GetDataResponse>)callbackResponse).Message;

        if (responseMsg.Error != null)
            return $"Error: {responseMsg.Error}";

        return JsonSerializer.Serialize(responseMsg.Data, hub.JsonSerializerOptions);
    }

    public async Task<string> Search(string query, string? basePath = null)
    {
        logger.LogInformation("Search called with query={Query}, basePath={BasePath}", query, basePath);

        var resolvedBase = basePath != null ? ResolvePath(basePath) : null;
        string fullQuery;
        if (string.IsNullOrEmpty(resolvedBase))
        {
            fullQuery = query;
        }
        else
        {
            // Remove empty namespace: placeholder — basePath provides the namespace context.
            // Use namespace: (not path:) so scope defaults to Children (search within, not exact).
            var cleanQuery = query.Replace("namespace:", "").Trim();
            fullQuery = $"namespace:{resolvedBase} {cleanQuery}".Trim();
        }

        try
        {
            var results = new List<object>();
            await foreach (var item in mesh.QueryAsync(new MeshQueryRequest { Query = fullQuery, Limit = 50 }))
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

    public async Task<string> Create(string node)
    {
        logger.LogInformation("Create called");

        try
        {
            var sanitized = RepairJson(node);
            var meshNode = JsonSerializer.Deserialize<MeshNode>(sanitized, hub.JsonSerializerOptions);
            if (meshNode == null)
                return "Invalid node: deserialized to null.";

            if (string.IsNullOrWhiteSpace(meshNode.Name))
                return "Error: 'name' property is required. Provide a human-readable display name.";

            if (meshNode.Id.Contains('/'))
                return $"Error: 'id' must not contain slashes. Got '{meshNode.Id}'. Use 'namespace' for the parent path and 'id' for just the node name.";

            // Validate content against schema if both nodeType and content are provided
            if (!string.IsNullOrEmpty(meshNode.NodeType) && meshNode.Content != null)
            {
                var validationError = await ValidateContentAgainstSchemaAsync(meshNode);
                if (validationError != null)
                    return validationError;
            }

            var created = await mesh.CreateNodeAsync(meshNode);
            return $"Created: {created.Path}";
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Create: invalid JSON, length={Length}", node.Length);
            return $"Invalid JSON: {ex.Message}. Tip: ensure all quotes and special characters in markdown content are properly escaped for JSON strings.";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error creating node");
            return $"Error: {ex.Message}";
        }
    }

    public async Task<string> Update(string nodes)
    {
        logger.LogInformation("Update called");

        try
        {
            var sanitized = RepairJson(nodes);
            var nodeList = JsonSerializer.Deserialize<List<MeshNode>>(sanitized, hub.JsonSerializerOptions);
            if (nodeList == null || nodeList.Count == 0)
                return "No nodes provided.";

            var results = new List<string>();
            foreach (var meshNode in nodeList)
            {
                // Reject partial nodes — Update does full replacement.
                // Use Patch for partial changes instead.
                if (string.IsNullOrEmpty(meshNode.NodeType) || meshNode.Content == null)
                {
                    var missing = new List<string>();
                    if (string.IsNullOrEmpty(meshNode.NodeType)) missing.Add("nodeType");
                    if (meshNode.Content == null) missing.Add("content");
                    results.Add($"Error: node at {meshNode.Path} is missing {string.Join(", ", missing)}. " +
                                "Update requires the complete node (from Get). Use Patch for partial updates.");
                    continue;
                }

                var updated = await mesh.UpdateNodeAsync(meshNode);
                results.Add($"Updated: {updated.Path}");
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

    public async Task<string> Patch(string path, string fields)
    {
        logger.LogInformation("Patch called for path={Path}", path);

        try
        {
            var resolvedPath = ResolvePath(path);
            var existing = await mesh.QueryAsync<MeshNode>($"path:{resolvedPath}").FirstOrDefaultAsync();
            if (existing == null)
                return $"Error: node not found at {resolvedPath}";

            var sanitized = RepairJson(fields);
            var jsonObj = JsonNode.Parse(sanitized) as JsonObject;
            if (jsonObj == null)
                return "Error: fields must be a JSON object";

            // Deserialize to get typed values using the hub's serializer options
            var partial = jsonObj.Deserialize<MeshNode>(hub.JsonSerializerOptions)
                ?? new MeshNode(existing.Id, existing.Namespace);

            var merged = existing with
            {
                Name = jsonObj.ContainsKey("name") ? partial.Name : existing.Name,
                Icon = jsonObj.ContainsKey("icon") ? partial.Icon : existing.Icon,
                Category = jsonObj.ContainsKey("category") ? partial.Category : existing.Category,
                Order = jsonObj.ContainsKey("order") ? partial.Order : existing.Order,
                Content = jsonObj.ContainsKey("content") ? partial.Content : existing.Content,
                PreRenderedHtml = jsonObj.ContainsKey("preRenderedHtml") ? partial.PreRenderedHtml : existing.PreRenderedHtml,
            };

            var updated = await mesh.UpdateNodeAsync(merged);
            return $"Patched: {updated.Path}";
        }
        catch (JsonException ex)
        {
            return $"Invalid JSON: {ex.Message}";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error patching node at {Path}", path);
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Attempts to repair common JSON issues from LLM output:
    /// - Truncated strings (unclosed quotes/braces)
    /// - Unescaped control characters inside strings
    /// </summary>
    private static string RepairJson(string json)
    {
        if (string.IsNullOrEmpty(json))
            return json;

        // Try parsing first — if it's valid, return as-is
        try
        {
            using var doc = JsonDocument.Parse(json);
            return json;
        }
        catch (JsonException)
        {
            // Fall through to repair
        }

        // Repair: try truncating to last complete JSON structure
        // Find the last closing brace/bracket that makes valid JSON
        for (var i = json.Length - 1; i > 0; i--)
        {
            if (json[i] is '}' or ']')
            {
                var candidate = json[..(i + 1)];
                try
                {
                    using var doc = JsonDocument.Parse(candidate);
                    return candidate;
                }
                catch (JsonException)
                {
                    // Try next position
                }
            }
        }

        return json; // Return original if repair fails
    }

    public async Task<string> Delete(string paths)
    {
        logger.LogInformation("Delete called");

        try
        {
            var pathList = JsonSerializer.Deserialize<List<string>>(paths, hub.JsonSerializerOptions);
            if (pathList == null || pathList.Count == 0)
                return "No paths provided.";

            var results = new List<string>();
            foreach (var path in pathList)
            {
                var resolvedPath = ResolvePath(path);
                await mesh.DeleteNodeAsync(resolvedPath);
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

    /// <summary>
    /// Validates node content against the content type for its NodeType.
    /// Creates a temporary hub with the NodeType's configuration to find the
    /// registered content type, then attempts to deserialize the content into that type.
    /// Returns an error message if invalid, or null if valid/no schema available.
    /// </summary>
    internal Task<string?> ValidateContentAgainstSchemaAsync(MeshNode meshNode)
    {
        try
        {
            var nodeTypeService = hub.ServiceProvider.GetService<INodeTypeService>();
            if (nodeTypeService == null)
                return Task.FromResult<string?>(null);

            var hubConfig = nodeTypeService.GetCachedHubConfiguration(meshNode.NodeType!);
            if (hubConfig == null)
                return Task.FromResult<string?>(null);

            // Create a temporary hub with the NodeType's config to access its type registry
            var tempAddress = new Address($"_schema_validation/{Guid.NewGuid():N}");
            var tempHub = hub.GetHostedHub(tempAddress, hubConfig);
            if (tempHub == null)
                return Task.FromResult<string?>(null);

            try
            {
                // Find the content type from the hub's type registry
                var typeRegistry = tempHub.ServiceProvider.GetService<ITypeRegistry>();
                if (typeRegistry == null || !typeRegistry.TryGetType(meshNode.NodeType!, out var typeDefinition))
                    return Task.FromResult<string?>(null);

                var contentType = typeDefinition!.Type;

                // Serialize content to JSON and try to deserialize into the target type
                var contentJson = JsonSerializer.Serialize(meshNode.Content, hub.JsonSerializerOptions);
                try
                {
                    var deserialized = JsonSerializer.Deserialize(contentJson, contentType, hub.JsonSerializerOptions);
                    if (deserialized == null)
                        return Task.FromResult<string?>($"Error: Content is null after deserialization for NodeType '{meshNode.NodeType}'.");

                    return Task.FromResult<string?>(null); // Valid
                }
                catch (JsonException ex)
                {
                    return Task.FromResult<string?>($"Error: Content does not match the schema for NodeType '{meshNode.NodeType}'. {ex.Message}");
                }
            }
            finally
            {
                tempHub.Dispose();
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Schema validation skipped for NodeType {NodeType}", meshNode.NodeType);
            return Task.FromResult<string?>(null);
        }
    }
}
