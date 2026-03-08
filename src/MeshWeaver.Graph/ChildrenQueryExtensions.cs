using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// Extension methods for querying children using IMeshService.
/// </summary>
public static class ChildrenQueryExtensions
{
    /// <summary>
    /// Queries for child nodes using the specified query pattern via IMeshService.
    /// Supports query patterns like "nodeType:ACME/Project/Todo".
    /// </summary>
    /// <param name="host">The layout area host</param>
    /// <param name="childrenQuery">The query pattern (e.g., "nodeType:ACME/Project/Todo")</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Matching MeshNode results</returns>
    public static async IAsyncEnumerable<MeshNode> QueryChildrenAsync(
        this LayoutAreaHost host,
        string childrenQuery,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var meshQuery = host.Hub.ServiceProvider.GetService<IMeshService>();
        if (meshQuery == null)
            yield break;

        var hubPath = host.Hub.Address.ToString();

        // Build the full query with path context
        // The childrenQuery may contain placeholders like {path}
        var query = childrenQuery.Replace("{path}", hubPath);

        // If the query doesn't already have a namespace filter, add it
        if (!query.Contains("namespace:"))
        {
            query = $"namespace:{hubPath} {query}";
        }

        await foreach (var item in meshQuery.QueryAsync<MeshNode>(query, ct: ct))
        {
            yield return item;
        }
    }

    /// <summary>
    /// Queries for child nodes of a specific nodeType via IMeshService.
    /// This is a convenience method for the common pattern "nodeType:{nodeType}".
    /// </summary>
    /// <param name="host">The layout area host</param>
    /// <param name="nodeType">The nodeType to filter by (e.g., "ACME/Project/Todo")</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Matching MeshNode results</returns>
    public static IAsyncEnumerable<MeshNode> QueryChildrenByTypeAsync(
        this LayoutAreaHost host,
        string nodeType,
        CancellationToken ct = default)
    {
        return host.QueryChildrenAsync($"nodeType:{nodeType}", ct);
    }

    /// <summary>
    /// Queries for child nodes and extracts their content as the specified type.
    /// Uses Hub's JsonSerializerOptions for proper type handling.
    /// </summary>
    /// <typeparam name="T">The content type to extract</typeparam>
    /// <param name="host">The layout area host</param>
    /// <param name="nodeType">The nodeType to filter by (e.g., "ACME/Project/Todo")</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Content objects of type T</returns>
    public static async IAsyncEnumerable<T> QueryChildContentAsync<T>(
        this LayoutAreaHost host,
        string nodeType,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default) where T : class
    {
        await foreach (var node in host.QueryChildrenByTypeAsync(nodeType, ct))
        {
            // Use Hub's JsonSerializerOptions for proper camelCase and type handling
            var content = node.GetContent<T>(host.Hub.JsonSerializerOptions);
            if (content != null)
            {
                yield return content;
            }
        }
    }

    /// <summary>
    /// Gets the content from a MeshNode, handling both direct types and JsonElement deserialization.
    /// Uses Hub's JsonSerializerOptions via the host for proper type handling.
    /// </summary>
    /// <typeparam name="T">The expected content type</typeparam>
    /// <param name="node">The mesh node</param>
    /// <param name="host">The layout area host to get JsonSerializerOptions from</param>
    /// <returns>The content as type T, or null if conversion fails</returns>
    public static T? GetContent<T>(this MeshNode node, LayoutAreaHost host) where T : class
        => node.GetContent<T>(host.Hub.JsonSerializerOptions);

    /// <summary>
    /// Gets the content from a MeshNode, handling both direct types and JsonElement deserialization.
    /// </summary>
    /// <typeparam name="T">The expected content type</typeparam>
    /// <param name="node">The mesh node</param>
    /// <param name="options">JsonSerializerOptions to use for deserialization (should come from Hub.JsonSerializerOptions)</param>
    /// <returns>The content as type T, or null if conversion fails</returns>
    public static T? GetContent<T>(this MeshNode node, System.Text.Json.JsonSerializerOptions options) where T : class
    {
        if (node.Content == null)
            return null;

        if (node.Content is T typed)
            return typed;

        if (node.Content is System.Text.Json.JsonElement json)
        {
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<T>(json.GetRawText(), options);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the content from a MeshNode, handling both direct types and JsonElement deserialization.
    /// WARNING: Uses default JsonSerializerOptions which may not have proper camelCase handling.
    /// Prefer the overload that takes JsonSerializerOptions from Hub.
    /// </summary>
    /// <typeparam name="T">The expected content type</typeparam>
    /// <param name="node">The mesh node</param>
    /// <returns>The content as type T, or null if conversion fails</returns>
    [Obsolete("Use GetContent<T>(node, options) with Hub.JsonSerializerOptions instead for proper camelCase handling")]
    public static T? GetContent<T>(this MeshNode node) where T : class
    {
        if (node.Content == null)
            return null;

        if (node.Content is T typed)
            return typed;

        if (node.Content is System.Text.Json.JsonElement json)
        {
            try
            {
                // Use CamelCase to match Hub's default serialization options
                var options = new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                    PropertyNameCaseInsensitive = true,
                    Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
                };
                return System.Text.Json.JsonSerializer.Deserialize<T>(json.GetRawText(), options);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }
}
