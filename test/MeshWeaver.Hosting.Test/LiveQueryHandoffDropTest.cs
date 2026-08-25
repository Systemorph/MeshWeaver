using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

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
        public readonly ManualResetEventSlim ReadEntered = new(false);
        public readonly ManualResetEventSlim ReleaseRead = new(true);
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
                ReadEntered.Set();
                if (!ReleaseRead.Wait(TimeSpan.FromSeconds(10)))
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

    [Fact]
    public void A_notification_snapshotted_before_Initial_and_delivered_after_still_reaches_the_query()
    {
        var inner = new InMemoryStorageAdapter();
        var feed = new SnapshotThenDeliverFeed();
        var adapter = new GatedAdapter(inner, feed);
        var provider = new StorageAdapterMeshQueryProvider(adapter);

        var changes = new List<QueryResultChange<MeshNode>>();
        var initial = new ManualResetEventSlim(false);
        var added = new ManualResetEventSlim(false);

        // Hold the query's own scope walk open so the Initial snapshot cannot be established
        // until we say so.
        adapter.GateOn = Base;
        adapter.ReleaseRead.Reset();

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
                    if (c.ChangeType == QueryChangeType.Initial) initial.Set();
                    else if (c.Items.Any(n => string.Equals(n.Path, ChildPath, StringComparison.OrdinalIgnoreCase)))
                        added.Set();
                }))
        { IsBackground = true };
        subscriber.Start();

        try
        {
            // The query's ONE feed subscription is attached and its scope walk is parked.
            Assert.True(adapter.ReadEntered.Wait(TimeSpan.FromSeconds(10)),
                "the query never started its scope walk");

            // The write COMMITS to the store (so any re-query can see it) …
            inner.Write(new MeshNode("some_model", Base) { NodeType = "TokenUsage" }, Options)
                .Subscribe();

            // … and its notification's fan-out snapshot is taken HERE — before the Initial is
            // established, so it holds only what the query has attached so far.
            var deliver = feed.PublishDeferred(DataChangeNotification.Updated(ChildPath, null));

            // Now let the Initial land. Its rows were read before the write, so it is EMPTY — the
            // query's only route to the row is the notification whose snapshot we already hold.
            adapter.ReleaseRead.Set();
            Assert.True(initial.Wait(TimeSpan.FromSeconds(10)), "the query never emitted its Initial");

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

            Assert.True(added.Wait(TimeSpan.FromSeconds(10)),
                "a change notification whose fan-out snapshot predates the Initial, delivered after "
                + "it, must still reach the live re-query. Dropping it loses the row PERMANENTLY: "
                + "nothing re-triggers the query, so the subscription never learns of a node a "
                + "point read returns immediately.");
        }
        finally
        {
            adapter.ReleaseRead.Set();
            subscriber.Join(TimeSpan.FromSeconds(10));
            sub?.Dispose();
        }
    }
}
