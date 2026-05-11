using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.Persistence.Http;

/// <summary>
/// <see cref="IStorageAdapter"/> backed by a remote MeshWeaver portal over its MCP
/// HTTP surface. Plug it in as the <em>target</em> of <see cref="StorageImporter"/>
/// to push a subtree out, or as the <em>source</em> to pull a subtree in.
///
/// <para>All operations route through <see cref="IRemoteMeshClient"/> which has an
/// <see cref="IObservable{T}"/>-based surface (per AsynchronousCalls.md). This
/// adapter is a thin <see cref="IObservable{T}"/> shim.</para>
///
/// <para><b>Partition objects (v1 scope):</b> <see cref="GetPartitionObjects"/>,
/// <see cref="SavePartitionObjects"/>, and friends are no-ops. The current MCP tool
/// surface doesn't expose a generic "enumerate partition objects" call.</para>
/// </summary>
public sealed class HttpMeshStorageAdapter : IStorageAdapter
{
    private readonly IRemoteMeshClient _client;

    public HttpMeshStorageAdapter(IRemoteMeshClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
        => _client.Get(path);

    public IObservable<Unit> Write(MeshNode node, JsonSerializerOptions options)
        // Upsert: probe existence then Create or Update; map to Unit.
        => _client.Get(node.Path)
            .SelectMany(existing => existing is null
                ? _client.Create(node)
                : _client.Update(node))
            .Select(_ => Unit.Default);

    public IObservable<Unit> Delete(string path)
        => _client.Delete(path).Select(_ => Unit.Default);

    public IObservable<bool> Exists(string path)
        => _client.Get(path).Select(n => n is not null);

    public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)> ListChildPaths(string? parentPath)
    {
        // Immediate children only — search by exact namespace, not by subtree.
        // Empty parent means root-level children: nodes whose namespace is empty.
        var query = string.IsNullOrEmpty(parentPath)
            ? "namespace:"
            : $"namespace:{parentPath}";
        // Remote-backed adapters have no notion of "directories without nodes" —
        // every container is also a node. Always return empty for DirectoryPaths.
        return _client.SearchPaths(query)
            .Select(paths => ((IEnumerable<string>)paths, (IEnumerable<string>)Array.Empty<string>()));
    }

    /// <inheritdoc />
    /// <remarks>V1: returns empty (no partition object enumeration over MCP yet).</remarks>
    public IObservable<object> GetPartitionObjects(string nodePath, string? subPath, JsonSerializerOptions options)
        => Observable.Empty<object>();

    /// <inheritdoc />
    /// <remarks>V1: no-op. See <see cref="GetPartitionObjects"/>.</remarks>
    public IObservable<Unit> SavePartitionObjects(
        string nodePath, string? subPath, IReadOnlyCollection<object> objects, JsonSerializerOptions options)
        => Observable.Return(Unit.Default);

    /// <inheritdoc />
    /// <remarks>V1: no-op. See <see cref="GetPartitionObjects"/>.</remarks>
    public IObservable<Unit> DeletePartitionObjects(string nodePath, string? subPath = null)
        => Observable.Return(Unit.Default);

    /// <inheritdoc />
    /// <remarks>V1: returns null (caches keyed off this timestamp will refresh as needed).</remarks>
    public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null)
        => Observable.Return<DateTimeOffset?>(null);
}
