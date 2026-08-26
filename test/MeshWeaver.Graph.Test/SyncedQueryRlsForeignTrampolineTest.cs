using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Threading;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Issue #2087 — the per-user RLS filter on a synced query is the residual of #2377's class that
/// #2389's sweep did not reach (it ratcheted <c>src/MeshWeaver.Hosting/Persistence</c> and the
/// storage adapters; this filter lives in <c>MeshWeaver.Graph</c>).
///
/// <para><b>The defect.</b> <c>WrapWithPerUserRls</c> re-emitted each upstream snapshot through the
/// parameterless <c>IEnumerable.ToObservable()</c>. Rx schedules that on
/// <c>SchedulerDefaults.Iteration</c> = <see cref="CurrentThreadScheduler"/>, which does NOT mean
/// "run it here now": it keeps a <c>[ThreadStatic]</c> flag for "a trampoline is already running on
/// this thread", and while that flag is set <c>Schedule</c> only <b>enqueues</b>. The filter runs on
/// the DELIVERY thread of an upstream emission — in a live portal routinely inside the hub pump's own
/// trampoline, or inside the one an await-resumed continuation inherits — so the per-node iteration
/// was handed to a queue nobody was going to drain, <c>.ToList()</c> never completed, and the
/// snapshot was dropped with no error and no completion. The user's listing stays exactly as it was:
/// empty, for a first snapshot, for as long as the view is open.</para>
///
/// <para><b>Why the empty case matters as much as the populated one.</b> Even a zero-element
/// sequence has to schedule its <c>OnCompleted</c>, so a legitimately empty snapshot stranded too —
/// which is the difference between "this listing is empty" and "this listing never answered", and
/// the two are indistinguishable on screen.</para>
///
/// <para>The cure is <c>ToInlineObservable()</c> (<c>ImmediateScheduler</c>, no ambient per-thread
/// state). This test subscribes from inside a real trampoline and blocks there — the shape that used
/// to strand — so it fails on the parameterless overload and passes on the inline one.</para>
/// </summary>
public class SyncedQueryRlsForeignTrampolineTest
{
    /// <summary>
    /// The filter must deliver its filtered snapshot when the subscribing frame is already inside a
    /// foreign Rx trampoline. The empty snapshot is not padding: it strands on exactly the same
    /// mechanism, and it is the case a fresh listing hits first.
    /// </summary>
    /// <param name="nodeCount">How many nodes the upstream snapshot carries.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    public void Filtered_snapshot_arrives_when_subscribed_inside_a_foreign_trampoline(int nodeCount)
        // 🚨 A DEDICATED thread, not the test thread — the xUnit thread can already carry a
        // trampoline (that is the very leak this class of defect is about), and opening one from
        // there would silently skip the whole body. See FreshThread.
        => FreshThread.Run(
            () => RunProbe(nodeCount, granted: true, expected: nodeCount),
            $"the probe thread never finished for {nodeCount} node(s) — the RLS filter's per-node "
            + "iteration was queued on the caller's trampoline instead of running inline, so it can "
            + "never run (#2087)");

    /// <summary>
    /// The same contract when the probe DENIES every node: an all-filtered-out snapshot must still
    /// be delivered as an empty list. Dropping it instead is what makes a permission change look
    /// like a hung view rather than an empty one.
    /// </summary>
    [Fact]
    public void A_fully_denied_snapshot_is_delivered_as_empty_not_dropped()
        => FreshThread.Run(
            () => RunProbe(nodeCount: 3, granted: false, expected: 0),
            "the probe thread never finished — a fully denied snapshot was dropped instead of "
            + "delivered as an empty list (#2087)");

    private static void RunProbe(int nodeCount, bool granted, int expected)
    {
        var snapshot = Enumerable.Range(0, nodeCount)
            .Select(i => new MeshNode($"n{i}", "probe"))
            .ToArray();

        // Observable.Return uses ImmediateScheduler, so the snapshot is delivered during Subscribe,
        // on the subscribing thread — i.e. inside the trampoline opened below. That is the whole
        // point: it reproduces the delivery thread a live upstream emission actually arrives on.
        var upstream = Observable.Return<IEnumerable<MeshNode>>(snapshot);

        using var arrived = new ManualResetEventSlim(false);
        IEnumerable<MeshNode>? filtered = null;
        var insideTrampoline = false;

        // Schedule() IS the trampoline: inside this action CurrentThreadScheduler reports that one
        // is already running, which is precisely the state an await-resumed continuation inherits.
        CurrentThreadScheduler.Instance.Schedule(() =>
        {
            insideTrampoline = !CurrentThreadScheduler.IsScheduleRequired;
            using var sub = SyncedQueryDataSourceExtensions
                .FilterByReadPermission(upstream, _ => Observable.Return(granted))
                .Subscribe(result => { filtered = result; arrived.Set(); });

            // 🚨 Blocking HERE is the point, not a shortcut: the stranded iteration could only ever
            // run after this frame returns to the trampoline that owns it, so a caller that waits
            // for its own snapshot is the shape that deadlocked. A budget generous enough that only
            // a never-scheduled iteration can exhaust it — this work is microseconds.
            Assert.True(arrived.Wait(TimeSpan.FromSeconds(5)),
                $"the filtered snapshot never arrived for {nodeCount} node(s) — the per-node "
                + "iteration was queued on the caller's trampoline instead of running inline, so it "
                + "can never run (#2087)");
        });

        Assert.True(insideTrampoline,
            "the probe never actually ran inside a trampoline — it would pass without testing anything");
        Assert.NotNull(filtered);
        Assert.Equal(expected, filtered!.Count());
    }
}
