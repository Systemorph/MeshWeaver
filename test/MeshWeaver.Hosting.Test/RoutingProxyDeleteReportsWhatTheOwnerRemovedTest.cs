using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Persistence.PartitionStorage;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Testing.FaultInjection;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <see cref="RoutingProxyAdapter"/> reports a delete as what the OWNING partition-storage hub
/// says it removed — never as the request (MeshWeaver#7446).
///
/// <para><b>The defect.</b> <c>Delete(path)</c> mapped a <see cref="DeleteBatchResponse"/> carrying an
/// <c>Error</c> to emitting the path, as if it had been deleted, and the proxy overrode neither
/// <c>DeleteIfExists</c> nor <c>DeleteMany</c>: the inherited defaults (<c>DeleteIfExists</c> → true,
/// then one row at a time) reported every failed remote delete as removed and never read
/// <see cref="DeleteBatchResponse.DeletedPaths"/>. A move or a recursive delete would then retire the
/// hubs of nodes that are still stored.</para>
///
/// <para>The wiring is REAL end to end: the mesh hub posts through the proxy to a partition-storage
/// hub the <see cref="PartitionStorageRouter"/> spawns, whose handler runs the store's own
/// <c>DeleteMany</c>. Only the store's fault is injected, by
/// <see cref="FaultInjectingStorageAdapter"/> over a real <see cref="InMemoryStorageAdapter"/>.</para>
/// </summary>
public class RoutingProxyDeleteReportsWhatTheOwnerRemovedTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly JsonSerializerOptions Options = new();

    private static MeshNode Node(string path)
    {
        var slash = path.LastIndexOf('/');
        return new MeshNode(path[(slash + 1)..], path[..slash]) { NodeType = "Test" };
    }

    private async Task<(IStorageAdapter Proxy, FaultInjectingStorageAdapter Store)> Wired(params string[] seeded)
    {
        var store = new FaultInjectingStorageAdapter(new InMemoryStorageAdapter(), "partition");
        foreach (var path in seeded)
            await ((IStorageAdapter)store).Write(Node(path), Options).FirstAsync().Await(TestContext.Current.CancellationToken);
        var router = new PartitionStorageRouter(Mesh, [new WritableProvider(store)], new MemoryCache(new MemoryCacheOptions()));
        return (new RoutingProxyAdapter(Mesh, router), store);
    }

    private static Task<MeshNode?> Stored(IStorageAdapter store, string path)
        => store.Read(path, Options).FirstAsync().Await(TestContext.Current.CancellationToken);

    /// <summary>
    /// A remote delete the owner FAILED is a fault. On unfixed code <c>Delete</c> emitted the path, so
    /// the caller read a node that is still stored as gone.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Delete_ThatTheOwnerFailed_IsAFault_NeverThePath()
    {
        var (proxy, store) = await Wired("S/A");
        var fault = store.FailOperations("S/A", StorageLegs.Delete);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => proxy.Delete("S/A").FirstAsync().Await(TestContext.Current.CancellationToken));

        Assert.True(fault.ArrivalCount > 0, "the injected store fault was never served — the test proved nothing");
        Assert.NotNull(await Stored(store, "S/A"));
    }

    /// <summary>
    /// <c>DeleteIfExists</c> is strict from the owner's answer: a path the store does not hold was not
    /// removed. On unfixed code the interface default answered true for every delete.
    /// Negative control: a stored path is removed and answers true.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task DeleteIfExists_AnswersWhatTheOwnerRemoved()
    {
        var (proxy, store) = await Wired("S/A");

        Assert.False(await proxy.DeleteIfExists("S/Missing").FirstAsync().Await(TestContext.Current.CancellationToken));
        Assert.True(await proxy.DeleteIfExists("S/A").FirstAsync().Await(TestContext.Current.CancellationToken));
        Assert.Null(await Stored(store, "S/A"));
    }

    /// <summary>
    /// A batch over two partition hubs whose SECOND run fails part-way raises a
    /// <see cref="BatchDeletePartiallyAppliedException"/> naming exactly what left storage — the first
    /// run's removal and the second run's own <see cref="DeleteBatchResponse.DeletedPaths"/> — and the
    /// row the owner kept is not in it. On unfixed code the batch SUCCEEDED and named all three paths.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task DeleteMany_FailingInALaterRun_NamesExactlyWhatWasRemoved()
    {
        var (proxy, store) = await Wired("P/a", "Q/b", "Q/c");
        var fault = store.FailDeleteMany("Q/c");

        var error = await Assert.ThrowsAsync<BatchDeletePartiallyAppliedException>(
            () => proxy.DeleteMany(["P/a", "Q/b", "Q/c"]).FirstAsync().Await(TestContext.Current.CancellationToken));

        Assert.True(fault.ArrivalCount > 0, "the injected store fault was never served — the test proved nothing");
        Assert.Equal(["P/a", "Q/b"], error.Removed.ToArray());
        Assert.Null(await Stored(store, "P/a"));
        Assert.Null(await Stored(store, "Q/b"));
        Assert.NotNull(await Stored(store, "Q/c"));
    }

    /// <summary>
    /// The success path, and the control for the failing case above: a batch the owners complete
    /// returns what they removed, in the caller's (children-first) order. A path no store held is
    /// absent rather than reported removed; on unfixed code the one-at-a-time default reported it.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task DeleteMany_ThatSucceeds_ReturnsWhatWasRemoved_InTheCallersOrder()
    {
        var (proxy, store) = await Wired("P/a/x", "P/a", "Q/b");

        var removed = await proxy.DeleteMany(["P/a/x", "P/a", "P/never", "Q/b"])
            .FirstAsync().Await(TestContext.Current.CancellationToken);

        Assert.Equal(["P/a/x", "P/a", "Q/b"], removed.ToArray());
        Assert.Null(await Stored(store, "P/a"));
        Assert.Null(await Stored(store, "Q/b"));
    }

    private sealed class WritableProvider(IStorageAdapter adapter) : IPartitionStorageProvider
    {
        public string Name => "partition";
        public bool IsReadOnly => false;
        public IStorageAdapter Adapter => adapter;
    }
}
