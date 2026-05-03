using System.Runtime.CompilerServices;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.Persistence.Http;

/// <summary>
/// Decorator that rewrites every path that flows through an underlying
/// <see cref="IStorageAdapter"/> from <c>{sourcePrefix}/...</c> to
/// <c>{targetPrefix}/...</c>. Used by the cross-instance mirror when the
/// caller asks for a different namespace on the destination
/// (e.g. push <c>rbuergi/Story</c> from local to <c>Systemorph/Story</c>
/// on prod).
///
/// <para><b>Symmetric:</b> reads/writes/deletes on the wrapped adapter
/// always see the <em>target-side</em> path. The <see cref="MeshNode.Namespace"/>
/// + <see cref="MeshNode.Id"/> on a node passed to <see cref="WriteAsync"/>
/// are rewritten so the remote stores it at the new path; the original
/// node's other content (NodeType, Content payload, etc.) is preserved.</para>
///
/// <para>Partition objects pass through unchanged — the path is what the
/// adapter routes on, and we remap that.</para>
/// </summary>
public sealed class PathRemappingStorageAdapter : IStorageAdapter
{
    private readonly IStorageAdapter _inner;
    private readonly string _sourcePrefix;
    private readonly string _targetPrefix;

    public PathRemappingStorageAdapter(IStorageAdapter inner, string sourcePrefix, string targetPrefix)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _sourcePrefix = (sourcePrefix ?? "").Trim('/');
        _targetPrefix = (targetPrefix ?? "").Trim('/');
    }

    /// <summary>
    /// Map a source-side path to its target-side equivalent. Paths that
    /// don't start with <see cref="_sourcePrefix"/> pass through unchanged
    /// — the importer occasionally walks paths outside the configured root
    /// (e.g. partition objects keyed by absolute path).
    /// </summary>
    private string Remap(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        var trimmed = path.TrimStart('/');
        if (string.IsNullOrEmpty(_sourcePrefix))
            return string.IsNullOrEmpty(_targetPrefix) ? trimmed : $"{_targetPrefix}/{trimmed}";
        if (trimmed.Equals(_sourcePrefix, StringComparison.OrdinalIgnoreCase))
            return _targetPrefix;
        if (trimmed.StartsWith(_sourcePrefix + "/", StringComparison.OrdinalIgnoreCase))
        {
            var suffix = trimmed[(_sourcePrefix.Length + 1)..];
            return string.IsNullOrEmpty(_targetPrefix) ? suffix : $"{_targetPrefix}/{suffix}";
        }
        return trimmed;
    }

    /// <summary>
    /// Apply <see cref="Remap"/> to a node's logical path: rewrites
    /// <see cref="MeshNode.Namespace"/> + <see cref="MeshNode.Id"/> +
    /// <see cref="MeshNode.MainNode"/> if it equalled the original path.
    /// Other content stays untouched.
    /// </summary>
    private MeshNode RemapNode(MeshNode node)
    {
        var newPath = Remap(node.Path);
        if (string.Equals(newPath, node.Path, StringComparison.Ordinal))
            return node;

        var lastSlash = newPath.LastIndexOf('/');
        var newNs = lastSlash > 0 ? newPath[..lastSlash] : null;
        var newId = lastSlash > 0 ? newPath[(lastSlash + 1)..] : newPath;

        var remapped = node with { Namespace = newNs, Id = newId };

        // If MainNode pointed at the original path (the most common shape
        // for a non-satellite primary node), retarget it too.
        if (string.Equals(node.MainNode, node.Path, StringComparison.Ordinal))
            remapped = remapped with { MainNode = newPath };

        return remapped;
    }

    public Task<MeshNode?> ReadAsync(string path, JsonSerializerOptions options, CancellationToken ct = default)
        => _inner.ReadAsync(Remap(path), options, ct);

    public Task WriteAsync(MeshNode node, JsonSerializerOptions options, CancellationToken ct = default)
        => _inner.WriteAsync(RemapNode(node), options, ct);

    public Task DeleteAsync(string path, CancellationToken ct = default)
        => _inner.DeleteAsync(Remap(path), ct);

    public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
        => _inner.ExistsAsync(Remap(path), ct);

    public Task<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)> ListChildPathsAsync(
        string? parentPath, CancellationToken ct = default)
        => _inner.ListChildPathsAsync(parentPath is null ? null : Remap(parentPath), ct);

    public IAsyncEnumerable<object> GetPartitionObjectsAsync(
        string nodePath, string? subPath, JsonSerializerOptions options, CancellationToken ct = default)
        => _inner.GetPartitionObjectsAsync(Remap(nodePath), subPath, options, ct);

    public Task SavePartitionObjectsAsync(
        string nodePath, string? subPath, IReadOnlyCollection<object> objects,
        JsonSerializerOptions options, CancellationToken ct = default)
        => _inner.SavePartitionObjectsAsync(Remap(nodePath), subPath, objects, options, ct);

    public Task DeletePartitionObjectsAsync(string nodePath, string? subPath = null, CancellationToken ct = default)
        => _inner.DeletePartitionObjectsAsync(Remap(nodePath), subPath, ct);

    public Task<DateTimeOffset?> GetPartitionMaxTimestampAsync(
        string nodePath, string? subPath = null, CancellationToken ct = default)
        => _inner.GetPartitionMaxTimestampAsync(Remap(nodePath), subPath, ct);
}
