using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <see cref="IStorageAdapter.DeleteMany"/> that fails PART-WAY names the rows it removed
/// (MeshWeaver#7396).
///
/// <para>The batch delete reported what it removed only when it succeeded. A batch that faulted
/// after removing some rows handed its caller a bare exception, so a move or a delete could not
/// retire the hubs and cache entries at the paths that were gone. These tests pin the storage half
/// of the contract: the default (one row at a time) and the union over several stores. The mesh half, a move and a delete retiring exactly those paths, is
/// pinned in <c>MeshWeaver.FaultInjection.Test</c>.</para>
/// </summary>
public class ABatchDeleteThatFailsPartWayNamesWhatItRemovedTest
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

    private static async Task<InMemoryStorageAdapter> Seeded(params string[] paths)
    {
        var adapter = new InMemoryStorageAdapter();
        foreach (var path in paths)
            await ((IStorageAdapter)adapter).Write(Node(path), Options).FirstAsync().Await(TestContext.Current.CancellationToken);
        return adapter;
    }

    /// <summary>
    /// The default implementation removes one row after another. When the third of four fails, the
    /// first two are gone, and the fault must say so. On unfixed code the fault was the store's bare
    /// exception, and the two removed rows were known to nobody.
    /// </summary>
    [Fact]
    public async Task TheDefault_FailingOnTheThirdRow_NamesTheTwoItRemoved()
    {
        var inner = await Seeded("S", "S/A", "S/A/x", "S/B");
        IStorageAdapter adapter = new SingleRowFaultAdapter(inner, failAt: "S/B");

        var error = await Assert.ThrowsAsync<BatchDeletePartiallyAppliedException>(
            () => adapter.DeleteMany(["S/A/x", "S/A", "S/B", "S"]).FirstAsync().Await());

        Assert.Equal(["S/A/x", "S/A"], error.Removed.ToArray());
        Assert.IsType<InvalidOperationException>(error.InnerException);
        // The set is the truth: what it names is gone, and what it does not name is still stored.
        Assert.Null(await ((IStorageAdapter)inner).Read("S/A/x", Options).FirstAsync().Await());
        Assert.Null(await ((IStorageAdapter)inner).Read("S/A", Options).FirstAsync().Await());
        Assert.NotNull(await ((IStorageAdapter)inner).Read("S/B", Options).FirstAsync().Await());
        Assert.NotNull(await ((IStorageAdapter)inner).Read("S", Options).FirstAsync().Await());
    }

    /// <summary>
    /// Negative control: a batch that fails on its FIRST row removed nothing, and its fault stays the
    /// store's own exception. Wrapping it would claim a partial removal that never happened.
    /// </summary>
    [Fact]
    public async Task TheDefault_FailingBeforeAnyRemoval_RaisesTheStoresOwnFault()
    {
        var inner = await Seeded("S/A", "S/B");
        IStorageAdapter adapter = new SingleRowFaultAdapter(inner, failAt: "S/A");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapter.DeleteMany(["S/A", "S/B"]).FirstAsync().Await());

        Assert.Empty(BatchDeletePartiallyAppliedException.RemovedBy(error));
        Assert.NotNull(await ((IStorageAdapter)inner).Read("S/B", Options).FirstAsync().Await());
    }

    /// <summary>
    /// The composite runs one batch per writable store. A store that failed part-way must not hide
    /// what the OTHER stores removed: every store is heard to the end, and the fault carries the
    /// union, in the caller's order. Its message is the store's own, verbatim: the exception adds
    /// data, never a framework-authored sentence no viewer's language could render.
    /// </summary>
    [Fact]
    public async Task TheUnionOverStores_CarriesEveryStoresRemovals_InTheCallersOrder()
    {
        string[] paths = ["S/A/x", "S/A", "S/B", "S"];
        var failed = new BatchDeleteStore(
            () => Observable.Throw<IReadOnlyList<string>>(new BatchDeletePartiallyAppliedException(
                ["S/A"], new InvalidOperationException("store one died"))),
            // Store one no longer holds anything it was asked about.
            _ => Observable.Return(false));
        var succeeded = new BatchDeleteStore(
            () => Observable.Return<IReadOnlyList<string>>(["S/B", "S/A/x"]),
            _ => Observable.Return(false));

        var error = await Assert.ThrowsAsync<BatchDeletePartiallyAppliedException>(
            () => StorageBatchDelete.Union(paths, [failed, succeeded]).FirstAsync().Await());

        Assert.Equal(["S/A/x", "S/A", "S/B"], error.Removed.ToArray());
        Assert.Equal("store one died", error.InnerException?.Message);
        Assert.Equal("store one died", error.Message);
    }

    /// <summary>
    /// A store whose <c>DeleteMany</c> throws while it is being CALLED, before it returns an
    /// observable, is that one store's failure. On unfixed code the throw happened while the
    /// composite enumerated its stores, outside every per-store outcome: it faulted the whole
    /// fan-out with the bare exception, and the rows the healthy store had removed were named to
    /// nobody, so no caller could retire their hubs.
    /// </summary>
    [Fact]
    public async Task TheComposite_AStoreThatThrowsWhenCalled_DoesNotHideWhatAnotherRemoved()
    {
        var healthy = await Seeded("S/A", "S/B");
        var throwing = await Seeded();
        IStorageAdapter composite = new PersistenceService(
        [
            new WritableProvider("healthy", healthy),
            new WritableProvider("throwing", new ThrowsWhenCalledAdapter(throwing)),
        ]);

        var error = await Assert.ThrowsAsync<BatchDeletePartiallyAppliedException>(
            () => composite.DeleteMany(["S/A", "S/B"]).FirstAsync().Await());

        Assert.Equal(["S/A", "S/B"], error.Removed.ToArray());
        Assert.Equal(ThrowsWhenCalledAdapter.Message, error.InnerException?.Message);
        Assert.Null(await ((IStorageAdapter)healthy).Read("S/A", Options).FirstAsync().Await());
    }

    /// <summary>
    /// The same path in TWO writable stores: one removes its copy, the other fails before removing
    /// its own. The node is still readable from the second store, so it must not be reported as
    /// removed: a caller would publish <c>Deleted</c> for it and retire the hub of a node that
    /// exists. On unfixed code the union named it because ONE store had removed it. A path the
    /// failing store removed itself, and one no store still holds, are still reported. The move's
    /// delete leg tombstones from this verdict, so the still-held path is never tombstoned either.
    /// </summary>
    [Fact]
    public async Task TheComposite_APathAFailedStoreStillHolds_IsNotReportedRemoved()
    {
        // Store one holds both rows and fails on S/A after removing S/B; store two holds S/A and
        // removes it. S/A is therefore still stored, in store one.
        var one = await Seeded("S/B", "S/A");
        var two = await Seeded("S/A");
        IStorageAdapter composite = new PersistenceService(
        [
            new WritableProvider("one", new SingleRowFaultAdapter(one, failAt: "S/A")),
            new WritableProvider("two", two),
        ]);

        // Driven through the MOVE's delete leg, which tombstones what the batch removed (#7930).
        var registry = new RecentlyDeletedRegistry();
        var error = await Assert.ThrowsAsync<BatchDeletePartiallyAppliedException>(
            () => MeshExtensions.TombstonedBatchDelete(composite, registry, ["S/B", "S/A"], Options, _ => { }, NullLogger.Instance).FirstAsync().Await());

        Assert.Equal(["S/B"], error.Removed.ToArray());
        Assert.NotNull(await composite.Read("S/A", Options).FirstAsync().Await());
        Assert.Null(await composite.Read("S/B", Options).FirstAsync().Await());
        // 🚨 The path store one still holds carries NO tombstone, although store two removed its copy
        // and published Deleted for it on the composite's merged feed — a tombstone would hide a row
        // that is still served. The control: the path no store holds any more IS tombstoned.
        Assert.False(registry.IsRecentlyDeleted("S/A"));
        Assert.True(registry.IsRecentlyDeleted("S/B"));
    }

    /// <summary>
    /// 🚨 A writable OVERRIDE of read-only content: the move's delete leg removes the override (the
    /// batch reports it removed — it was, from every WRITABLE store), but the read-only original goes
    /// on being served at the same path. That path must NOT be tombstoned: a tombstone makes the dying
    /// owner answer NotFound and every read answer DeleteInProgress for a node that is still served
    /// (review of #7930). The leg asks the persistence layer's whole-store verdict
    /// (<see cref="IStorageAdapter.Exists"/>, every provider that serves reads) before it marks.
    /// Control: a path only the writable store held IS tombstoned. On the unfixed leg the override's
    /// path was tombstoned because its writable copy was removed.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AMovedOverrideOfReadOnlyContent_LeavesTheOriginalServedAndUntombstoned(bool partWay)
    {
        var shipped = new InMemoryStorageAdapter();
        await ((IStorageAdapter)shipped).Write(Node("S/A") with { Name = "shipped" }, Options)
            .FirstAsync().Await(TestContext.Current.CancellationToken);
        var writable = await Seeded("S/B", "S/A", "S/C");
        IStorageAdapter composite = new PersistenceService(
        [
            new WritableProvider("db", partWay ? new SingleRowFaultAdapter(writable, failAt: "S/C") : writable),
            new ReadOnlyProvider("shipped", shipped),
        ]);
        var registry = new RecentlyDeletedRegistry();

        // Children-first order is irrelevant here; S/C (part-way variant) is where the store refuses.
        IReadOnlyList<string>? retired = null;
        var leg = MeshExtensions.TombstonedBatchDelete(
                composite, registry, ["S/B", "S/A", "S/C"], Options, removed => retired = removed, NullLogger.Instance)
            .FirstAsync();
        if (partWay)
        {
            var error = await Assert.ThrowsAsync<BatchDeletePartiallyAppliedException>(() => leg.Await());
            Assert.Equal(["S/B", "S/A"], error.Removed.ToArray());
        }
        else
            Assert.Equal(["S/B", "S/A", "S/C"], (await leg.Await()).ToArray());

        // Retirement follows the batch's verdict — the override's path included, since its served node
        // changed — whatever the tombstone decides.
        Assert.Equal(partWay ? ["S/B", "S/A"] : ["S/B", "S/A", "S/C"], retired!.ToArray());
        // The read-only original is still served at the override's path, and is not tombstoned.
        Assert.Equal("shipped", (await composite.Read("S/A", Options).FirstAsync().Await())?.Name);
        Assert.False(registry.IsRecentlyDeleted("S/A"));
        // The control: a path only the writable store held is gone for good, and tombstoned.
        Assert.Null(await composite.Read("S/B", Options).FirstAsync().Await());
        Assert.True(registry.IsRecentlyDeleted("S/B"));
        // A path the failed store still holds is neither reported removed nor tombstoned.
        Assert.Equal(!partWay, registry.IsRecentlyDeleted("S/C"));
    }

    private sealed class ReadOnlyProvider(string name, IStorageAdapter adapter) : IPartitionStorageProvider
    {
        public string Name => name;
        public bool IsReadOnly => true;
        public IStorageAdapter Adapter => adapter;
    }

    private sealed class WritableProvider(string name, IStorageAdapter adapter) : IPartitionStorageProvider
    {
        public string Name => name;
        public bool IsReadOnly => false;
        public IStorageAdapter Adapter => adapter;
    }

    /// <summary>A store whose <see cref="IStorageAdapter.DeleteMany"/> throws synchronously, before returning an observable.</summary>
    private sealed class ThrowsWhenCalledAdapter(IStorageAdapter inner) : ForwardingAdapter(inner)
    {
        public const string Message = "injected: DeleteMany threw while being called";

        public override IObservable<IReadOnlyList<string>> DeleteMany(IReadOnlyCollection<string> paths)
            => throw new InvalidOperationException(Message);
    }

    /// <summary>
    /// A real in-memory store whose single-row delete of one path FAULTS without removing it. It does
    /// not override <see cref="IStorageAdapter.DeleteMany"/>, so the batch runs the interface's
    /// default over this adapter's own <see cref="IStorageAdapter.DeleteIfExists"/>.
    /// </summary>
    private sealed class SingleRowFaultAdapter(IStorageAdapter inner, string failAt) : ForwardingAdapter(inner)
    {
        public override IObservable<bool> DeleteIfExists(string path)
            => string.Equals(path, failAt, StringComparison.Ordinal)
                ? Observable.Throw<bool>(new InvalidOperationException($"injected: the store refused to delete '{path}'"))
                : Inner.DeleteIfExists(path);
    }

    /// <summary>Forwards every member to a real store; subclasses override the one they inject a fault into.</summary>
    private abstract class ForwardingAdapter(IStorageAdapter inner) : IStorageAdapter
    {
        protected IStorageAdapter Inner => inner;

        public virtual IObservable<bool> DeleteIfExists(string path) => inner.DeleteIfExists(path);

        // Unless overridden, the interface's default batch over THIS adapter's DeleteIfExists.
        public virtual IObservable<IReadOnlyList<string>> DeleteMany(IReadOnlyCollection<string> paths)
            => StorageBatchDelete.OneAtATime(paths, DeleteIfExists);

        public IObservable<string> Delete(string path) => inner.Delete(path);

        public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
            => inner.Read(path, options);

        public IObservable<MeshNode?> Write(MeshNode node, JsonSerializerOptions options)
            => inner.Write(node, options);

        public IObservable<IReadOnlyCollection<string>> ListDescendantPaths(string rootPath)
            => inner.ListDescendantPaths(rootPath);

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
