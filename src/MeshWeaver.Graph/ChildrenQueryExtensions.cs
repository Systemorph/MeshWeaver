using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// Extension methods for querying children using IMeshQuery.
/// </summary>
public static class ChildrenQueryExtensions
{
    /// <summary>
    /// Queries for child nodes using the specified query pattern via IMeshQuery.
    /// Supports query patterns like "nodeType:ACME/Project/Todo scope:children".
    /// </summary>
    /// <param name="host">The layout area host</param>
    /// <param name="childrenQuery">The query pattern (e.g., "nodeType:ACME/Project/Todo scope:children")</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Matching MeshNode results</returns>
    public static async IAsyncEnumerable<MeshNode> QueryChildrenAsync(
        this LayoutAreaHost host,
        string childrenQuery,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var meshQuery = host.Hub.ServiceProvider.GetService<IMeshQuery>();
        if (meshQuery == null)
            yield break;

        var hubPath = host.Hub.Address.ToString();

        // Build the full query with path context
        // The childrenQuery may contain placeholders like {path}
        var query = childrenQuery.Replace("{path}", hubPath);

        // If the query doesn't already have a path filter, add it
        if (!query.Contains("path:"))
        {
            query = $"path:{hubPath} {query}";
        }

        await foreach (var item in meshQuery.QueryAsync<MeshNode>(query, ct: ct))
        {
            yield return item;
        }
    }

    /// <summary>
    /// Queries for child nodes of a specific nodeType via IMeshQuery.
    /// This is a convenience method for the common pattern "nodeType:{nodeType} scope:children".
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
        return host.QueryChildrenAsync($"nodeType:{nodeType} scope:children", ct);
    }

    /// <summary>
    /// Queries for child nodes and extracts their content as the specified type.
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
            var content = node.GetContent<T>();
            if (content != null)
            {
                yield return content;
            }
        }
    }

    /// <summary>
    /// Gets the content from a MeshNode, handling both direct types and JsonElement deserialization.
    /// </summary>
    /// <typeparam name="T">The expected content type</typeparam>
    /// <param name="node">The mesh node</param>
    /// <returns>The content as type T, or null if conversion fails</returns>
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
                var options = new System.Text.Json.JsonSerializerOptions
                {
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
