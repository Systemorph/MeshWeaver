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
    private readonly IMeshQuery? meshQuery;
    private readonly IMeshCatalog? meshCatalog;

    public MeshOperations(IMessageHub hub)
    {
        this.hub = hub;
        this.logger = hub.ServiceProvider.GetRequiredService<ILogger<MeshOperations>>();
        this.meshQuery = hub.ServiceProvider.GetService<IMeshQuery>();
        this.meshCatalog = hub.ServiceProvider.GetService<IMeshCatalog>();
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

            // Get single node via MeshCatalog (enforces security via ValidateReadAsync)
            if (meshCatalog == null)
                return "Mesh catalog service not available.";

            var address = new Address(resolvedPath);
            var meshNode = await meshCatalog.GetNodeAsync(address);
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
    /// Tries to resolve a path as a Unified Path with prefix (schema:, model:, data:).
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
        logger.LogInformation("Resolving Unified Path: address={Address}, remainder={Remainder}",
            resolution.Prefix, resolution.Remainder);

        var response = await hub.AwaitResponse(
            new GetDataRequest(reference),
            o => o.WithTarget(address));

        if (response.Message.Error != null)
            return $"Error: {response.Message.Error}";

        return JsonSerializer.Serialize(response.Message.Data, hub.JsonSerializerOptions);
    }

    public async Task<string> Search(string query, string? basePath = null)
    {
        logger.LogInformation("Search called with query={Query}, basePath={BasePath}", query, basePath);

        if (meshQuery == null)
            return "Query service not available.";

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

    public async Task<string> Create(string node)
    {
        logger.LogInformation("Create called");

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
                var tcs = new TaskCompletionSource<UpdateNodeResponse>();
                var delivery = hub.Post(
                    new UpdateNodeRequest(meshNode),
                    o => o.WithTarget(hub.Address));
                hub.RegisterCallback<UpdateNodeResponse>(delivery, response =>
                {
                    tcs.TrySetResult(response.Message);
                    return response;
                });
                var updateResponse = await tcs.Task;
                if (!updateResponse.Success)
                    throw new InvalidOperationException(updateResponse.Error ?? "Update failed");
                results.Add($"Updated: {updateResponse.Node!.Path}");
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
                var tcs = new TaskCompletionSource<DeleteNodeResponse>();
                var delivery = hub.Post(
                    new DeleteNodeRequest(resolvedPath),
                    o => o.WithTarget(hub.Address));
                hub.RegisterCallback<DeleteNodeResponse>(delivery, response =>
                {
                    tcs.TrySetResult(response.Message);
                    return response;
                });
                var deleteResponse = await tcs.Task;
                if (!deleteResponse.Success)
                    throw new InvalidOperationException(deleteResponse.Error ?? "Delete failed");
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
