using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// Extension methods for accessing MeshNode data from workspace streams.
/// </summary>
public static class WorkspaceNodeExtensions
{
    /// <summary>
    /// Gets the MeshNode for the current hub as a stream.
    /// The node is retrieved via IMeshService based on the hub's address.
    /// </summary>
    public static IObservable<MeshNode?> GetNodeStream(this IWorkspace workspace)
    {
        var meshQuery = workspace.Hub.ServiceProvider.GetRequiredService<IMeshService>();
        var nodePath = workspace.Hub.Address.ToString();

        return Observable.FromAsync(async ct =>
            await meshQuery.QueryAsync<MeshNode>($"path:{nodePath}").FirstOrDefaultAsync(ct));
    }

    /// <summary>
    /// Gets the MeshNode's Content as a typed stream.
    /// </summary>
    public static IObservable<T?> GetNodeContent<T>(this IWorkspace workspace) where T : class
    {
        return workspace.GetNodeStream()
            .Select(node => DeserializeContent<T>(node, workspace.Hub.JsonSerializerOptions));
    }

    /// <summary>
    /// Gets the MeshNode for a specific path as a stream.
    /// </summary>
    public static IObservable<MeshNode?> GetNodeStream(this IWorkspace workspace, string path)
    {
        var meshQuery = workspace.Hub.ServiceProvider.GetRequiredService<IMeshService>();

        return Observable.FromAsync(async ct =>
            await meshQuery.QueryAsync<MeshNode>($"path:{path}").FirstOrDefaultAsync(ct));
    }

    /// <summary>
    /// Gets a specific node's Content as a typed stream.
    /// </summary>
    public static IObservable<T?> GetNodeContent<T>(this IWorkspace workspace, string path) where T : class
    {
        return workspace.GetNodeStream(path)
            .Select(node => DeserializeContent<T>(node, workspace.Hub.JsonSerializerOptions));
    }

    private static T? DeserializeContent<T>(MeshNode? node, JsonSerializerOptions? options) where T : class
    {
        if (node?.Content == null) return null;
        if (node.Content is T typed) return typed;
        if (node.Content is JsonElement json)
        {
            try { return JsonSerializer.Deserialize<T>(json, options); }
            catch { return null; }
        }
        return null;
    }

    /// <summary>
    /// Gets a single instance of type T from the workspace stream.
    /// </summary>
    public static IObservable<T?> GetSingle<T>(this IWorkspace workspace) where T : class
    {
        var stream = workspace.GetStream<T>();
        if (stream == null)
            return Observable.Return<T?>(null);

        return stream.Select(items => items?.FirstOrDefault());
    }
}
