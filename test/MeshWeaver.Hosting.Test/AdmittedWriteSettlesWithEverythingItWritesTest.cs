using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// #6719 review: what "an admitted write has SETTLED" must mean for the subtree-deletion guard, pinned
/// on the production decorators directly (no mesh) so each ordering is produced by construction:
/// <list type="bullet">
/// <item>the caller has received the write's result BEFORE a waiting delete is released;</item>
/// <item>a secondary write the decorators below start under the same path (the monotonic guard's
/// write-conflict trace) settles WITH the write that caused it;</item>
/// <item>a batch writes exactly the nodes whose paths were admitted.</item>
/// </list>
/// </summary>
public class AdmittedWriteSettlesWithEverythingItWritesTest
{
    private static readonly JsonSerializerOptions Options = new();

    /// <summary>
    /// A write that completes SYNCHRONOUSLY must still reach its caller before its registration is
    /// released: a delete waiting on that registration resumes right there, and would otherwise remove
    /// the row and publish <c>Deleted</c> before the caller published the write's <c>Created</c>.
    /// Negative control: connecting the replay before attaching the caller (the previous shape)
    /// records <c>settled</c> first.
    /// </summary>
    [Fact]
    public void A_synchronously_completing_write_reaches_its_caller_before_a_waiting_delete_is_released()
    {
        var registry = new RecentlyDeletedRegistry();
        var store = new ParkedWriteStorageAdapter(new InMemoryStorageAdapter());
        using var guard = new SubtreeDeletionGuardStorageAdapter(store, registry);

        var order = ImmutableList<string>.Empty;
        IDisposable? waiter = null;
        // At the store the write is admitted and still pending: a delete starts waiting for it here.
        store.OnWrite = _ => waiter ??= registry.AdmittedWritesSettled("ordering")
            .Subscribe(_ => order = order.Add("delete released"));

        using (guard.Write(new MeshNode("n", "ordering") { Name = "x", Version = 1 }, Options)
                   .Subscribe(_ => order = order.Add("caller received the write")))
        {
            order.Should().Equal(
                ["caller received the write", "delete released"],
                "the caller publishes the write's change before a delete waiting on it may run");
        }
        waiter?.Dispose();
    }

    /// <summary>
    /// The monotonic guard records a resolved conflict as a satellite under the conflicted path
    /// (<c>{path}/_Activity/write-conflict-{version}</c>), below the subtree-deletion guard. It used to
    /// be fire-and-forget: the parent write settled, a recursive delete of the path ran, and the trace
    /// committed after it — a descendant left under a subtree reported removed. Parking the trace at
    /// the store shows the parent write now stays unsettled until the trace terminates.
    /// Negative control: subscribing the trace independently (the previous shape) settles while it is
    /// parked.
    /// </summary>
    [Fact]
    public void A_write_conflict_trace_settles_with_the_write_that_caused_it()
    {
        var registry = new RecentlyDeletedRegistry();
        var store = new ParkedWriteStorageAdapter(new InMemoryStorageAdapter());
        using var guard = new SubtreeDeletionGuardStorageAdapter(new MonotonicWriteGuardStorageAdapter(store), registry);
        const string root = "conflict";
        const string path = root + "/node";

        MeshNode? durable = null;
        using (guard.Write(new MeshNode("node", root) { Name = "newer", Version = 5 }, Options)
                   .Subscribe(n => durable = n))
            durable.Should().NotBeNull("the durable row the stale write will conflict with");

        var tracePath = $"{path}/_Activity/write-conflict-{durable!.Version}";
        store.ParkWritesTo(tracePath);
        try
        {
            var staleResult = ImmutableList<MeshNode?>.Empty;
            using var stale = guard.Write(new MeshNode("node", root) { Name = "stale", Version = 3 }, Options)
                .Subscribe(n => staleResult = staleResult.Add(n));

            var traceParked = false;
            using (store.WriteParked.Subscribe(_ => traceParked = true))
                traceParked.Should().BeTrue("the stale write was resolved and its trace reached the store");

            var settled = false;
            using var wait = registry.AdmittedWritesSettled(root).Subscribe(_ => settled = true);
            settled.Should().BeFalse(
                "the conflict trace is a write under the subtree and is still in flight — a delete must not run yet");
            registry.AdmittedWritesUnder(root).Should().Contain(path);

            store.Release();
            settled.Should().BeTrue("the trace terminated, so the write that caused it settled");
            staleResult.Should().ContainSingle("the resolution still answers the caller once");

            MeshNode? trace = null;
            using (store.Read(tracePath, Options).Subscribe(n => trace = n))
                trace.Should().NotBeNull("the trace committed before the write settled");
        }
        finally
        {
            store.Release();
        }
    }

    /// <summary>
    /// A batch is snapshotted once: the paths the guard registers and tests are the very nodes the
    /// store writes, even when the caller's array is changed between the call and the subscription.
    /// </summary>
    [Fact]
    public void A_batch_writes_exactly_the_nodes_whose_paths_were_admitted()
    {
        var registry = new RecentlyDeletedRegistry();
        var store = new ParkedWriteStorageAdapter(new InMemoryStorageAdapter());
        using var guard = new SubtreeDeletionGuardStorageAdapter(store, registry);
        using var deleting = registry.BeginSubtreeDeletion("batch/deleting");

        var nodes = new[] { new MeshNode("admitted", "batch") { Name = "a", Version = 1 } };
        var write = guard.WriteMany(nodes, Options);
        // Swapped AFTER the call: a guard that re-reads the array at Subscribe would write a node
        // under the subtree being deleted, having admitted a different path.
        nodes[0] = new MeshNode("child", "batch/deleting") { Name = "b", Version = 1 };

        var written = ImmutableList<string>.Empty;
        Exception? error = null;
        using (write.Subscribe(list => written = written.AddRange(list.Select(n => n.Path)), ex => error = ex))
        {
            error.Should().BeNull();
            written.Should().Equal("batch/admitted");
        }

        var stray = false;
        using (store.Exists("batch/deleting/child").Subscribe(e => stray = e))
            stray.Should().BeFalse("nothing may be written under a subtree whose deletion is in flight");
    }
}
