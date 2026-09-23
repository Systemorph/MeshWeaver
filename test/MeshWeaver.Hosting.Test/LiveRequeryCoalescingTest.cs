using System;
using System.Collections.Generic;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// <see cref="StorageAdapterMeshQueryProvider"/> re-queries a LIVE query once per BATCH of relevant
/// changes, never once per change (MeshWeaver#5344 / #5315 / #1186).
///
/// <para><b>The defect.</b> The live pipeline was <c>changeBuffer.Select(_ =&gt; RunQuery()).Concat()</c>:
/// one full scope walk queued per notification, with no bound. A burst of N changes under one live
/// query's scope cost N back-to-back walks, and the queue grows fastest exactly when reads are
/// slowest. On partitioned Postgres this provider serves every path with a <c>_</c> segment, so that
/// backlog sat on the read pools an activation's path resolution waits on — the Initial the query
/// fan-in's 15 s stall terminal then fires on. Plugins#2328 removed the same queue from the Postgres
/// fan-out provider; <see cref="MeshWeaver.Reactive.CoalesceWhileRunningExtensions"/> is the shape
/// both now use.</para>
///
/// <para><b>How the window is held.</b> The scope walk runs inline on the thread that delivers the
/// notification. The FIRST re-query's walk of the query's base path is parked on a dedicated thread;
/// while it is parked, the rest of the burst is delivered from the test thread; then the walk is
/// released and the test counts how many walks of the base path the live query issued.</para>
/// </summary>
public class LiveRequeryCoalescingTest
{
    private static readonly JsonSerializerOptions Options = new();
    private const string Base = "acme/_Activity";
    private const int Burst = 12;

    /// <summary>
    /// Delegates every read/write to a real <see cref="InMemoryStorageAdapter"/>, publishes the
    /// test's own change feed, and counts — and, once armed, parks the first of — the walks of
    /// <see cref="Base"/>.
    /// </summary>
    private sealed class CountingAdapter(InMemoryStorageAdapter inner, IObservable<DataChangeNotification> feed)
        : IStorageAdapter
    {
        // Worker → test: completed once the armed walk has parked. Test → worker: a volatile flag
        // the parked walk polls under a bounded SpinUntil.
        private readonly AsyncSubject<Unit> walkParked = new();
        private int release;
        private int armed;
        private int parkTimedOut;
        private int walks;

        public IObservable<Unit> WalkParked => walkParked;
        public int Walks => Volatile.Read(ref walks);
        public bool ParkTimedOut => Volatile.Read(ref parkTimedOut) != 0;

        /// <summary>From now on, count walks of <see cref="Base"/> and park the first one.</summary>
        public void Arm() => Volatile.Write(ref armed, 1);

        /// <summary>Releases the parked walk; idempotent, so a <c>finally</c> can always call it.</summary>
        public void Release() => Volatile.Write(ref release, 1);

        public IObservable<DataChangeNotification> Changes => feed;

        public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)>
            ListChildPaths(string? parentPath)
            => inner.ListChildPaths(parentPath).Do(_ =>
            {
                if (!string.Equals(parentPath, Base, StringComparison.OrdinalIgnoreCase)) return;
                if (Volatile.Read(ref armed) == 0) return;
                if (Interlocked.Increment(ref walks) != 1) return;
                walkParked.OnNext(Unit.Default);
                walkParked.OnCompleted();
                if (!SpinWait.SpinUntil(() => Volatile.Read(ref release) == 1, TimeSpan.FromSeconds(30)))
                    Volatile.Write(ref parkTimedOut, 1);
            });

        public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
            => inner.Read(path, options);
        public IObservable<MeshNode?> Write(MeshNode node, JsonSerializerOptions options)
            => inner.Write(node, options);
        public IObservable<string> Delete(string path) => inner.Delete(path);
        public IObservable<bool> Exists(string path) => inner.Exists(path);
        public IObservable<object> GetPartitionObjects(
            string nodePath, string? subPath, JsonSerializerOptions options)
            => inner.GetPartitionObjects(nodePath, subPath, options);
        public IObservable<Unit> SavePartitionObjects(
            string nodePath, string? subPath, IReadOnlyCollection<object> objects, JsonSerializerOptions options)
            => inner.SavePartitionObjects(nodePath, subPath, objects, options);
        public IObservable<Unit> DeletePartitionObjects(string nodePath, string? subPath = null)
            => inner.DeletePartitionObjects(nodePath, subPath);
        public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null)
            => inner.GetPartitionMaxTimestamp(nodePath, subPath);
    }

    [Fact]
    public async Task ABurstOfChanges_WhileAReQueryIsInFlight_CostsOneFollowUp_NotOneWalkPerChange()
    {
        var ct = TestContext.Current.CancellationToken;
        var feed = new Subject<DataChangeNotification>();
        var adapter = new CountingAdapter(new InMemoryStorageAdapter(), feed);
        var provider = new StorageAdapterMeshQueryProvider(adapter);

        var initial = new AsyncSubject<Unit>();
        using var subscription = provider
            .Query<MeshNode>(
                MeshQueryRequest.FromQueries([$"path:{Base} scope:children"], "system-security"),
                Options)
            .Subscribe(c =>
            {
                if (c.ChangeType != QueryChangeType.Initial) return;
                initial.OnNext(Unit.Default);
                initial.OnCompleted();
            });
        await initial.Should().Within(TestTimeouts.Convergence)
            .Emit("the live query never emitted its Initial", cancellationToken: ct);

        adapter.Arm();
        // 🚨 The FIRST change is delivered OFF the test thread: its re-query walks inline on the
        // delivering thread and parks there, so delivering it here would park the test itself.
        var first = new Thread(() => feed.OnNext(DataChangeNotification.Updated($"{Base}/n00", null)))
        { IsBackground = true };
        first.Start();
        try
        {
            await adapter.WalkParked.Should().Within(TestTimeouts.Convergence)
                .Emit("the first change never started a re-query", cancellationToken: ct);

            // The rest of the burst arrives while that re-query is in flight.
            for (var i = 1; i < Burst; i++)
                feed.OnNext(DataChangeNotification.Updated($"{Base}/n{i:D2}", null));
        }
        finally
        {
            adapter.Release();
        }
        first.Join(TimeSpan.FromSeconds(30)).Should().BeTrue("the parked re-query never finished");
        adapter.ParkTimedOut.Should().BeFalse(
            "the park must be RELEASED, not time out — otherwise the window was never held and the count below proves nothing");

        // The in-flight re-query plus ONE follow-up for the eleven changes that arrived while it ran.
        // The per-change queue walked twelve times.
        await Observable.Interval(TimeSpan.FromMilliseconds(20)).StartWith(0L)
            .Select(_ => adapter.Walks)
            .Where(walks => walks >= 2)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the follow-up re-query never ran", cancellationToken: ct);
        await Observable.Interval(TimeSpan.FromMilliseconds(20)).StartWith(0L)
            .Select(_ => adapter.Walks)
            .Where(walks => walks > 2)
            .Should().NotEmit(3.Seconds(),
                $"{Burst} changes during one in-flight re-query must fold into ONE follow-up — "
                + "one full walk per change is the backlog the fan-in's stall terminal fires on",
                ct);
        adapter.Walks.Should().Be(2);
    }
}
