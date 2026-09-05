using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// <see cref="IStorageAdapter.DeleteMany"/> — the DELETE-side twin of <c>WriteMany</c>.
///
/// <para><b>Why it exists.</b> A recursive delete removed rows one at a time, and on Postgres each
/// one is its own statement, its own implicit transaction and its own turn on a capacity-1 write
/// pool. Retiring a 9,834-node Space took ~8 minutes — about 21 nodes a second — with a flat
/// profile: the subtree was not big, the per-node cost was.</para>
///
/// <para>These tests pin the CONTRACT every backend must honour, because the default
/// implementation is what most adapters inherit and the batched Postgres override has to agree
/// with it: what comes back is what was actually removed, in the caller's order, and a decorator
/// that forgets to forward silently loses the batching.</para>
/// </summary>
public class StorageAdapterDeleteManyTest
{
    private static readonly JsonSerializerOptions Options = new();

    private static MeshNode Node(string path)
    {
        var slash = path.LastIndexOf('/');
        return new MeshNode(slash < 0 ? path : path[(slash + 1)..], slash < 0 ? null : path[..slash])
        {
            NodeType = "Test",
        };
    }

    // Returned as the INTERFACE on purpose: DeleteMany is a default interface member, so calling
    // it through the concrete type would not even compile — which is the same reason a decorator
    // that forgets to forward silently gets the default instead of the batched override.
    private static IStorageAdapter Seeded(params string[] paths)
    {
        IStorageAdapter adapter = new InMemoryStorageAdapter();
        foreach (var path in paths)
            adapter.Write(Node(path), Options).Wait();
        return adapter;
    }

    [Fact]
    public void DeletesEveryPath_AndReportsThemInTheCallersOrder()
    {
        var adapter = Seeded("S", "S/A", "S/A/x", "S/B");
        // Children before parents — the order a subtree delete hands down, and the order the
        // change feed must keep, because that feed is what wakes the per-node hubs.
        var order = new[] { "S/A/x", "S/A", "S/B", "S" };

        var deleted = adapter.DeleteMany(order).Wait();

        Assert.Equal(order, deleted.ToArray());
        Assert.All(order, p => Assert.Null(adapter.Read(p, Options).Wait()));
    }

    [Fact]
    public void PathsItDoesNotHold_AreAbsentFromTheResult_NotInvented()
    {
        // 🚨 The result is the REMOVED set, not an echo of the request: the composite unions these
        // across providers to learn what actually left storage, so an optimistic echo would make
        // it claim a delete no provider performed.
        var adapter = Seeded("S/A");

        var deleted = adapter.DeleteMany(new[] { "S/A", "S/never-existed" }).Wait();

        Assert.Equal(new[] { "S/A" }, deleted.ToArray());
    }

    [Fact]
    public void EmptyInput_IsANoOp()
    {
        var adapter = Seeded("S/A");

        Assert.Empty(adapter.DeleteMany(Array.Empty<string>()).Wait());
        Assert.NotNull(adapter.Read("S/A", Options).Wait());
    }

    [Fact]
    public void IsIdempotent_SoARerunHealsAHalfRemovedSubtree()
    {
        var adapter = Seeded("S/A", "S/B");
        var paths = new[] { "S/A", "S/B" };

        Assert.Equal(2, adapter.DeleteMany(paths).Wait().Count);
        // Second pass removes nothing and reports nothing — the property the drain loop relies on.
        Assert.Empty(adapter.DeleteMany(paths).Wait());
    }

    [Fact]
    public void PublishesOneDeletedNotificationPerRemovedPath_InOrder()
    {
        var adapter = Seeded("S", "S/A");
        var seen = new List<string>();
        using var _ = adapter.Changes.Subscribe(c => seen.Add(c.Path));

        adapter.DeleteMany(new[] { "S/A", "S" }).Wait();

        Assert.Equal(new[] { "S/A", "S" }, seen.ToArray());
    }

    /// <summary>
    /// 🚨 A decorator that does not forward silently degrades the batch to N singles at the
    /// outermost layer that falls back to the default. The guards are the ones production always
    /// has in front of the real adapter, so this is the regression that would quietly give the
    /// 8 minutes back.
    /// </summary>
    [Fact]
    public void TheProductionDecoratorStack_ForwardsTheBatch_InsteadOfFallingBackToSingles()
    {
        var inner = Seeded("S/A", "S/B");
        var counting = new CountingAdapter(inner);
        IStorageAdapter stack = new SubtreeDeletionGuardStorageAdapter(
            new MonotonicWriteGuardStorageAdapter(counting), registry: null);

        var deleted = stack.DeleteMany(new[] { "S/A", "S/B" }).Wait();

        Assert.Equal(new[] { "S/A", "S/B" }, deleted.ToArray());
        Assert.Equal(1, counting.DeleteManyCalls);
        Assert.Equal(0, counting.DeleteCalls);
    }

    private sealed class CountingAdapter(IStorageAdapter inner) : IStorageAdapter
    {
        public int DeleteCalls;
        public int DeleteManyCalls;

        public IObservable<IReadOnlyList<string>> DeleteMany(IReadOnlyCollection<string> paths)
        {
            DeleteManyCalls++;
            return inner.DeleteMany(paths);
        }

        public IObservable<string> Delete(string path)
        {
            DeleteCalls++;
            return inner.Delete(path);
        }

        public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
            => inner.Read(path, options);

        public IObservable<MeshNode?> Write(MeshNode node, JsonSerializerOptions options)
            => inner.Write(node, options);

        public IObservable<IReadOnlyCollection<string>> ListDescendantPaths(string rootPath)
            => inner.ListDescendantPaths(rootPath);

        public IObservable<bool> DeleteIfExists(string path) => inner.DeleteIfExists(path);

        public IObservable<DataChangeNotification> Changes => inner.Changes;

        public IObservable<bool> Exists(string path) => inner.Exists(path);

        public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)> ListChildPaths(string? parentPath)
            => inner.ListChildPaths(parentPath);

        public IObservable<object> GetPartitionObjects(
            string nodePath, string? subPath, JsonSerializerOptions options)
            => inner.GetPartitionObjects(nodePath, subPath, options);

        public IObservable<System.Reactive.Unit> SavePartitionObjects(
            string nodePath, string? subPath, IReadOnlyCollection<object> objects, JsonSerializerOptions options)
            => inner.SavePartitionObjects(nodePath, subPath, objects, options);

        public IObservable<System.Reactive.Unit> DeletePartitionObjects(string nodePath, string? subPath = null)
            => inner.DeletePartitionObjects(nodePath, subPath);

        public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null)
            => inner.GetPartitionMaxTimestamp(nodePath, subPath);
    }
}
