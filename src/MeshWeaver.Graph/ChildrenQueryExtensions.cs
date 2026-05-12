using System.Reactive.Linq;
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
    /// Live <see cref="IObservable{T}"/> of child <see cref="MeshNode"/>s matching the supplied
    /// query pattern (e.g. <c>"nodeType:ACME/Project/Todo"</c>). Re-emits whenever the
    /// underlying result set changes — subscribe and react.
    /// </summary>
    public static IObservable<IReadOnlyList<MeshNode>> ObserveChildren(
        this LayoutAreaHost host,
        string childrenQuery)
    {
        var meshQuery = host.Hub.ServiceProvider.GetService<IMeshService>();
        if (meshQuery == null)
            return Observable.Return<IReadOnlyList<MeshNode>>([]);

        var hubPath = host.Hub.Address.ToString();
        var query = childrenQuery.Replace("{path}", hubPath);
        if (!query.Contains("namespace:"))
            query = $"namespace:{hubPath} {query}";

        return meshQuery.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(query))
            .Select(c => (IReadOnlyList<MeshNode>)c.Items);
    }

    /// <summary>
    /// Live observable of child nodes of a specific nodeType.
    /// </summary>
    public static IObservable<IReadOnlyList<MeshNode>> ObserveChildrenByType(
        this LayoutAreaHost host,
        string nodeType)
        => host.ObserveChildren($"nodeType:{nodeType}");

    /// <summary>
    /// Gets the content from a MeshNode, handling both direct types and JsonElement deserialization.
    /// Uses Hub's JsonSerializerOptions via the host for proper type handling.
    /// </summary>
    public static T? GetContent<T>(this MeshNode node, LayoutAreaHost host) where T : class
        => node.GetContent<T>(host.Hub.JsonSerializerOptions);

    /// <summary>
    /// Gets the content from a MeshNode, handling both direct types and JsonElement deserialization.
    /// </summary>
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
