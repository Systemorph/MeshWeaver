using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>A FAILED STORE WRITE OF A NODE'S OWN SAVE IS NEVER DROPPED.</b>
///
/// <para>The per-node hub persists its own node through a sampler: each sampled state is posted as a
/// <c>SaveMeshNodeRequest</c>, marked "requested" the moment the hub accepts it, and written by
/// <c>MeshDataSource.WriteSampledNode</c>. A write that FAILED in the store only logged a warning — and
/// because the state was already marked requested, the dispose-time flush (which persists only
/// UNSAVED states) skipped it too. The committed change then lived only in RAM: a recycle tore the hub
/// down, the next activation loaded the OLDER row, and the change was silently gone.</para>
///
/// <para>The fault is injected at the storage adapter — the outermost layer of the production chain —
/// and fires exactly ONCE, for the one path under test. The negative control runs the identical
/// sequence with no fault armed, so a pass there proves the recycle path itself is sound and the
/// difference is the failed write.</para>
/// </summary>
public class AFailedOwnSaveSurvivesARecycleTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // ——— fault injection —————————————————————————————————————————————————————————————————

    // The path whose NEXT write fails, and how many writes are left to fail. Set by the test,
    // read by the decorator on the storage thread.
    private volatile string? faultPath;
    private int faultsRemaining;
    // Completed by the decorator when it has injected the fault — the test waits on that, never on time.
    private readonly AsyncSubject<Unit> faultInjected = new();

    private sealed class WriteFaultingStorageAdapter(IStorageAdapter inner, AFailedOwnSaveSurvivesARecycleTest test)
        : IStorageAdapter
    {
        private IObservable<T>? Fault<T>(string path)
        {
            if (!string.Equals(path, test.faultPath, StringComparison.Ordinal)
                || Interlocked.Decrement(ref test.faultsRemaining) < 0)
                return null;
            test.faultInjected.OnNext(Unit.Default);
            test.faultInjected.OnCompleted();
            return Observable.Throw<T>(new InvalidOperationException(
                $"injected store failure for {path} (AFailedOwnSaveSurvivesARecycleTest)"));
        }

        public IObservable<DataChangeNotification> Changes => inner.Changes;
        public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options) => inner.Read(path, options);
        public IObservable<MeshNode> ReadMany(IReadOnlyCollection<string> paths, JsonSerializerOptions options)
            => inner.ReadMany(paths, options);
        public IObservable<MeshNode?> Write(MeshNode node, JsonSerializerOptions options)
            => Fault<MeshNode?>(node.Path) ?? inner.Write(node, options);
        public IObservable<IReadOnlyList<MeshNode>> WriteMany(IReadOnlyCollection<MeshNode> nodes, JsonSerializerOptions options)
            => nodes.Select(n => Fault<IReadOnlyList<MeshNode>>(n.Path)).FirstOrDefault(f => f is not null)
               ?? inner.WriteMany(nodes, options);
        public IObservable<bool?> WriteIfVersion(MeshNode node, long expectedVersion, JsonSerializerOptions options)
            => Fault<bool?>(node.Path) ?? inner.WriteIfVersion(node, expectedVersion, options);
        public IObservable<bool> Exists(string path) => inner.Exists(path);
        public IObservable<string> Delete(string path) => inner.Delete(path);
        public IObservable<bool> DeleteIfExists(string path) => inner.DeleteIfExists(path);
        public IObservable<string?> FindDeleteBlockingProvider(string path) => inner.FindDeleteBlockingProvider(path);
        public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)> ListChildPaths(string? parentPath)
            => inner.ListChildPaths(parentPath);
        public IObservable<IReadOnlyCollection<string>> ListDescendantPaths(string rootPath)
            => inner.ListDescendantPaths(rootPath);
        public IObservable<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatch(string fullPath, JsonSerializerOptions options)
            => inner.FindBestPrefixMatch(fullPath, options);
        public IObservable<(MeshNode? Node, int MatchedSegments)> ResolvePath(string fullPath, JsonSerializerOptions options)
            => inner.ResolvePath(fullPath, options);
        public IObservable<IEnumerable<string>> ListPartitionSubPaths(string nodePath) => inner.ListPartitionSubPaths(nodePath);
        public IObservable<object> GetPartitionObjects(string nodePath, string? subPath, JsonSerializerOptions options)
            => inner.GetPartitionObjects(nodePath, subPath, options);
        public IObservable<Unit> SavePartitionObjects(string nodePath, string? subPath, IReadOnlyCollection<object> objects, JsonSerializerOptions options)
            => inner.SavePartitionObjects(nodePath, subPath, objects, options);
        public IObservable<Unit> DeletePartitionObjects(string nodePath, string? subPath = null)
            => inner.DeletePartitionObjects(nodePath, subPath);
        public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null)
            => inner.GetPartitionMaxTimestamp(nodePath, subPath);
    }

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).ConfigureServices(services =>
        {
            // Wrap the outermost non-keyed IStorageAdapter registration (same hook as
            // CreateWhenTheStoreIsUnreachableTest), so the per-node hub's own save is the call that faults.
            var registered = services.Last(d => d.ServiceType == typeof(IStorageAdapter) && !d.IsKeyedService);
            services.Remove(registered);
            return services.AddSingleton<IStorageAdapter>(sp =>
                new WriteFaultingStorageAdapter(
                    registered.ImplementationFactory is { } factory
                        ? (IStorageAdapter)factory(sp)
                        : registered.ImplementationInstance as IStorageAdapter
                          ?? throw new InvalidOperationException(
                              "The IStorageAdapter registration is neither a factory nor an instance — update "
                              + "this hook; an unwrapped store would make every assertion here vacuous."),
                    this));
        });

    // ——— helpers —————————————————————————————————————————————————————————————————————————

    private static string NewId() => "fos" + Guid.NewGuid().ToString("N")[..8];

    private string? TextOf(MeshNode? node) => node?.ContentAs<MarkdownContent>(Mesh.JsonSerializerOptions)?.Content;

    private IStorageAdapter Store => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    /// <summary>Re-reads the DURABLE row until it carries <paramref name="text"/> — what a recycle reloads.</summary>
    private Task<MeshNode?> DurableRowCarries(string path, string text, string because)
        => Observable.Interval(TimeSpan.FromMilliseconds(100)).StartWith(0L)
            .SelectMany(_ => Store.Read(path, Mesh.JsonSerializerOptions).Take(1))
            .Where(n => TextOf(n) == text)
            .Should().Within(60.Seconds()).Emit(because, cancellationToken: TestContext.Current.CancellationToken);

    /// <summary>
    /// Creates a page, waits until its row is durable and its hub is live, then changes it through the
    /// OWNER's own data change (the sampler route, not a patch's post-commit flush), optionally with
    /// the store armed to fail that save once, and recycles the hub.
    /// </summary>
    private async Task<string> ChangeThenRecycle(bool failTheSave)
    {
        var ct = TestContext.Current.CancellationToken;
        var id = NewId();
        var path = $"{TestPartition}/{id}";
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await meshService.CreateNode(new MeshNode(id, TestPartition)
            {
                Name = id,
                NodeType = "Markdown",
                State = MeshNodeState.Active,
                Content = new MarkdownContent { Content = "v1" },
            })
            .Take(1).Should().Within(60.Seconds()).Emit("the page must exist first", cancellationToken: ct);
        await DurableRowCarries(path, "v1", "the created row is durable");

        await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null && TextOf(n) == "v1")
            .Should().Within(60.Seconds()).Emit("the node's hub serves the created state", cancellationToken: ct);
        var before = Mesh.GetHostedHub(new Address(path), HostedHubCreation.Never);
        before.Should().NotBeNull("the node's hub is activated before the change");

        if (failTheSave)
        {
            faultsRemaining = 1;
            faultPath = path;
        }

        // The change is the OWNER's own write — the route the persistence sampler persists (a
        // client's patch is written by the post-commit flush instead, which answers its caller).
        await before!.GetWorkspace().GetMeshNodeStream()
            .Update(n => n with { Content = new MarkdownContent { Content = "v2" } })
            .Take(1).Should().Within(60.Seconds()).Emit("the owner commits its own change", cancellationToken: ct);
        await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null && TextOf(n) == "v2")
            .Should().Within(60.Seconds()).Emit("the change is live", cancellationToken: ct);

        if (failTheSave)
        {
            await faultInjected.Should().Within(60.Seconds())
                .Emit("the sampled save reached the store and was failed once", cancellationToken: ct);
            // Disarm, so only that ONE write failed: every later write — the re-attempt — goes through.
            faultPath = null;
        }
        else
        {
            await DurableRowCarries(path, "v2", "with a healthy store the sampled save lands");
        }

        await RequestHub
            .RecycleNode(path, reason: "AFailedOwnSaveSurvivesARecycleTest: re-activate the node from its durable row")
            .Take(1)
            .Should().Within(90.Seconds()).Emit("the recycled address answers again", cancellationToken: ct);
        Mesh.GetHostedHub(new Address(path), HostedHubCreation.Never)
            .Should().NotBeSameAs(before, "the recycle replaced the activation, so what it serves was RE-READ");
        return path;
    }

    // ——— the guards ——————————————————————————————————————————————————————————————————————

    /// <summary>
    /// 🚨 THE GUARD. The store refuses the own save once; the hub is recycled. The change must be in
    /// the durable row and served by the fresh activation — never the older row.
    /// </summary>
    [Fact(Timeout = 240000)]
    public async Task AFailedOwnSave_IsReattempted_SoARecycleServesTheChange()
    {
        var path = await ChangeThenRecycle(failTheSave: true);

        await DurableRowCarries(path, "v2",
            "a save the store failed is re-attempted (at the latest by the dispose flush) — never dropped");
        await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null && TextOf(n) == "v2")
            .Should().Within(60.Seconds()).Emit("the re-activated hub serves the change, not the older row",
                cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Negative control: the identical sequence with no fault armed. It must pass, so the guard above
    /// fails only because of the failed write.
    /// </summary>
    [Fact(Timeout = 240000)]
    public async Task WithAHealthyStore_TheChangeSurvivesTheRecycle()
    {
        var path = await ChangeThenRecycle(failTheSave: false);

        await DurableRowCarries(path, "v2", "the durable row carries the change");
        await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null && TextOf(n) == "v2")
            .Should().Within(60.Seconds()).Emit("the re-activated hub serves the change",
                cancellationToken: TestContext.Current.CancellationToken);
    }
}

/// <summary>
/// 🚨 <b>A NODE'S OWN WRITES REACH THE STORE ONE AT A TIME, IN ORDER.</b> The failed-save re-attempt
/// above makes a newer write (or the dispose flush) follow a failed one quickly; if the OLDER write is
/// still in flight on a store whose pool runs writes concurrently and that has no conditional write,
/// it could land after the newer write or the flush and roll the row back. Every own write and the
/// flush are therefore queued on <c>PendingOwnSave.Enqueue</c>: a write starts only once the previous
/// one has terminated, either way. The writes here are hand-driven subjects, so the order is
/// observed exactly — no store, no timing.
/// </summary>
public class OwnWritesAreSerializedTest
{
    [Fact]
    public void AWriteStillInFlight_HoldsBackEveryLaterWriteAndTheFlush()
    {
        var pending = new MeshDataSourceExtensions.PendingOwnSave();
        var v2 = new Subject<Unit>();
        var v3 = new Subject<Unit>();
        var flush = new Subject<Unit>();
        var started = System.Collections.Immutable.ImmutableList<string>.Empty;
        Exception? flushFault = null;

        pending.Enqueue(() => { started = started.Add("v2"); return v2; });
        pending.Enqueue(() => { started = started.Add("v3"); return v3; });
        pending.Enqueue(() => { started = started.Add("flush"); return flush; })
            .Subscribe(_ => { }, ex => flushFault = ex);

        started.Should().Equal(["v2"], "v3 and the flush wait while the older v2 write is still in flight");

        v2.OnError(new InvalidOperationException("v2 failed in the store"));
        started.Should().Equal(["v2", "v3"], "a FAILED write settles its slot too, so the next write runs");

        v3.OnNext(Unit.Default);
        started.Should().Equal(["v2", "v3"], "a write's value is not its termination");
        v3.OnCompleted();
        started.Should().Equal(["v2", "v3", "flush"], "the flush runs only after every earlier write terminated");

        flush.OnError(new InvalidOperationException("flush failed"));
        flushFault.Should().BeOfType<InvalidOperationException>(
            "the queued write's own outcome is replayed to its caller — a failed flush surfaces as a dispose fault");
    }
}
