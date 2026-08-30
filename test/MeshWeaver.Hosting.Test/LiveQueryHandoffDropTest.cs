using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Pins the early→live handoff of a LIVE query subscription: a change notification whose fan-out
/// SNAPSHOT was taken before the Initial snapshot was established, but which is DELIVERED after,
/// must still reach the re-query.
///
/// <para><b>The defect this kills.</b> Every live query used to open TWO subscriptions on the
/// adapter's change feed: an "early" one that buffered notifications into a backlog while
/// <c>!initialDone</c>, and a "live" one attached inside the initial-results callback, after which
/// the early one was disposed. <see cref="IsolatedChangeFeed"/> — correctly — snapshots its observer
/// list BEFORE delivering, because a subscriber may attach or detach mid-publish. So a write
/// published just before the live subscription was attached is delivered against a snapshot holding
/// ONLY the early observer, and by the time that delivery ran the callback had already set
/// <c>initialDone = true</c>. The early handler's <c>if (!initialDone)</c> then threw the
/// notification away; the live observer never saw it because it was not in the snapshot; and
/// nothing re-triggers the re-query. The row stayed invisible to that subscription <b>for its whole
/// life</b> — while a point read, and any FRESH subscription, returned it immediately.</para>
///
/// <para>That is a permanent miss dressed as a timeout, which is why raising a budget could never
/// fix it. It is the long-running <c>ThreadTokenUsageTest</c>/<c>ModelSubstitutionTest</c>
/// <c>WaitForUsage</c> red (#1040, #1812, #2001): the token-usage satellite WAS written, with the
/// right counts, on both the Completed and the Cancelled path — the reader's listing simply never
/// learned of it. In the portal the same shape makes any live children listing (the chat token
/// chip, a notification bell, a folder view) silently miss a node created in that window until
/// something else changes under the same base path.</para>
///
/// <para>The cure is structural: ONE subscription for the query's whole lifetime, whose routing
/// decision — backlog vs live buffer — is made inside the same critical section that publishes the
/// buffer. Then a notification lands in exactly one of the two and can never fall between them.</para>
///
/// <para>🚨 <b>Issue #2319: a SECOND, unrelated timing hazard.</b> After the fix above landed, this
/// test still failed intermittently on CI — every single time on the FIRST test of the whole
/// assembly to exercise this reactive pipeline in a fresh process. An instrumented loop at CI's 4
/// CPUs showed the gated scenario never drops the notification once warm (400 iterations, only ever
/// iteration 0 failed), so <see cref="WarmUpJitAndTypeLoad"/> was added to pay the one-time
/// JIT/type-load cost on throwaway objects before the gated scenario's budgets start. That is real
/// and it stays.</para>
///
/// <para>🚨 <b>Issue #2377: it was NOT the whole story, and the rest was a framework bug.</b> The
/// test kept failing afterwards — now inside the warm-up itself, the phase built to absorb cold JIT,
/// stuck for its full 30 s against an EMPTY store. That is not slowness: a warm-process loop is
/// structurally blind to anything that only manifests cold or via cross-test state, so it had never
/// ruled anything out. Reproduced at ~23% by running the whole assembly cold in a Linux container
/// capped at 4 CPUs, and traced: <c>Subscribe</c> returned having never walked, with no error and no
/// completion, and the Initial never arrived at all.</para>
///
/// <para>Cause: the scope walk emitted its path lists with the parameterless
/// <c>IEnumerable.ToObservable()</c>, which Rx schedules on <c>CurrentThreadScheduler</c> — and that
/// scheduler only ENQUEUES while another trampoline is running on the thread. The captured stack
/// shows whose: the hub's own <c>MessageService.DrainOne</c> pump opened one, ~500 frames down a
/// <c>.ToTask()</c> resolved, and .NET resumed its awaiter INLINE on that stack — so xUnit carried on
/// running tests inside a stranger's trampoline. This test then subscribed there and blocked on its
/// own Initial, and the enqueued walk could not be drained until the blocked frame returned, which it
/// never would. Fixed at the source with <c>ToInlineObservable()</c>
/// (<see cref="InlineObservableExtensions"/>); <c>LiveQueryForeignTrampolineTest</c> pins it
/// deterministically. A red below therefore means exactly what the class doc above says.</para>
/// </summary>
public class LiveQueryHandoffDropTest
{
    private static readonly JsonSerializerOptions Options = new();
    private const string Base = "rbuergi/_Usage";
    private const string ChildPath = "rbuergi/_Usage/some_model";

    /// <summary>
    /// A change feed with the SAME snapshot-then-deliver semantics as <see cref="IsolatedChangeFeed"/>,
    /// but with the two halves separated so a test can interleave work between them. That interleaving
    /// is the whole window: nothing here is artificial — the production feed takes exactly this
    /// snapshot, and any thread scheduled between the snapshot and the delivery reproduces it.
    /// </summary>
    private sealed class SnapshotThenDeliverFeed : IObservable<DataChangeNotification>
    {
        private ImmutableList<IObserver<DataChangeNotification>> observers =
            ImmutableList<IObserver<DataChangeNotification>>.Empty;
        private readonly object gate = new();

        public IDisposable Subscribe(IObserver<DataChangeNotification> observer)
        {
            lock (gate) observers = observers.Add(observer);
            return System.Reactive.Disposables.Disposable.Create(() =>
            {
                lock (gate) observers = observers.Remove(observer);
            });
        }

        /// <summary>Takes the fan-out snapshot NOW; the returned action performs the delivery.</summary>
        public Action PublishDeferred(DataChangeNotification n)
        {
            var snapshot = Volatile.Read(ref observers);
            return () =>
            {
                foreach (var o in snapshot)
                {
                    try { o.OnNext(n); }
                    catch (ObjectDisposedException) { }
                }
            };
        }
    }

    /// <summary>
    /// Delegates every read/write to a real <see cref="InMemoryStorageAdapter"/>, but exposes the
    /// test's own feed and lets the test hold <see cref="ListChildPaths"/> open — so the Initial
    /// snapshot can be pinned to a known moment relative to the notification's fan-out snapshot.
    /// </summary>
    private sealed class GatedAdapter(InMemoryStorageAdapter inner, SnapshotThenDeliverFeed feed)
        : IStorageAdapter
    {
        /// <summary>
        /// 🚨 No hand-woven gate. The walk → test signal is an <see cref="AsyncSubject{T}"/> the
        /// walk completes and the test awaits reactively; the test → walk release is a volatile
        /// flag the parked walk polls under a bounded SpinUntil. Parking the walk IS the subject
        /// here, so the park stays — what goes is the kernel handle and the disposal it needed.
        /// </summary>
        private readonly AsyncSubject<Unit> readEntered = new();
        private int releaseRead = 1;

        /// <summary>Completes once the gated walk has parked.</summary>
        public IObservable<Unit> ReadEntered => readEntered;

        /// <summary>Closes the gate, so the next matching walk parks.</summary>
        public void HoldRead() => Volatile.Write(ref releaseRead, 0);

        /// <summary>Opens the gate; idempotent, so a `finally` can always call it.</summary>
        public void ReleaseRead() => Volatile.Write(ref releaseRead, 1);

        /// <summary>Holds the FIRST walk of the query's own base path only — later reads run free.</summary>
        public string? GateOn;
        private int gateArmed = 1;
        private int gateTimedOut;

        /// <summary>
        /// True when the gate gave up instead of being released. 🚨 The test MUST assert this is
        /// false: a gate that times out lets the scope walk continue on its own, so the Initial is
        /// no longer pinned relative to the notification's fan-out snapshot — the window the test
        /// exists to hold open would never have been held, and the test could pass having verified
        /// nothing.
        /// </summary>
        public bool GateTimedOut => Volatile.Read(ref gateTimedOut) != 0;

        public IObservable<DataChangeNotification> Changes => feed;

        public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)>
            ListChildPaths(string? parentPath)
            // .Do runs AFTER inner has produced the child list, so the (still empty) result is
            // already captured when we park — the write below cannot leak into this Initial.
            => inner.ListChildPaths(parentPath).Do(_ =>
            {
                if (!string.Equals(parentPath, GateOn, StringComparison.OrdinalIgnoreCase)) return;
                if (Interlocked.Exchange(ref gateArmed, 0) != 1) return;
                readEntered.OnNext(Unit.Default);
                readEntered.OnCompleted();
                if (!SpinWait.SpinUntil(() => Volatile.Read(ref releaseRead) == 1, TimeSpan.FromSeconds(10)))
                    Volatile.Write(ref gateTimedOut, 1);
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

    /// <summary>
    /// Pays the FIRST-EVER-call cost of <see cref="StorageAdapterMeshQueryProvider"/>'s reactive
    /// pipeline (JIT of <c>ObserveQueryInternal</c>/<c>CollectMatched</c>/<c>FindMatchingNodes</c>/
    /// <c>ProcessBatch</c> and every generic Rx operator instantiation they touch) OUTSIDE the
    /// gated scenario's timed waits below.
    ///
    /// <para>🚨 This is not a theoretical concern — it is issue #2319, root-caused by running this
    /// exact test repeatedly inside a Linux container capped at 4 CPUs (matching CI's runner):
    /// every observed CI failure was on the FIRST test this pipeline ever ran in a fresh process
    /// (confirmed via an instrumented 400-iteration in-process loop — only ever iteration 0, the
    /// cold call, ever failed; iterations 1..399 in the SAME warm process never did, even under
    /// artificial CPU load). Under contention, JITting this pipeline for the first time can itself
    /// take longer than the per-step budget below — a slow COLD call, not a dropped notification.
    /// Warming it up here — on throwaway objects, fully independent of the gated scenario — moves
    /// that one-time cost out of the window the assertions below measure, so a red there again
    /// means what the class doc says: the row was actually dropped, not merely slow to JIT.</para>
    ///
    /// <para>🚨 It is NOT, however, what made this test fail AFTER the warm-up landed: that was the
    /// framework bug in issue #2377 (a scope walk queued on a foreign Rx trampoline, so the Initial
    /// never arrived at all), fixed in <c>StorageAdapterMeshQueryProvider</c>. Both budgets below
    /// stay at 30 s and both stay FAIL-FAST: falling through silently would run the gated scenario
    /// against a still-cold pipeline and let it misreport its own timeout as a dropped row.</para>
    /// </summary>
    private static async Task WarmUpJitAndTypeLoad()
    {
        var warmInner = new InMemoryStorageAdapter();
        var warmProvider = new StorageAdapterMeshQueryProvider(warmInner);
        // 🚨 AsyncSubjects, not events: nothing to dispose, nothing that can leak a kernel handle,
        // and the waits below are reactive rather than parked on the calling thread. (This helper
        // is also exercised repeatedly by ad-hoc repro loops, which is what made the undisposed
        // ManualResetEventSlim handle visible in the first place.)
        var warmInitial = new AsyncSubject<Unit>();
        var warmLive = new AsyncSubject<Unit>();
        using (warmProvider
                   .Query<MeshNode>(
                       MeshQueryRequest.FromQueries(["path:warmup/_Usage scope:children"], "system-security"),
                       Options)
                   .Subscribe(c =>
                   {
                       if (c.ChangeType == QueryChangeType.Initial)
                       {
                           warmInitial.OnNext(Unit.Default);
                           warmInitial.OnCompleted();
                       }
                       else
                       {
                           warmLive.OnNext(Unit.Default);
                           warmLive.OnCompleted();
                       }
                   }))
        {
            // Generous, uncontested-by-design budget: this phase primes the JIT, it does not pin
            // the handoff — a slow warm-up is not the defect under test, so it is allowed to be
            // slow. But it must FAIL FAST and LOUD if it doesn't complete: silently falling through
            // would run the gated scenario below against a STILL-cold pipeline and could then
            // misreport its own timeout as "dropped row" — exactly the misleading signal this
            // warm-up exists to prevent.
            await warmInitial.Should().Within(30.Seconds()).Emit(
                "warm-up: Initial never arrived — the environment is too degraded to even JIT-prime "
                + "this pipeline, so the gated assertions below cannot be trusted either");
            warmInner.Write(new MeshNode("x", "warmup/_Usage") { NodeType = "TokenUsage" }, Options)
                .Subscribe();
            await warmLive.Should().Within(30.Seconds()).Emit(
                "warm-up: live update never arrived — the environment is too degraded to even "
                + "JIT-prime this pipeline, so the gated assertions below cannot be trusted either");
        }
    }

    [Fact]
    public async Task A_notification_snapshotted_before_Initial_and_delivered_after_still_reaches_the_query()
    {
        await WarmUpJitAndTypeLoad();

        var inner = new InMemoryStorageAdapter();
        var feed = new SnapshotThenDeliverFeed();
        var adapter = new GatedAdapter(inner, feed);
        var provider = new StorageAdapterMeshQueryProvider(adapter);

        var changes = new List<QueryResultChange<MeshNode>>();
        // 🚨 Signals the subscriber thread produces and this test consumes — AsyncSubjects the
        // producer completes, awaited through the assertion helpers; no hand-woven gate.
        var initial = new AsyncSubject<Unit>();
        var added = new AsyncSubject<Unit>();

        // Hold the query's own scope walk open so the Initial snapshot cannot be established
        // until we say so.
        adapter.GateOn = Base;
        adapter.HoldRead();

        // 🚨 Subscribe OFF the test thread. The provider's scope walk runs inline on the
        // subscribing thread, so parking it from the test thread would park the test itself.
        IDisposable? sub = null;
        var subscriber = new Thread(() =>
            sub = provider
                .Query<MeshNode>(
                    MeshQueryRequest.FromQueries([$"path:{Base} scope:children"], "system-security"),
                    Options)
                .Subscribe(c =>
                {
                    lock (changes) changes.Add(c);
                    if (c.ChangeType == QueryChangeType.Initial)
                    {
                        initial.OnNext(Unit.Default);
                        initial.OnCompleted();
                    }
                    else if (c.Items.Any(n => string.Equals(n.Path, ChildPath, StringComparison.OrdinalIgnoreCase)))
                    {
                        added.OnNext(Unit.Default);
                        added.OnCompleted();
                    }
                }))
        { IsBackground = true };
        subscriber.Start();

        try
        {
            // The query's ONE feed subscription is attached and its scope walk is parked.
            await adapter.ReadEntered.Should().Within(10.Seconds()).Emit(
                "the query never started its scope walk");

            // The write COMMITS to the store (so any re-query can see it) …
            inner.Write(new MeshNode("some_model", Base) { NodeType = "TokenUsage" }, Options)
                .Subscribe();

            // … and its notification's fan-out snapshot is taken HERE — before the Initial is
            // established, so it holds only what the query has attached so far.
            var deliver = feed.PublishDeferred(DataChangeNotification.Updated(ChildPath, null));

            // Now let the Initial land. Its rows were read before the write, so it is EMPTY — the
            // query's only route to the row is the notification whose snapshot we already hold.
            adapter.ReleaseRead();
            await initial.Should().Within(10.Seconds()).Emit("the query never emitted its Initial");

            // 🚨 The gate must have been RELEASED, not given up on. A timed-out gate lets the walk
            // continue by itself, which un-pins the Initial from the notification's snapshot — the
            // test would then pass without ever holding the window it exists to hold.
            Assert.False(adapter.GateTimedOut,
                "the scope-walk gate timed out instead of being released — the Initial was no longer "
                + "pinned relative to the notification's fan-out snapshot, so this run verified nothing");

            // The setup is only meaningful if the Initial genuinely predates the write — otherwise
            // the row would arrive through the snapshot and this test would prove nothing.
            QueryResultChange<MeshNode> initialChange;
            lock (changes) initialChange = changes.First(c => c.ChangeType == QueryChangeType.Initial);
            Assert.Empty(initialChange.Items);

            // Deliver against the pre-Initial snapshot. This is the exact window the old
            // two-subscription handoff dropped on the floor.
            deliver();

            await added.Should().Within(10.Seconds()).Emit(
                "a change notification whose fan-out snapshot predates the Initial, delivered after "
                + "it, must still reach the live re-query. Dropping it loses the row PERMANENTLY: "
                + "nothing re-triggers the query, so the subscription never learns of a node a "
                + "point read returns immediately.");
        }
        finally
        {
            adapter.ReleaseRead();
            subscriber.Join(TimeSpan.FromSeconds(10));
            sub?.Dispose();
        }
    }
}
