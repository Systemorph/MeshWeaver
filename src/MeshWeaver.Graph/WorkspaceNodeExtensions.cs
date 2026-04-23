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
    /// Delegates to <see cref="MeshNodeExtensions.GetMeshNodeStream(IWorkspace)"/> —
    /// uses the live MeshNodeReference stream, never a query.
    /// </summary>
    public static IObservable<MeshNode?> GetNodeStream(this IWorkspace workspace)
        => workspace.GetMeshNodeStream().Select(n => (MeshNode?)n);

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
    /// Auto-dispatches to local own-node stream or remote MeshNodeReference subscription
    /// (see <see cref="MeshNodeExtensions.GetMeshNodeStream(IWorkspace, string)"/>).
    /// </summary>
    public static IObservable<MeshNode?> GetNodeStream(this IWorkspace workspace, string path)
        => workspace.GetMeshNodeStream(path).Select(n => (MeshNode?)n);

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
