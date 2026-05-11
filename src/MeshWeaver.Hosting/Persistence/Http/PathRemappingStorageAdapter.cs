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

    public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
        => _inner.Read(Remap(path), options);

    public IObservable<MeshNode> Write(MeshNode node, JsonSerializerOptions options)
        => _inner.Write(RemapNode(node), options);

    public IObservable<string> Delete(string path)
        => _inner.Delete(Remap(path));

    public IObservable<bool> Exists(string path)
        => _inner.Exists(Remap(path));

    public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)> ListChildPaths(string? parentPath)
        => _inner.ListChildPaths(parentPath is null ? null : Remap(parentPath));

    public IObservable<object> GetPartitionObjects(string nodePath, string? subPath, JsonSerializerOptions options)
        => _inner.GetPartitionObjects(Remap(nodePath), subPath, options);

    public IObservable<System.Reactive.Unit> SavePartitionObjects(
        string nodePath, string? subPath, IReadOnlyCollection<object> objects, JsonSerializerOptions options)
        => _inner.SavePartitionObjects(Remap(nodePath), subPath, objects, options);

    public IObservable<System.Reactive.Unit> DeletePartitionObjects(string nodePath, string? subPath = null)
        => _inner.DeletePartitionObjects(Remap(nodePath), subPath);

    public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null)
        => _inner.GetPartitionMaxTimestamp(Remap(nodePath), subPath);
}
