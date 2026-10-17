using System;
using System.Diagnostics;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using MeshWeaver.Hosting;
using Microsoft.Reactive.Testing;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 A re-open must not NEST. <see cref="StandingWatchRecovery.ReopenOnTransientFault{T}"/> used to
/// re-open from inside its own <c>Catch</c> (<c>Timer.SelectMany(_ =&gt; Subscription(n))</c>), so
/// every fault a long-lived watch survived wrapped one more operator layer around the next opening.
/// The chain — and the stack every later emission climbs to reach the subscriber — grew with the
/// number of faults over the hub's life and was never released after the source recovered.
///
/// <para>Measured as the stack depth at which an emission reaches the subscriber after N transient
/// faults, on a <see cref="TestScheduler"/>: it must be the SAME after 1000 faults as after one.
/// <see cref="TheNestingShape_GrowsWithEveryFault"/> is the negative control — the previous
/// implementation, driven by the same source and clock, grows.</para>
/// </summary>
public class StandingWatchRecoveryDoesNotNestTest
{
    private static int DepthAfterFaults(
        int faults,
        Func<Func<IObservable<int>>, IScheduler, IObservable<int>> reopen)
    {
        var scheduler = new TestScheduler();
        var opens = 0;
        var depth = -1;
        Exception? terminal = null;
        IObservable<int> Open() => ++opens <= faults
            ? Observable.Throw<int>(new TimeoutException("transient"))
            : Observable.Return(42);

        using var _ = reopen(Open, scheduler)
            .Subscribe(_ => depth = new StackTrace().FrameCount, ex => terminal = ex);
        scheduler.AdvanceBy(TimeSpan.FromSeconds(faults + 1).Ticks);

        terminal.Should().BeNull("every fault is transient and the budget is unbounded");
        opens.Should().Be(faults + 1, "each fault re-opens exactly once");
        depth.Should().BePositive("the opening after the last fault emits");
        return depth;
    }

    private static IObservable<int> Current(Func<IObservable<int>> open, IScheduler scheduler) =>
        StandingWatchRecovery.ReopenOnTransientFault(
            open, int.MaxValue, _ => TimeSpan.FromSeconds(1), scheduler, isTransient: _ => true);

    [Fact]
    public void AThousandConsecutiveFaults_LeaveTheSubscriptionDepthUnchanged()
    {
        var afterOne = DepthAfterFaults(1, Current);
        var afterThousand = DepthAfterFaults(1000, Current);

        afterThousand.Should().Be(afterOne,
            "a re-open replaces the failed opening in place; it never wraps another layer around it");
    }

    [Fact]
    public void TheBudgetStillResetsOnAnEmission_AndANonTransientFaultStillEnds()
    {
        var scheduler = new TestScheduler();
        var opens = 0;
        var reopens = new System.Collections.Generic.List<int>();
        Exception? terminal = null;
        // Faults 1-2, then an opening that EMITS before faulting, then faults until the budget (2) is spent.
        IObservable<int> Open() => ++opens switch
        {
            3 => Observable.Return(1).Concat(Observable.Throw<int>(new TimeoutException("after emission"))),
            _ => Observable.Throw<int>(new TimeoutException("transient")),
        };

        using var _ = StandingWatchRecovery.ReopenOnTransientFault(
                Open, 2, n => TimeSpan.FromSeconds(n), scheduler,
                onReopen: (_, n, _) => reopens.Add(n), isTransient: ex => ex is TimeoutException)
            .Subscribe(_ => { }, ex => terminal = ex);
        scheduler.AdvanceBy(TimeSpan.FromMinutes(1).Ticks);

        reopens.Should().Equal([1, 2, 1, 2], "the emission in the third opening restarts the count");
        terminal.Should().BeOfType<TimeoutException>("the spent budget surfaces the last fault unchanged");

        Exception? defect = null;
        var defectOpens = 0;
        using var __ = StandingWatchRecovery.ReopenOnTransientFault(
                () => { defectOpens++; return Observable.Throw<int>(new InvalidOperationException("defect")); },
                5, StandingWatchRecovery.DefaultBackoff, scheduler, isTransient: ex => ex is TimeoutException)
            .Subscribe(_ => { }, ex => defect = ex);
        scheduler.AdvanceBy(TimeSpan.FromMinutes(5).Ticks);
        defect.Should().BeOfType<InvalidOperationException>();
        defectOpens.Should().Be(1, "a non-transient fault ends the watch on its first occurrence");
    }

    /// <summary>The previous implementation, verbatim in shape: the re-open nested inside the Catch.</summary>
    private static IObservable<int> Nesting(Func<IObservable<int>> open, IScheduler scheduler)
    {
        IObservable<int> Subscription(int consecutiveSoFar) => Observable.Defer(() =>
        {
            var emitted = false;
            return Observable.Defer(open)
                .Do(_ => emitted = true)
                .Catch((Exception _) =>
                {
                    var consecutive = (emitted ? 0 : consecutiveSoFar) + 1;
                    return Observable.Timer(TimeSpan.FromSeconds(1), scheduler).SelectMany(_ => Subscription(consecutive));
                });
        });
        return Subscription(0);
    }

    [Fact]
    public void TheNestingShape_GrowsWithEveryFault()
    {
        var afterOne = DepthAfterFaults(1, Nesting);
        var afterTwoHundred = DepthAfterFaults(200, Nesting);

        afterTwoHundred.Should().BeGreaterThan(afterOne + 199,
            "each re-open of the old shape wraps the next opening in one more layer");
    }
}
