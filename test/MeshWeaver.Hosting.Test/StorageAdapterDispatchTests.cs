using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Regression tests for the SECOND fan-out point of issue #899:
/// <see cref="IStorageAdapter.Changes"/>. PR #910 moved the mesh change feed off the
/// publisher's thread and flagged this one as the nearest remaining inversion candidate —
/// <c>InMemoryStorageAdapter.Write</c>/<c>Delete</c> pushed into a plain
/// <see cref="System.Reactive.Subjects.Subject{T}"/> INLINE, and
/// <c>StorageAdapterMeshQueryProvider</c> feeds the very same synced-query merges the change
/// feed does.
///
/// <para>The in-memory adapter is the one backend where this is reachable: every other one
/// (Postgres, Sqlite, Snowflake, Cosmos) publishes from inside <c>IIoPool</c>, i.e. already
/// off the calling hub's thread and outside whatever gate the caller holds. The in-memory
/// path is fully synchronous — <c>Observable.Defer</c> straight through — so a writer's
/// thread walked the ENTIRE subscriber graph: <c>PersistenceService.Changes</c>'s shared
/// <c>Merge</c> gate → each synced query's <c>Concat</c> gate → the process-wide
/// <c>Replay(1)</c> in <c>IMeshNodeStreamCache</c> → other hubs' <c>PermissionEvaluator</c>
/// folds → <c>MeshDataSource</c>'s <c>GetMeshNodeStream().Update(...)</c> into a FOREIGN
/// hub.</para>
/// </summary>
public class StorageAdapterDispatchTests
{
    private static readonly JsonSerializerOptions Options = new();

    private static MeshNode Node(string id) => new(id) { NodeType = "X" };

    /// <summary>
    /// The invariant everything else follows from: a write hands the notification to the
    /// feed's dispatch chain and returns — the subscriber runs on a DIFFERENT thread.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Write_runs_change_subscribers_off_the_writer_thread()
    {
        var adapter = new InMemoryStorageAdapter();

        var seen = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = adapter.Changes.Subscribe(
            _ => seen.TrySetResult(Environment.CurrentManagedThreadId));

        var writerThread = Environment.CurrentManagedThreadId;
        adapter.Write(Node("a"), Options).Subscribe();

        var handlerThread = await seen.Task.WaitAsync(TimeSpan.FromSeconds(10));
        handlerThread.Should().NotBe(writerThread,
            "a change subscriber that runs on the writer's thread runs inside whatever locks "
            + "the writing hub already holds — that is the #899 lock-order inversion");
    }

    /// <summary>
    /// Ordering is part of the contract: one FIFO dispatch chain. A feed that fanned out with
    /// unordered thread-pool work items could reorder Updated/Deleted for the same path and
    /// resurrect a deleted node in every downstream synced query.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Change_events_are_delivered_in_write_order()
    {
        var adapter = new InMemoryStorageAdapter();

        const int count = 200;
        var received = new ConcurrentQueue<string>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = adapter.Changes.Subscribe(n =>
        {
            received.Enqueue(n.Path);
            if (received.Count == count)
                done.TrySetResult();
        });

        var expected = new string[count];
        for (var i = 0; i < count; i++)
        {
            expected[i] = $"space/node{i}";
            adapter.Write(Node(expected[i]), Options).Subscribe();
        }

        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        received.ToArray().Should().Equal(expected);
    }

    /// <summary>
    /// The #899 inversion on the storage feed, deterministically — same probe as the mesh
    /// change feed's, pointed at <c>Write</c>. Fails (hangs to the 10 s deadlock bound) on the
    /// pre-fix adapter, which published inline via <c>Subject.OnNext</c>.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Concurrent_writers_holding_their_own_gates_cannot_deadlock()
    {
        var adapter = new InMemoryStorageAdapter();

        await RxFanOutInversionHarness.AssertCannotDeadlock(
            subscribe: handler => adapter.Changes.Subscribe(n => handler(n.Path)),
            publish: tag => adapter.Write(Node(tag), Options).Subscribe(),
            because:
                "a storage write must not run the change-feed subscriber graph on the writer's "
                + "thread — doing so takes the shared synced-query gates while the writer still "
                + "holds its own permission-fold gate, which is the #899 deadlock");
    }

    /// <summary>Same probe over <c>Delete</c> — the operation the wedge was actually observed on.</summary>
    [Fact(Timeout = 30000)]
    public async Task Concurrent_deleters_holding_their_own_gates_cannot_deadlock()
    {
        var adapter = new InMemoryStorageAdapter();
        adapter.Write(Node("p1"), Options).Subscribe();
        adapter.Write(Node("p2"), Options).Subscribe();

        await RxFanOutInversionHarness.AssertCannotDeadlock(
            subscribe: handler => adapter.Changes.Subscribe(n => handler(n.Path)),
            publish: tag => adapter.Delete(tag).Subscribe(),
            because:
                "a recursive delete fans out per-leaf deletes across per-node hubs; each leaf's "
                + "storage delete must not walk the subscriber graph while its hub holds the "
                + "permission fold's gate (#899)");
    }

    /// <summary>
    /// A throwing subscriber must not starve the others. The pre-fix adapter published through
    /// a plain <c>Subject</c> — where the first throwing observer aborts delivery to every
    /// observer subscribed after it — and wrapped the publish in
    /// <c>catch { /* never throw */ }</c>, so the starvation was completely silent (#889: a
    /// permanently stale <c>$security-access</c> fold).
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task A_throwing_change_subscriber_does_not_starve_the_others()
    {
        var adapter = new InMemoryStorageAdapter();

        using var faulty = adapter.Changes.Subscribe(_ => throw new InvalidOperationException("boom"));
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var healthy = adapter.Changes.Subscribe(_ => reached.TrySetResult());

        adapter.Write(Node("a"), Options).Subscribe();

        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
