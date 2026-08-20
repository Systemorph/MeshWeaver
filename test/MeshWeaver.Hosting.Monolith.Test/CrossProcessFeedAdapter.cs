#pragma warning disable CS1591

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Test-only <see cref="IStorageAdapter"/> decorator that models ONE PROCESS's view of a shared
/// durable store: its own commits publish locally (as every adapter does), and commits made by
/// ANOTHER process arrive through <see cref="PublishExternal"/> — stripped of their entity, which
/// is what a cross-process feed actually delivers.
///
/// <para><b>Why entity-less is the faithful model, not a convenience.</b> Every source whose
/// notification can outrun row visibility passes <c>Entity = null</c> by contract:
/// <c>PostgreSqlChangeListener</c> parses <c>{path, op}</c> out of a <c>pg_notify</c> payload that
/// has no room for a node, the Cosmos change feed and the Snowflake poller do the same
/// ("Entity = null (subscribers re-read…)"). Populating it from the payload is the row-level-security
/// bypass <c>StorageAdapterMeshQueryProvider</c> REMOVED (#1250), so a consumer that needs the node
/// must RE-READ. This decorator therefore reproduces the exact shape of the production feed rather
/// than a friendlier one.</para>
///
/// <para><see cref="LocalChanges"/> — the inner adapter's own commits — is what a bridge forwards to
/// the other process. Forwarding <see cref="Changes"/> instead would loop for ever, and would also
/// be wrong: a Postgres NOTIFY fires from the row trigger on a WRITE, never on receiving one.</para>
///
/// <para><see cref="ReadCount"/> makes the cost of a notification observable, which is what turns
/// "the re-read is bounded" and "an unowned path is discarded cheaply" into assertions instead of
/// claims.</para>
/// </summary>
internal sealed class CrossProcessFeedAdapter(IStorageAdapter inner) : IStorageAdapter
{
    private readonly IsolatedChangeFeed _external = new(null, "cross-process");
    private readonly ConcurrentDictionary<string, int> _readCounts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _writeCounts =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>This process's OWN commits — what a bridge forwards to the other process.</summary>
    public IObservable<DataChangeNotification> LocalChanges => inner.Changes;

    /// <summary>
    /// Delivers a commit made in ANOTHER process, exactly as a LISTEN/NOTIFY event arrives: the
    /// path and the kind, and no entity.
    /// </summary>
    public void PublishExternal(DataChangeNotification notification)
        => _external.OnNext(notification with { Entity = null });

    /// <summary>Reads of <paramref name="path"/> that reached this adapter.</summary>
    public int ReadCount(string path) => _readCounts.GetValueOrDefault(path);

    /// <summary>Writes of <paramref name="path"/> that reached this adapter.</summary>
    public int WriteCount(string path) => _writeCounts.GetValueOrDefault(path);

    public IObservable<DataChangeNotification> Changes => inner.Changes.Merge(_external);

    public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
        => Observable.Defer(() =>
        {
            _readCounts.AddOrUpdate(path, 1, (_, c) => c + 1);
            return inner.Read(path, options);
        });

    public IObservable<MeshNode> ReadMany(IReadOnlyCollection<string> paths, JsonSerializerOptions options)
        => paths.Select(p => Read(p, options).Where(n => n is not null).Select(n => n!)).Merge();

    public IObservable<MeshNode?> Write(MeshNode node, JsonSerializerOptions options)
        => Observable.Defer(() =>
        {
            if (!string.IsNullOrEmpty(node.Path))
                _writeCounts.AddOrUpdate(node.Path, 1, (_, c) => c + 1);
            return inner.Write(node, options);
        });

    public IObservable<IReadOnlyList<MeshNode>> WriteMany(
        IReadOnlyCollection<MeshNode> nodes, JsonSerializerOptions options)
        => Observable.Defer(() =>
        {
            foreach (var node in nodes)
                if (!string.IsNullOrEmpty(node.Path))
                    _writeCounts.AddOrUpdate(node.Path, 1, (_, c) => c + 1);
            return inner.WriteMany(nodes, options);
        });

    // Forwarded, never defaulted: the default implementation is a read-then-write pair, which
    // would silently drop the store's ATOMIC compare-and-set — the property the cross-cluster
    // build claim depends on.
    public IObservable<bool?> WriteIfVersion(MeshNode node, long expectedVersion, JsonSerializerOptions options)
        => Observable.Defer(() =>
        {
            if (!string.IsNullOrEmpty(node.Path))
                _writeCounts.AddOrUpdate(node.Path, 1, (_, c) => c + 1);
            return inner.WriteIfVersion(node, expectedVersion, options);
        });

    public IObservable<string> Delete(string path) => inner.Delete(path);

    public IObservable<bool> DeleteIfExists(string path) => inner.DeleteIfExists(path);

    public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)> ListChildPaths(string? parentPath)
        => inner.ListChildPaths(parentPath);

    public IObservable<IReadOnlyCollection<string>> ListDescendantPaths(string rootPath)
        => inner.ListDescendantPaths(rootPath);

    public IObservable<bool> Exists(string path) => inner.Exists(path);

    public IObservable<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatch(
        string fullPath, JsonSerializerOptions options)
        => inner.FindBestPrefixMatch(fullPath, options);

    public IObservable<(MeshNode? Node, int MatchedSegments)> ResolvePath(
        string fullPath, JsonSerializerOptions options)
        => inner.ResolvePath(fullPath, options);

    public IObservable<IEnumerable<string>> ListPartitionSubPaths(string nodePath)
        => inner.ListPartitionSubPaths(nodePath);

    public IObservable<object> GetPartitionObjects(string nodePath, string? subPath, JsonSerializerOptions options)
        => inner.GetPartitionObjects(nodePath, subPath, options);

    public IObservable<Unit> SavePartitionObjects(
        string nodePath, string? subPath,
        IReadOnlyCollection<object> objects, JsonSerializerOptions options)
        => inner.SavePartitionObjects(nodePath, subPath, objects, options);

    public IObservable<Unit> DeletePartitionObjects(string nodePath, string? subPath = null)
        => inner.DeletePartitionObjects(nodePath, subPath);

    public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null)
        => inner.GetPartitionMaxTimestamp(nodePath, subPath);
}
