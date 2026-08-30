using System;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Fixture;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Issue #2377 — a live query's <c>Initial</c> snapshot must be produced <b>during
/// <c>Subscribe</c>, on the subscribing thread</b>, even when the subscribing frame is already
/// inside somebody else's Rx <see cref="CurrentThreadScheduler"/> trampoline.
///
/// <para><b>The defect.</b> The pedestrian scope walk emitted its path lists with the parameterless
/// <c>IEnumerable.ToObservable()</c>, which Rx schedules on <c>SchedulerDefaults.Iteration</c> —
/// <see cref="CurrentThreadScheduler"/>. That scheduler does not mean "run it now": it keeps a
/// <c>[ThreadStatic]</c> flag for "a trampoline is already running on this thread", and while that
/// flag is set <c>Schedule</c> only <b>enqueues</b>, leaving the item for whoever owns the outer
/// trampoline to drain. So a query subscribed from inside a foreign trampoline never walked at all:
/// <c>Subscribe</c> returned with the walk sitting in someone else's queue, and if the caller then
/// blocked waiting for its first result, the two waited on each other. <b>No error, no completion,
/// no row — forever.</b> In the portal that is a live children listing (chat token chip,
/// notification bell, folder view) that silently stays empty.</para>
///
/// <para><b>Why an ordinary caller lands inside a foreign trampoline.</b> Rx runs every operator
/// subscription through that trampoline, and a <c>Task</c> completed from inside an Rx pipeline
/// resumes its awaiter <i>inline on that thread</i> — so anything after an
/// <c>await …FirstAsync().ToTask()</c> can be running inside one. Under xUnit that even leaks across
/// tests, which is what made <c>LiveQueryHandoffDropTest</c> fail ~23% of cold whole-assembly runs on
/// a 4-CPU Linux runner (its 30 s warm-up wait was the block) while every warm or single-test run
/// passed.</para>
///
/// <para><b>The cure</b> is <c>ToInlineObservable()</c> (<see cref="InlineObservableExtensions"/>) —
/// <see cref="ImmediateScheduler"/>, which carries no ambient per-thread state. This test holds the
/// walk to that contract: it subscribes from inside a real trampoline and blocks there, which is
/// exactly the shape that used to strand.</para>
/// </summary>
public class LiveQueryForeignTrampolineTest
{
    private static readonly JsonSerializerOptions Options = new();

    /// <summary>
    /// Every walk-backed scope must still emit its Initial when subscribed inside a foreign
    /// trampoline. <c>scope:exact</c> is included as the control: it never walked, so it passed
    /// before the fix too.
    /// </summary>
    /// <param name="query">The query shape under test.</param>
    [Theory]
    [InlineData("path:probe/_Usage scope:children")]
    [InlineData("path:probe/_Usage scope:subtree")]
    [InlineData("path:probe/_Usage scope:descendants")]
    [InlineData("path:probe/_Usage/one scope:exact")]
    public void Initial_arrives_when_subscribed_inside_a_foreign_trampoline(string query)
        // 🚨 A DEDICATED thread, not the test thread — see FreshThread for why that is load-bearing
        // here: the xUnit test thread can already carry a trampoline (that is the very leak this
        // test is about), and opening one from there would silently skip the whole body.
        => FreshThread.Run(
            () => RunProbe(query),
            $"the probe thread never finished for [{query}] — the scope walk was queued on the "
            + "caller's trampoline instead of running inline, so it can never run (#2377)");

    private static void RunProbe(string query)
    {
        var adapter = new InMemoryStorageAdapter();
        adapter.Write(new MeshNode("one", "probe/_Usage") { NodeType = "TokenUsage" }, Options)
            .Subscribe();
        var provider = new StorageAdapterMeshQueryProvider(adapter);
        // 🚨 A volatile flag, not a hand-woven gate. Blocking the trampoline frame IS the subject
        // (see below), and a flag polled under a bounded SpinUntil expresses that with no kernel
        // handle to dispose and no observable→blocking bridge.
        var initial = 0;
        var insideTrampoline = false;

        // Schedule() IS the trampoline: inside this action CurrentThreadScheduler reports that one
        // is already running, which is precisely the state an await-resumed continuation inherits.
        CurrentThreadScheduler.Instance.Schedule(() =>
        {
            insideTrampoline = !CurrentThreadScheduler.IsScheduleRequired;
            using var sub = provider
                .Query<MeshNode>(MeshQueryRequest.FromQueries([query], "system-security"), Options)
                .Subscribe(c => { if (c.ChangeType == QueryChangeType.Initial) Volatile.Write(ref initial, 1); });

            // 🚨 Blocking HERE is the point, not a shortcut: the stranded walk could only ever run
            // after this frame returns to the trampoline that owns it, so a caller that waits for
            // its own Initial is the shape that deadlocked. A budget generous enough that only a
            // never-scheduled walk can exhaust it — this work is microseconds.
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref initial) == 1, TimeSpan.FromSeconds(5)),
                $"Initial never arrived for [{query}] — the scope walk was queued on the caller's "
                + "trampoline instead of running inline, so it can never run (#2377)");
        });

        Assert.True(insideTrampoline,
            "the probe never actually ran inside a trampoline — it would pass without testing anything");
    }
}
