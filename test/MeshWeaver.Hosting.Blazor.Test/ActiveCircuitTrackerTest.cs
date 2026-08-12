using MeshWeaver.Hosting;
using Xunit;

namespace MeshWeaver.Hosting.Blazor.Test;

/// <summary>
/// The pod's "may I stop?" counter. It decides whether a rolled-out pod waits for its users or cuts
/// them off, so the two failure directions are not symmetric: over-counting delays a pod's exit
/// (bounded by <c>terminationGracePeriodSeconds</c>), while UNDER-counting hands back the abrupt
/// kill this exists to prevent.
/// </summary>
public class ActiveCircuitTrackerTest
{
    [Fact]
    public void FreshTracker_IsDrained()
    {
        var tracker = new ActiveCircuitTracker();

        tracker.Count.Should().Be(0);
        tracker.Drained.Should().BeTrue("a pod nobody is connected to may stop immediately");
    }

    [Fact]
    public void OpenAndClose_MoveTheCount()
    {
        var tracker = new ActiveCircuitTracker();

        tracker.Opened();
        tracker.Opened();
        tracker.Count.Should().Be(2);
        tracker.Drained.Should().BeFalse("two people are working here");

        tracker.Closed();
        tracker.Drained.Should().BeFalse("one is still working");

        tracker.Closed();
        tracker.Drained.Should().BeTrue("the last circuit closed — now it may stop");
    }

    /// <summary>
    /// Blazor can report a circuit closed after a connection-down already ended it. Unclamped, the
    /// second close would push the count to -1, and -1 != 0 reads as "still busy" — the pod would
    /// then hang until the grace ceiling on EVERY roll. Worse in the other direction: a stray close
    /// before an open would make a busy pod read as drained and stop on top of live sessions.
    /// </summary>
    [Fact]
    public void DoubleClose_CannotDriveTheCountNegative()
    {
        var tracker = new ActiveCircuitTracker();

        tracker.Opened();
        tracker.Closed();
        tracker.Closed();
        tracker.Closed();

        tracker.Count.Should().Be(0);
        tracker.Drained.Should().BeTrue();

        // …and the counter still works afterwards: a clamp that leaked would make the NEXT circuit
        // invisible, so the pod would stop while someone was on it.
        tracker.Opened();
        tracker.Drained.Should().BeFalse("a new circuit after a double-close still counts");
    }

    /// <summary>Circuits open and close on many threads; the count is the shutdown decision.</summary>
    [Fact]
    public void ConcurrentOpensAndCloses_SettleExactly()
    {
        var tracker = new ActiveCircuitTracker();
        const int perThread = 1_000;

        Parallel.For(0, 8, _ =>
        {
            for (var i = 0; i < perThread; i++) tracker.Opened();
        });
        tracker.Count.Should().Be(8 * perThread);

        Parallel.For(0, 8, _ =>
        {
            for (var i = 0; i < perThread; i++) tracker.Closed();
        });
        tracker.Count.Should().Be(0, "every open was matched — no lost update in either direction");
        tracker.Drained.Should().BeTrue();
    }
}
