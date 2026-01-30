using System.Reactive.Linq;
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
    /// The node is retrieved from persistence based on the hub's address.
    /// </summary>
    /// <param name="workspace">The workspace</param>
    /// <returns>An observable stream of the MeshNode, or null if not found</returns>
    public static IObservable<MeshNode?> GetNodeStream(this IWorkspace workspace)
    {
        var persistence = workspace.Hub.ServiceProvider.GetService<IPersistenceService>();
        if (persistence == null)
            return Observable.Return<MeshNode?>(null);

        var nodePath = workspace.Hub.Address.ToString();

        // Create an observable that fetches the node
        // TODO: Subscribe to node changes for reactive updates
        return Observable.FromAsync(async ct =>
            await persistence.GetNodeAsync(nodePath, workspace.Hub.JsonSerializerOptions, ct));
    }

    /// <summary>
    /// Gets the MeshNode's Content as a typed stream.
    /// </summary>
    /// <typeparam name="T">The expected content type</typeparam>
    /// <param name="workspace">The workspace</param>
    /// <returns>An observable stream of the content, or null if not found or wrong type</returns>
    public static IObservable<T?> GetNodeContent<T>(this IWorkspace workspace) where T : class
    {
        return workspace.GetNodeStream()
            .Select(node => node?.Content as T);
    }

    /// <summary>
    /// Gets the MeshNode for a specific path as a stream.
    /// </summary>
    /// <param name="workspace">The workspace</param>
    /// <param name="path">The node path</param>
    /// <returns>An observable stream of the MeshNode, or null if not found</returns>
    public static IObservable<MeshNode?> GetNodeStream(this IWorkspace workspace, string path)
    {
        var persistence = workspace.Hub.ServiceProvider.GetService<IPersistenceService>();
        if (persistence == null)
            return Observable.Return<MeshNode?>(null);

        return Observable.FromAsync(async ct =>
            await persistence.GetNodeAsync(path, workspace.Hub.JsonSerializerOptions, ct));
    }

    /// <summary>
    /// Gets a specific node's Content as a typed stream.
    /// </summary>
    /// <typeparam name="T">The expected content type</typeparam>
    /// <param name="workspace">The workspace</param>
    /// <param name="path">The node path</param>
    /// <returns>An observable stream of the content, or null if not found or wrong type</returns>
    public static IObservable<T?> GetNodeContent<T>(this IWorkspace workspace, string path) where T : class
    {
        return workspace.GetNodeStream(path)
            .Select(node => node?.Content as T);
    }

    /// <summary>
    /// Gets a single instance of type T from the workspace stream.
    /// This is useful for types that are registered as collections but you expect only one instance.
    /// </summary>
    /// <typeparam name="T">The type to get</typeparam>
    /// <param name="workspace">The workspace</param>
    /// <returns>An observable of the first instance, or null if not found</returns>
    public static IObservable<T?> GetSingle<T>(this IWorkspace workspace) where T : class
    {
        var stream = workspace.GetStream<T>();
        if (stream == null)
            return Observable.Return<T?>(null);

        return stream.Select(items => items?.FirstOrDefault());
    }
}
