using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Reactive;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// The pedestrian <see cref="StorageAdapterMeshQueryProvider"/> must never starve the shared read
/// pool (Doc/Architecture/QueryFanInStallTerminal → "Head-of-line starvation of pg-read").
///
/// <para><b>The production shape.</b> On the control instance a subscription to
/// <c>namespace:Admin scope:descendants nodeType:Thread</c> was served by the pedestrian, because
/// <c>DefersToNativeProvider</c> kept every scoped SATELLITE read local and the partitioned Postgres
/// persistence is not an <see cref="IScopedQueryStorageAdapter"/>, so the walk short-circuit never
/// fired. The walk then expanded with unbounded <c>SelectMany</c>s — every child listing and every
/// per-path read SUBSCRIBED at once — enqueueing ~2×N admissions on the process-wide FIFO
/// <c>pg-read:</c> pool, ahead of everything else. A one-row <c>path:Hosting/PlatformBuilds</c> probe
/// queued behind it missed the fan-in's 15 s Initial bound (<c>QueryProviderStalledException</c>).</para>
///
/// <para><b>The rig.</b> A store that is deliberately NOT an <see cref="IScopedQueryStorageAdapter"/>
/// (the path-routing adapter's shape), whose <c>ListChildPaths</c> and <c>Read</c> run through ONE
/// <see cref="IoPool"/> of cap 2 with 20 ms of latency each — a small, slow, shared pool — seeded with
/// 1,020 nodes under <c>Admin</c>. It counts every storage request it receives and the peak number
/// OUTSTANDING (subscribed and not yet terminated: queued in the pool or running), which is the
/// number of queue positions one query occupies.</para>
/// </summary>
public class PedestrianWalkHeadOfLineTest
{
    private static readonly JsonSerializerOptions Options = new();
    private const int Groups = 20;
    private const int PerGroup = 50;
    private const string AdminThreads = "namespace:Admin scope:descendants nodeType:Thread";
    private static readonly TimeSpan Latency = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// A slow, pooled, NON-scoped store. Every <see cref="ListChildPaths"/> / <see cref="Read"/> is one
    /// pool admission; <see cref="Requests"/> counts them, <see cref="PeakOutstanding"/> is the most that
    /// were queued-or-running at once, <see cref="Completed"/> ticks on every terminal.
    /// </summary>
    private sealed class PooledSlowStore(IIoPool pool, ImmutableDictionary<string, MeshNode> nodes) : IStorageAdapter
    {
        private int requests;
        private int outstanding;
        private int peak;
        private int completed;
        private readonly ISubject<int> completedTicks = Subject.Synchronize(new BehaviorSubject<int>(0));

        public int Requests => Volatile.Read(ref requests);
        public int PeakOutstanding => Volatile.Read(ref peak);
        public IObservable<int> Completed => completedTicks;

        private IObservable<T> Pooled<T>(Func<T> answer)
            => Observable.Defer(() =>
            {
                Interlocked.Increment(ref requests);
                var now = Interlocked.Increment(ref outstanding);
                int seen;
                while (now > (seen = Volatile.Read(ref peak))
                       && Interlocked.CompareExchange(ref peak, now, seen) != seen) { }
                return pool.Invoke(async ct =>
                    {
                        await Task.Delay(Latency, ct).ConfigureAwait(false);
                        return answer();
                    })
                    .Finally(() =>
                    {
                        Interlocked.Decrement(ref outstanding);
                        completedTicks.OnNext(Interlocked.Increment(ref completed));
                    });
            });

        public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)>
            ListChildPaths(string? parentPath)
            => Pooled<(IEnumerable<string>, IEnumerable<string>)>(() => (
                nodes.Values
                    .Where(n => string.Equals(n.Namespace, parentPath ?? "", StringComparison.OrdinalIgnoreCase))
                    .Select(n => n.Path)
                    .ToList(),
                Enumerable.Empty<string>()));

        public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
            => Pooled(() => nodes.TryGetValue(path, out var node) ? node : null);

        public IObservable<MeshNode?> Write(MeshNode node, JsonSerializerOptions options)
            => Observable.Throw<MeshNode?>(new NotSupportedException("read-only rig"));
        public IObservable<string> Delete(string path)
            => Observable.Throw<string>(new NotSupportedException("read-only rig"));
        public IObservable<bool> Exists(string path) => Observable.Return(nodes.ContainsKey(path));
        public IObservable<object> GetPartitionObjects(string nodePath, string? subPath, JsonSerializerOptions options)
            => Observable.Empty<object>();
        public IObservable<Unit> SavePartitionObjects(
            string nodePath, string? subPath, IReadOnlyCollection<object> objects, JsonSerializerOptions options)
            => Observable.Return(Unit.Default);
        public IObservable<Unit> DeletePartitionObjects(string nodePath, string? subPath = null)
            => Observable.Return(Unit.Default);
        public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null)
            => Observable.Return<DateTimeOffset?>(null);
    }

    /// <summary>1,020 primary nodes under <c>Admin</c> (20 groups × 50), and three under <c>Other</c>.</summary>
    private static ImmutableDictionary<string, MeshNode> Seed()
    {
        var b = ImmutableDictionary.CreateBuilder<string, MeshNode>(StringComparer.OrdinalIgnoreCase);
        void Add(string id, string ns) { var n = new MeshNode(id, ns) { NodeType = "Markdown" }; b[n.Path] = n; }
        for (var g = 0; g < Groups; g++)
        {
            Add($"G{g:D2}", "Admin");
            for (var i = 0; i < PerGroup; i++)
                Add($"N{i:D3}", $"Admin/G{g:D2}");
        }
        for (var i = 0; i < 3; i++)
            Add($"O{i}", "Other");
        return b.ToImmutable();
    }

    // Short drain budgets: the walk under test is abandoned mid-flight at the end of each test.
    private static IoPool SmallSlowPool() => new(2, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1));

    private static IMeshQueryCore Core(PooledSlowStore store, bool deferToNative)
        => new MeshQuery(
            [new StorageAdapterMeshQueryProvider(store,
                options: new StorageAdapterQueryProviderOptions { DeferToNativeProvider = deferToNative })],
            hub: null!);

    private static MeshQueryRequest AsSystem(string query) => MeshQueryRequest.FromQuery(query, WellKnownUsers.System);

    /// <summary>
    /// With a native partitioned provider wired (<c>DeferToNativeProvider</c>), a scoped SATELLITE
    /// read is the native provider's: the pedestrian contributes an empty Initial and touches the store
    /// not at all. Negative control (unfixed provider): it walks all 1,020 nodes — ~2,040 admissions —
    /// and its Initial does not arrive within the bound.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ScopedSatelliteRead_WithANativeProvider_DoesNotWalkTheStore()
    {
        var ct = TestContext.Current.CancellationToken;
        using var pool = SmallSlowPool();
        var store = new PooledSlowStore(pool, Seed());

        var frame = await Core(store, deferToNative: true)
            .Query<MeshNode>(AsSystem(AdminThreads), Options)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(5))
            .Await(ct);

        frame.Items.Should().BeEmpty("the pedestrian contributes nothing to a query the native provider owns");
        store.Requests.Should().BeLessThanOrEqualTo(1,
            "a scoped satellite read with a native provider wired must not walk the partition — the walk "
            + "cannot even find threads (they live in a satellite table under node-less _Thread segments) "
            + "and cost two pg-read admissions per node, ahead of every other read in the process");
    }

    /// <summary>
    /// Without a native provider the pedestrian still walks — but ONE query may never hold more than a
    /// handful of queue positions on the shared pool: at most <c>WalkConcurrency</c> (4) listings and 4
    /// reads, i.e. ≤ 8 outstanding. Negative control (unfixed provider): every listing of a level and
    /// every read is subscribed at once — measured 2,001 outstanding (about 2×N).
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task TheWalk_NeverHoldsMoreThanItsBoundOnTheSharedPool()
    {
        var ct = TestContext.Current.CancellationToken;
        using var pool = SmallSlowPool();
        var store = new PooledSlowStore(pool, Seed());

        using (Core(store, deferToNative: false)
                   .Query<MeshNode>(AsSystem(AdminThreads), Options)
                   .Subscribe(_ => { }, _ => { }))
        {
            // Well into the read phase: 21 listings, then reads.
            await store.Completed.Where(c => c >= 200).FirstAsync()
                .Timeout(TimeSpan.FromSeconds(20)).Await(ct);
        }

        store.PeakOutstanding.Should().BeLessThanOrEqualTo(8,
            "one query's walk may keep at most 4 listings and 4 reads outstanding on the shared pool; an "
            + "unbounded walk enqueues every node of the partition ahead of every other query");
    }

    /// <summary>
    /// The consequence that matters: while the Admin walk runs, ANOTHER scoped query on the same pool
    /// gets its Initial promptly, because it queues behind at most the walk's bound, not behind the
    /// walk's whole remainder. Negative control (unfixed provider): ~1,000 reads are queued ahead of it —
    /// measured 14.4 s.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AnotherQuery_GetsItsInitialPromptly_WhileTheWalkRuns()
    {
        var ct = TestContext.Current.CancellationToken;
        using var pool = SmallSlowPool();
        var store = new PooledSlowStore(pool, Seed());
        var core = Core(store, deferToNative: false);

        using (core.Query<MeshNode>(AsSystem(AdminThreads), Options).Subscribe(_ => { }, _ => { }))
        {
            // The walk is under way and has filled whatever queue positions it is going to take.
            await store.Completed.Where(c => c >= 60).FirstAsync()
                .Timeout(TimeSpan.FromSeconds(20)).Await(ct);

            var clock = Stopwatch.StartNew();
            var other = await core
                .Query<MeshNode>(AsSystem("namespace:Other scope:children"), Options)
                .FirstAsync()
                .Timeout(TimeSpan.FromSeconds(30))
                .Await(ct);
            clock.Stop();

            other.Items.Select(n => n.Path).OrderBy(p => p, StringComparer.Ordinal)
                .Should().Equal(["Other/O0", "Other/O1", "Other/O2"], "the second query's answer is complete");
            clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2),
                "a scoped query sharing the pool must queue behind at most the walk's bound, never behind "
                + "the whole remaining walk (production: path:Hosting/PlatformBuilds missing the 15 s bound)");
        }
    }
}
