using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Graph.Configuration;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The in-flight register that makes detached activity writes visible to shutdown.
///
/// <para>What is actually being pinned: a write that is still running when the hub disposes must
/// DELAY that disposal rather than be killed by it — and a write that will never finish must not
/// hold shutdown open. Both halves matter; a drain with no timeout trades a lost write for a hung
/// silo, which is worse.</para>
/// </summary>
public class ActivityWriteTrackerTest
{
    /// <summary>An idle hub must not pay for this — the overwhelmingly common shutdown.</summary>
    [Fact]
    public async Task Drain_WithNothingInFlight_CompletesImmediately()
    {
        var tracker = new ActivityWriteTracker();

        var completed = await tracker.Drain().Timeout(TimeSpan.FromSeconds(2)).FirstAsync();

        completed.Should().Be(System.Reactive.Unit.Default);
        tracker.Count.Should().Be(0);
    }

    /// <summary>
    /// 🚨 THE POINT. While a write is outstanding the drain must NOT complete — that is the whole
    /// mechanism by which hub disposal, and therefore the grain's bounded wait, accounts for it.
    /// </summary>
    [Fact]
    public async Task Drain_WaitsWhileAWriteIsInFlight_AndCompletesWhenItEnds()
    {
        var tracker = new ActivityWriteTracker();
        var write = tracker.Begin("alice/_UserActivity/Doc_Page");
        tracker.Count.Should().Be(1);

        var drain = tracker.Drain().FirstAsync().ToTask();

        // Still running: the drain must be pending, not completed.
        var early = await Task.WhenAny(drain, Task.Delay(300));
        early.Should().NotBe(drain, "the drain must wait while a write is still in flight");

        write.Dispose();                        // the write finishes

        await drain.WaitAsync(TimeSpan.FromSeconds(3));
        tracker.Count.Should().Be(0);
    }

    /// <summary>
    /// A write that never ends must NOT hold shutdown open forever. The grain gives hub disposal
    /// 5 s before "moving on", so overrunning here would only rob the other dispose actions of
    /// their share of that window and surface as a disposal hang instead of a lost write.
    /// </summary>
    [Fact]
    public async Task Drain_TimesOutRatherThanBlockingShutdownForever()
    {
        var tracker = new ActivityWriteTracker();
        _ = tracker.Begin("alice/_UserActivity/Stuck");   // never released

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await tracker.Drain()
            .Timeout(ActivityWriteTracker.DrainTimeout + TimeSpan.FromSeconds(5))
            .FirstAsync();
        sw.Stop();

        sw.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(1),
            "it must actually have waited for the write, not returned instantly");
        sw.Elapsed.Should().BeLessThan(ActivityWriteTracker.DrainTimeout + TimeSpan.FromSeconds(3),
            "…and it must give up on its own rather than blocking the silo");
    }

    /// <summary>
    /// The budget must sit UNDER the grain's 5 s hub-disposal window
    /// (<c>MessageHubGrain.OnDeactivateAsync</c>), or the grain cuts the drain off anyway and the
    /// other registered dispose actions lose their share of it.
    /// </summary>
    [Fact]
    public void DrainTimeout_IsUnderTheGrainsDisposalBudget() =>
        ActivityWriteTracker.DrainTimeout.Should().BeLessThan(TimeSpan.FromSeconds(5),
            "the grain gives hub disposal 5 s total before moving on");

    /// <summary>
    /// Concurrent tracks for the SAME path are the documented create-vs-update race. Releasing one
    /// must not report the path drained while the other is still writing.
    /// </summary>
    [Fact]
    public async Task ConcurrentWritesToTheSamePath_BothMustFinishBeforeDraining()
    {
        var tracker = new ActivityWriteTracker();
        var first = tracker.Begin("alice/_UserActivity/Same");
        var second = tracker.Begin("alice/_UserActivity/Same");

        var drain = tracker.Drain().FirstAsync().ToTask();
        first.Dispose();

        var early = await Task.WhenAny(drain, Task.Delay(300));
        early.Should().NotBe(drain,
            "one of two concurrent writes to the same path finished — the other is still running");

        second.Dispose();
        await drain.WaitAsync(TimeSpan.FromSeconds(3));
    }

    /// <summary>
    /// Rx may dispose a subscription more than once. A double release would under-count and let a
    /// drain report clear while a write is still running.
    /// </summary>
    [Fact]
    public async Task ReleasingTwice_IsIdempotent()
    {
        var tracker = new ActivityWriteTracker();
        var a = tracker.Begin("alice/_UserActivity/A");
        var b = tracker.Begin("alice/_UserActivity/B");

        a.Dispose();
        a.Dispose();                               // the double release

        tracker.Count.Should().Be(1, "B is still in flight — the repeat release must not remove it");

        b.Dispose();
        await tracker.Drain().Timeout(TimeSpan.FromSeconds(3)).FirstAsync();
    }

    /// <summary>
    /// A drain overrun must NAME what it abandoned. A lost activity write is otherwise invisible:
    /// the request it belonged to completed successfully long before.
    /// </summary>
    [Fact]
    public void InFlight_NamesThePathsSoAnOverrunIsDiagnosable()
    {
        var tracker = new ActivityWriteTracker();
        _ = tracker.Begin("alice/_UserActivity/Doc_One");
        _ = tracker.Begin("bob/_UserActivity/Doc_Two");

        tracker.InFlight.Should().HaveCount(2);
        tracker.InFlight.Should().Contain("alice/_UserActivity/Doc_One");
        tracker.InFlight.Should().Contain("bob/_UserActivity/Doc_Two");
    }

    /// <summary>An empty or whitespace path is a caller bug, not something to silently register.</summary>
    [Fact]
    public void Begin_RejectsAnEmptyPath()
    {
        var tracker = new ActivityWriteTracker();
        Assert.Throws<ArgumentException>(() => tracker.Begin("  "));
    }
}
