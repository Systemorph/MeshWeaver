using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Read-only <see cref="IStorageService"/> backed by a fixed list of
/// <see cref="MeshNode"/>s — typically the slice of an
/// <see cref="IStaticNodeProvider"/>'s output that lives under a single
/// partition (first-segment) prefix.
///
/// Used by <see cref="RoutingPersistenceServiceCore"/> to surface
/// static-provider partitions (NodeType definitions, doc namespaces, test
/// fixtures) through the same routing path as writable partitions, without
/// mixing static-provider concerns into the writable persisters
/// (<see cref="InMemoryPersistenceService"/>, file-system, PostgreSQL).
/// See <c>Doc/Architecture/PartitionedPersistence.md</c> §"Where Partitions Come From".
///
/// All write operations throw <see cref="System.NotSupportedException"/>; static
/// partitions are immutable. To allow runtime writes under the same partition
/// prefix, layer a writable store on top via <see cref="LayeredPartitionStore"/>.
/// </summary>
internal sealed class StaticNodePartitionStore : IStorageService
{
    private readonly ImmutableDictionary<string, MeshNode> _nodes;

    public StaticNodePartitionStore(IEnumerable<MeshNode> nodes)
    {
        _nodes = nodes
            .GroupBy(n => n.Path, System.StringComparer.OrdinalIgnoreCase)
            .ToImmutableDictionary(g => g.Key, g => g.Last(), System.StringComparer.OrdinalIgnoreCase);
    }

    public IObservable<MeshNode?> GetNode(string path, JsonSerializerOptions options)
        => Observable.Return(_nodes.TryGetValue(path, out var node) ? node : null);

    /// <summary>
    /// Test/back-compat shim. Production callers go through <see cref="GetNode"/>.
    /// </summary>
    [System.Obsolete("Use GetNode(path, options) which returns IObservable<MeshNode?>.")]
    public Task<MeshNode?> GetNodeAsync(string path, JsonSerializerOptions options, CancellationToken ct = default)
        => Task.FromResult(_nodes.TryGetValue(path, out var node) ? node : null);

    public IAsyncEnumerable<MeshNode> GetChildrenAsync(string? parentPath, JsonSerializerOptions options)
        => EnumerateChildren(parentPath, includeSatellites: false).ToAsyncEnumerable();

    public IAsyncEnumerable<MeshNode> GetAllChildrenAsync(string? parentPath, JsonSerializerOptions options)
        => EnumerateChildren(parentPath, includeSatellites: true).ToAsyncEnumerable();

    public IAsyncEnumerable<MeshNode> GetDescendantsAsync(string? parentPath, JsonSerializerOptions options)
        => EnumerateDescendants(parentPath, includeSatellites: false).ToAsyncEnumerable();

    public IAsyncEnumerable<MeshNode> GetAllDescendantsAsync(string? parentPath, JsonSerializerOptions options)
        => EnumerateDescendants(parentPath, includeSatellites: true).ToAsyncEnumerable();

    public Task<MeshNode> SaveNodeAsync(MeshNode node, JsonSerializerOptions options, CancellationToken ct = default)
        => throw new System.NotSupportedException(
            $"StaticNodePartitionStore is read-only; cannot save '{node.Path}'. " +
            "Static partitions hold IStaticNodeProvider seed data only.");

    public Task DeleteNodeAsync(string path, bool recursive = false, CancellationToken ct = default)
        => throw new System.NotSupportedException(
            $"StaticNodePartitionStore is read-only; cannot delete '{path}'.");

    public Task<MeshNode> MoveNodeAsync(string sourcePath, string targetPath, JsonSerializerOptions options, CancellationToken ct = default)
        => throw new System.NotSupportedException(
            $"StaticNodePartitionStore is read-only; cannot move '{sourcePath}'.");

    public IAsyncEnumerable<MeshNode> SearchAsync(string? parentPath, string query, JsonSerializerOptions options)
        => EnumerateDescendants(parentPath, includeSatellites: true)
            .Where(n => string.IsNullOrEmpty(query)
                || (n.Name?.Contains(query, System.StringComparison.OrdinalIgnoreCase) ?? false))
            .ToAsyncEnumerable();

    public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
        => Task.FromResult(_nodes.ContainsKey(path));

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public IAsyncEnumerable<Comment> GetCommentsAsync(string nodePath, JsonSerializerOptions options)
        => System.Linq.AsyncEnumerable.Empty<Comment>();

    public Task<Comment> AddCommentAsync(Comment comment, JsonSerializerOptions options, CancellationToken ct = default)
        => throw new System.NotSupportedException("StaticNodePartitionStore is read-only.");

    public Task DeleteCommentAsync(string commentId, CancellationToken ct = default)
        => throw new System.NotSupportedException("StaticNodePartitionStore is read-only.");

    public Task<Comment?> GetCommentAsync(string commentId, CancellationToken ct = default)
        => Task.FromResult<Comment?>(null);

    public IAsyncEnumerable<object> GetPartitionObjectsAsync(string nodePath, string? subPath, JsonSerializerOptions options)
        => System.Linq.AsyncEnumerable.Empty<object>();

    public Task SavePartitionObjectsAsync(string nodePath, string? subPath, IReadOnlyCollection<object> objects, JsonSerializerOptions options, CancellationToken ct = default)
        => throw new System.NotSupportedException("StaticNodePartitionStore is read-only.");

    public Task DeletePartitionObjectsAsync(string nodePath, string? subPath = null, CancellationToken ct = default)
        => throw new System.NotSupportedException("StaticNodePartitionStore is read-only.");

    public Task<System.DateTimeOffset?> GetPartitionMaxTimestampAsync(string nodePath, string? subPath = null, CancellationToken ct = default)
        => Task.FromResult<System.DateTimeOffset?>(null);

    private IEnumerable<MeshNode> EnumerateChildren(string? parentPath, bool includeSatellites)
    {
        var prefix = string.IsNullOrEmpty(parentPath) ? "" : parentPath + "/";
        foreach (var node in _nodes.Values)
        {
            if (string.IsNullOrEmpty(parentPath))
            {
                if (!node.Path.Contains('/')) yield return node;
            }
            else if (node.Path.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase))
            {
                var remainder = node.Path[prefix.Length..];
                if (!remainder.Contains('/')
                    && (includeSatellites || string.Equals(node.MainNode, node.Path, System.StringComparison.OrdinalIgnoreCase)))
                {
                    yield return node;
                }
            }
        }
    }

    private IEnumerable<MeshNode> EnumerateDescendants(string? parentPath, bool includeSatellites)
    {
        var prefix = string.IsNullOrEmpty(parentPath) ? "" : parentPath + "/";
        foreach (var node in _nodes.Values)
        {
            if (string.IsNullOrEmpty(parentPath)
                || node.Path.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase))
            {
                if (includeSatellites || string.Equals(node.MainNode, node.Path, System.StringComparison.OrdinalIgnoreCase))
                    yield return node;
            }
        }
    }
}
