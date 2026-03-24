using System.Text.Json;
using MeshWeaver.Data;
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
            var meshNode = JsonSerializer.Deserialize<MeshNode>(node, hub.JsonSerializerOptions);
            if (meshNode == null)
                return "Invalid node: deserialized to null.";

            if (string.IsNullOrWhiteSpace(meshNode.Name))
                return "Error: 'name' property is required. Provide a human-readable display name.";

            var created = await mesh.CreateNodeAsync(meshNode);
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

    public async Task<string> Update(string nodes)
    {
        logger.LogInformation("Update called");

        try
        {
            var nodeList = JsonSerializer.Deserialize<List<MeshNode>>(nodes, hub.JsonSerializerOptions);
            if (nodeList == null || nodeList.Count == 0)
                return "No nodes provided.";

            var results = new List<string>();
            foreach (var meshNode in nodeList)
            {
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
}
