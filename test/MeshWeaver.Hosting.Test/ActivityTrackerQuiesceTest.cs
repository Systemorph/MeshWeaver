using System;
using System.Diagnostics;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// The activity quiesce — the phase-0 of mesh teardown — lets a run that is WORKING finish,
/// cancels only a run that has stopped reporting progress, and abandons one that ignores even
/// that. Each verdict is DATA on the report, so a teardown can say what it had to do rather than
/// pretend the run finished. Progress is what the run reports itself (every appended log line);
/// the tracker never invents a heartbeat.
/// </summary>
public class ActivityTrackerQuiesceTest
{
    private static readonly TimeSpan Stall = TimeSpan.FromMilliseconds(400);

    [Fact]
    public async Task AnIdleMesh_QuiescesAtOnce_AndClean()
    {
        using var tracker = new ActivityTracker();
        var sw = Stopwatch.StartNew();
        var report = await tracker.Quiesce(Stall).FirstAsync().Timeout(TimeSpan.FromSeconds(5))
            .Await(TestContext.Current.CancellationToken);
        report.Clean.Should().BeTrue();
        sw.Elapsed.Should().BeLessThan(Stall, "an idle mesh must not wait a stall budget to learn it is idle");
    }

    /// <summary>
    /// A run that keeps reporting progress is waited for — well past one stall budget — and is
    /// never cancelled. This is the contract that lets accepted work finish its job.
    /// </summary>
    [Fact]
    public async Task AProgressingRun_IsWaitedFor_AndNeverCancelled()
    {
        using var tracker = new ActivityTracker();
        var cancelRequested = 0;
        var handle = tracker.TrackRun("progressing", () => Interlocked.Exchange(ref cancelRequested, 1));

        // The run: reports progress every 100 ms for three stall budgets, then finishes.
        var runLength = TimeSpan.FromMilliseconds(Stall.TotalMilliseconds * 3);
        var run = Observable.Interval(TimeSpan.FromMilliseconds(100))
            .TakeUntil(Observable.Timer(runLength))
            .Do(_ => handle.Progress())
            .Finally(handle.Dispose)
            .Subscribe();

        var sw = Stopwatch.StartNew();
        var report = await tracker.Quiesce(Stall).FirstAsync().Timeout(TimeSpan.FromSeconds(15))
            .Await(TestContext.Current.CancellationToken);
        sw.Stop();
        run.Dispose();

        report.Clean.Should().BeTrue(report.ToString());
        Volatile.Read(ref cancelRequested).Should().Be(0, "a run that reports progress is never cancelled");
        sw.Elapsed.Should().BeGreaterThan(Stall + Stall,
            "the quiesce must have waited well past one stall budget for a run that kept working");
    }

    /// <summary>
    /// A run that stops reporting progress is CANCELLED after one stall budget. Here the run
    /// honours the cancel by finishing, so the quiesce completes clean of abandonments but names
    /// the run it had to kill.
    /// </summary>
    [Fact]
    public async Task AStalledRun_IsCancelledAfterOneStallBudget_AndNamed()
    {
        using var tracker = new ActivityTracker();
        ActivityRunHandle? handle = null;
        handle = tracker.TrackRun("stalled", () => handle!.Dispose()); // observes the cancel by finishing

        var sw = Stopwatch.StartNew();
        var report = await tracker.Quiesce(Stall).FirstAsync().Timeout(TimeSpan.FromSeconds(15))
            .Await(TestContext.Current.CancellationToken);
        sw.Stop();

        report.Cancelled.Should().ContainSingle().Which.Should().Be("stalled");
        report.Abandoned.Should().BeEmpty();
        report.Clean.Should().BeFalse("a kill is a finding, not a clean quiesce");
        sw.Elapsed.Should().BeGreaterThanOrEqualTo(Stall,
            "the cancel is a verdict reached after a whole stall budget of no progress, never a reflex");
        handle.CancelRequested.Should().BeTrue();
    }

    /// <summary>
    /// A run that ignores its cancellation is ABANDONED after a second stall budget, so the
    /// quiesce (and the teardown behind it) can proceed — and the report says so by name instead
    /// of pretending the run finished.
    /// </summary>
    [Fact]
    public async Task ARunThatIgnoresCancellation_IsAbandonedAfterASecondBudget()
    {
        using var tracker = new ActivityTracker();
        var cancelRequested = 0;
        var handle = tracker.TrackRun("blind", () => Interlocked.Exchange(ref cancelRequested, 1));
        try
        {
            var sw = Stopwatch.StartNew();
            var report = await tracker.Quiesce(Stall).FirstAsync().Timeout(TimeSpan.FromSeconds(15))
                .Await(TestContext.Current.CancellationToken);
            sw.Stop();

            Volatile.Read(ref cancelRequested).Should().Be(1, "the run was handed its cancellation first");
            report.Cancelled.Should().ContainSingle().Which.Should().Be("blind");
            report.Abandoned.Should().ContainSingle().Which.Should().Be("blind");
            sw.Elapsed.Should().BeGreaterThanOrEqualTo(Stall + Stall,
                "one budget to cancel, another to conclude the run ignores it");
            handle.Abandoned.Should().BeTrue();
        }
        finally
        {
            handle.Dispose();
        }
    }

    /// <summary>The counting surface is unchanged: Track() still counts, WhenIdle still fires.</summary>
    [Fact]
    public async Task Track_StillCounts_AndWhenIdleFiresOnRelease()
    {
        using var tracker = new ActivityTracker();
        var registration = tracker.Track();
        var idle = tracker.WhenIdle.FirstAsync().Timeout(TimeSpan.FromSeconds(5)).Await(TestContext.Current.CancellationToken);
        await Task.Yield();
        idle.IsCompleted.Should().BeFalse("a tracked run holds idle open");
        registration.Dispose();
        await idle;
        tracker.InFlight.Should().BeEmpty();
    }
}
