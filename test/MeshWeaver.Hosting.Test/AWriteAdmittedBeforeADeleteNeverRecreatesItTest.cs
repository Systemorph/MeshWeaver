using System;
using System.Collections.Generic;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// The store of record for <see cref="AWriteAdmittedBeforeADeleteNeverRecreatesItTest"/>: a real
/// <see cref="InMemoryStorageAdapter"/> that can PARK writes to one path until the test releases
/// them. It sits BELOW the production decorators, so a parked write has already been admitted by
/// the subtree-deletion guard — exactly a write queued on the I/O pool, or slow at the backend,
/// when a delete begins. Parking is an Rx continuation on the release, never a sleep. A parked
/// write is EAGER, as the production stores are (<c>IIoPool.Run</c>): it commits once released
/// even if its caller has unsubscribed.
/// </summary>
internal sealed class ParkedWriteStorageAdapter(InMemoryStorageAdapter inner) : IStorageAdapter
{
    private string? _parkPath;
    private readonly AsyncSubject<Unit> _release = new();
    private readonly AsyncSubject<Unit> _writeParked = new();

    /// <summary>Parks every write to <paramref name="path"/> from now until <see cref="Release"/>.</summary>
    public void ParkWritesTo(string path) => Volatile.Write(ref _parkPath, path);

    /// <summary>Completes once a write to the parked path has reached the store and is parked.</summary>
    public IObservable<Unit> WriteParked => _writeParked;

    /// <summary>Lets every parked write through to the store. Idempotent.</summary>
    public void Release()
    {
        Volatile.Write(ref _parkPath, null);
        _release.OnNext(Unit.Default);
        _release.OnCompleted();
    }

    private bool Parked(string path)
        => Volatile.Read(ref _parkPath) is { } parked
           && string.Equals(parked, path, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public IObservable<MeshNode?> Write(MeshNode node, JsonSerializerOptions options)
        => Observable.Defer(() =>
        {
            if (!Parked(node.Path))
                return inner.Write(node, options);
            _writeParked.OnNext(Unit.Default);
            _writeParked.OnCompleted();
            // EAGER, like IIoPool.Run under every production store: once called, the write runs
            // to its own terminal whoever unsubscribes — so a caller's unsubscribe cannot cancel it.
            var parked = _release.SelectMany(_ => inner.Write(node, options)).Replay();
            parked.Connect();
            return parked.AsObservable();
        });

    /// <inheritdoc />
    public IObservable<bool?> WriteIfVersion(MeshNode node, long expectedVersion, JsonSerializerOptions options)
        => inner.WriteIfVersion(node, expectedVersion, options);

    /// <inheritdoc />
    public IObservable<string> Delete(string path) => inner.Delete(path);

    /// <inheritdoc />
    public IObservable<bool> DeleteIfExists(string path) => inner.DeleteIfExists(path);

    /// <inheritdoc />
    public IObservable<IReadOnlyList<string>> DeleteMany(IReadOnlyCollection<string> paths)
        => ((IStorageAdapter)inner).DeleteMany(paths);

    /// <inheritdoc />
    public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options) => inner.Read(path, options);

    /// <inheritdoc />
    public IObservable<bool> Exists(string path) => inner.Exists(path);

    /// <inheritdoc />
    public IObservable<bool> ExistsInWritableStorage(string path)
        => ((IStorageAdapter)inner).ExistsInWritableStorage(path);

    /// <inheritdoc />
    public IObservable<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatch(
        string fullPath, JsonSerializerOptions options)
        => inner.FindBestPrefixMatch(fullPath, options);

    /// <inheritdoc />
    public IObservable<(MeshNode? Node, int MatchedSegments)> ResolvePath(
        string fullPath, JsonSerializerOptions options)
        => inner.ResolvePath(fullPath, options);

    /// <inheritdoc />
    public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)>
        ListChildPaths(string? parentPath)
        => inner.ListChildPaths(parentPath);

    /// <inheritdoc />
    public IObservable<IReadOnlyCollection<string>> ListDescendantPaths(string rootPath)
        => inner.ListDescendantPaths(rootPath);

    /// <inheritdoc />
    public IObservable<IEnumerable<string>> ListPartitionSubPaths(string nodePath)
        => ((IStorageAdapter)inner).ListPartitionSubPaths(nodePath);

    /// <inheritdoc />
    public IObservable<object> GetPartitionObjects(string nodePath, string? subPath, JsonSerializerOptions options)
        => inner.GetPartitionObjects(nodePath, subPath, options);

    /// <inheritdoc />
    public IObservable<Unit> SavePartitionObjects(
        string nodePath, string? subPath, IReadOnlyCollection<object> objects, JsonSerializerOptions options)
        => inner.SavePartitionObjects(nodePath, subPath, objects, options);

    /// <inheritdoc />
    public IObservable<Unit> DeletePartitionObjects(string nodePath, string? subPath = null)
        => inner.DeletePartitionObjects(nodePath, subPath);

    /// <inheritdoc />
    public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null)
        => inner.GetPartitionMaxTimestamp(nodePath, subPath);

    /// <inheritdoc />
    public IObservable<DataChangeNotification> Changes => inner.Changes;
}

/// <summary>
/// #6719: a write ADMITTED by the storage guard before a delete begins must never commit AFTER the
/// delete and recreate the node. The guard used to check only at admission, so a write already on
/// its way to the store (parked here, as an I/O-pool queue or a slow backend would hold it) was not
/// re-checked when it committed; the delete answered "removed", the write then landed, and its
/// post-commit <c>Supersede</c> erased the tombstone so the recreate looked legitimate.
///
/// <para>The fault is injected deterministically: the write is parked BELOW the production
/// decorators, so it has passed the guard. The delete is started only then, and the write is
/// released only once the delete has provably opened its scope — the ordering the defect needs,
/// produced by construction rather than by timing.</para>
/// </summary>
public class AWriteAdmittedBeforeADeleteNeverRecreatesItTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private readonly ParkedWriteStorageAdapter _store = new(new InMemoryStorageAdapter());

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder.ConfigureServices(services =>
        {
            // Registered BEFORE the bootstrap's TryAddSingleton, so this IS the store of record and
            // the production decorators (subtree guard, monotonic guard, version writing) wrap it.
            services.AddSingleton<IStorageAdapter>(_store);
            return services;
        }));

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The outermost adapter — the one every in-process writer goes through.</summary>
    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    private RecentlyDeletedRegistry Registry => Mesh.ServiceProvider.GetRequiredService<RecentlyDeletedRegistry>();

    /// <summary>
    /// The window in which a delete that does NOT wait for the admitted write would answer. It must
    /// sit strictly INSIDE the delete's own stage budget: wider, and the waiting delete's
    /// <c>settle-admitted-writes</c> stage times out first, and the instrument reads the product's
    /// own timeout as "answered while parked". On CI <see cref="TestTimeouts.Quick"/> is the
    /// CI-scaled convergence over three (~36 s), past the 30 s budget, which is exactly how it failed.
    /// </summary>
    private TimeSpan WhileParkedWindow
    {
        get
        {
            var budget = (Mesh.ServiceProvider.GetService<MeshOperationOptions>() ?? new MeshOperationOptions()).Timeout;
            var quarter = TimeSpan.FromTicks(budget.Ticks / 4);
            return quarter < TestTimeouts.Quick ? quarter : TestTimeouts.Quick;
        }
    }

    [Fact]
    public Task AWriteAdmittedBeforeTheDelete_CommitsBeforeIt_AndTheNodeStaysDeleted()
        => RunScenario(callerUnsubscribes: false);

    /// <summary>
    /// The same ordering when the write's CALLER gives up (a timeout, an unsubscribe) while the write
    /// is in flight. The store write is eager and still commits, so the caller leaving must not
    /// release the admission: the delete still waits for the write's own terminal.
    /// </summary>
    [Fact]
    public Task AWriteWhoseCallerUnsubscribed_IsStillWaitedFor_AndTheNodeStaysDeleted()
        => RunScenario(callerUnsubscribes: true);

    private async Task RunScenario(bool callerUnsubscribes)
    {
        var node = await NodeFactory.CreateNode(
                new MeshNode($"admitted-{Guid.NewGuid():N}"[..17], TestPartition) { Name = "seed", NodeType = "Markdown" })
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: Ct);
        var path = node.Path;

        // 1. A write admitted by the guard and parked at the store — on its way, not yet committed.
        _store.ParkWritesTo(path);
        var write = Storage.Write(node with { Name = "admitted before the delete" }, Mesh.JsonSerializerOptions)
            .Materialize()
            .Where(n => n.Kind != System.Reactive.NotificationKind.OnCompleted)
            .Replay(1);
        var writeConnection = write.Connect();

        // Released explicitly at step 5; the finally only guarantees a failing assertion cannot
        // strand the parked write (Release is idempotent).
        try
        {
            await _store.WriteParked.Should().Within(TestTimeouts.Convergence)
                .Emit("the write passed the guard and is parked at the store", cancellationToken: Ct);

            // The caller walks away from the in-flight write. The (eager) store write keeps going.
            if (callerUnsubscribes)
                writeConnection.Dispose();

            // 2. The delete starts while the write is in flight.
            var delete = ObserveNodeOperation(new DeleteNodeRequest(path))
                .Select(d => d.Message)
                .Replay(1);
            using var deleteConnection = delete.Connect();

            // 3. The switch: release only once the delete has provably begun — its scope is open, or
            //    it has already answered. Without this the test could release before the delete even
            //    started, and would pass with or without the fix.
            await Observable.Interval(TimeSpan.FromMilliseconds(20)).StartWith(0L)
                .Select(tick => Registry.IsUnderActiveDeletion(path, out var _))
                .Where(open => open)
                .Select(_ => Unit.Default)
                .Merge(delete.Select(_ => Unit.Default))
                .Should().Within(TestTimeouts.Convergence)
                .Emit("the delete has begun", cancellationToken: Ct);

            // 4. Give a delete that does NOT wait for the admitted write every chance to finish while it
            //    is parked. A "nothing happened" reading, so a bounded window is the instrument — and
            //    it sits inside the delete's own budget (WhileParkedWindow).
            var whileParked = await delete.Select(r => (DeleteNodeResponse?)r)
                .Merge(Observable.Timer(WhileParkedWindow).Select(_ => (DeleteNodeResponse?)null))
                .Should().Within(TestTimeouts.Convergence).Emit("either the answer or the window", cancellationToken: Ct);
            Output.WriteLine(whileParked is null
                ? "[delete] still waiting for the admitted write while it is parked"
                : $"[delete] answered while the admitted write was parked: success={whileParked.Success} error={whileParked.Error}");

            // 5. Release the admitted write and let everything settle.
            _store.Release();
            if (!callerUnsubscribes)
            {
                var writeOutcome = await write.Should().Within(TestTimeouts.Convergence)
                    .Emit("the admitted write terminates once released", cancellationToken: Ct);
                Output.WriteLine($"[write] {writeOutcome.Kind} {writeOutcome.Exception?.Message}");
                writeOutcome.Kind.Should().Be(System.Reactive.NotificationKind.OnNext,
                    "the admitted write commits — BEFORE the delete, which then removes it");
            }

            var response = await delete.Should().Within(TestTimeouts.Convergence)
                .Emit("the delete answers", cancellationToken: Ct);
            Output.WriteLine($"[delete] success={response.Success} error={response.Error}");
            response.Success.Should().BeTrue(response.Error ?? "nothing objects to deleting this node");

            // THE SUBJECT: the delete answered "removed" — the store of record must agree. Read once
            // the admitted write has been released; a delete that waited for it answered only after
            // it committed.
            var stored = await _store.Read(path, Mesh.JsonSerializerOptions)
                .DefaultIfEmpty(null)
                .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: Ct);
            Output.WriteLine($"[store] {(stored is null ? "absent" : $"PRESENT name='{stored.Name}' v{stored.Version}")}");
            stored.Should().BeNull(
                "the delete answered success — a write admitted before it must not commit after it and "
                + "recreate the node");

            whileParked.Should().BeNull(
                "the delete must wait for the write admitted before it, not answer while it is in flight");
        }
        finally
        {
            _store.Release();
            writeConnection.Dispose();
        }
    }
}
