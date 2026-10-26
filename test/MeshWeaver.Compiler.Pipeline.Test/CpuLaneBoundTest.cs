using System;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh.Threading;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// Each expiry of <see cref="CpuLaneBound"/>, in isolation, against a real one-slot <see cref="IoPool"/>:
/// <list type="bullet">
/// <item>a prefix that never reaches the lane times out at the bound;</item>
/// <item>an admitted emit past the bound times out, its token is cancelled, and the lane goes on;</item>
/// <item>a compile queued behind a lane whose only leaf ignores cancellation fails, naming the stall,
/// instead of parking at Compiling for ever;</item>
/// <item>a compile queued behind a DRAINING lane for longer than the bound is not timed out.</item>
/// </list>
/// Parked leaves wait on a volatile flag under a bounded <see cref="SpinWait.SpinUntil(Func{bool}, TimeSpan)"/>
/// and are released in a <c>finally</c>, so a failing assertion cannot strand the pool.
/// </summary>
public class CpuLaneBoundTest
{
    private static readonly TimeSpan Bound = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(20);

    private sealed class Flag
    {
        public volatile int Set;
    }

    // The production shape: mark QUEUED, then run on the lane, mark ADMITTED, and honour both the
    // bound's token and the lane's.
    private static IObservable<int> OnLane(
        IoPool lane, CpuLaneWait wait, CancellationToken boundToken, Func<CancellationToken, int> emit)
        => Observable.Defer(() =>
        {
            wait.MarkQueued();
            return lane.InvokeBlocking(laneToken =>
            {
                wait.MarkAdmitted();
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(boundToken, laneToken);
                return emit(linked.Token);
            });
        });

    private static IObservable<int> Bounded(
        Func<CancellationToken, CpuLaneWait, IObservable<int>> leg, IoPool lane)
        => CpuLaneBound.Bound(leg, Bound, "roslyn-compile", "type/Probe", () => lane.AdmissionsCompleted);

    private static IObservable<Exception> Fault(IObservable<int> source)
        => source.Materialize().Where(n => n.Kind != System.Reactive.NotificationKind.OnNext)
            .Take(1)
            .Select(n => n.Exception ?? new InvalidOperationException("completed without a fault"));

    [Fact(Timeout = 60_000)]
    public async Task APrefixThatNeverReachesTheLane_TimesOutAtTheBound_AndIsCancelled()
    {
        using var lane = new IoPool(1);
        var cancelled = new Flag();
        var fault = await Fault(Bounded((ct, _) =>
            {
                ct.Register(() => cancelled.Set = 1);
                return Observable.Never<int>();
            }, lane))
            .Should().Within(Generous)
            .Emit("a prefix that never queues must time out", cancellationToken: TestContext.Current.CancellationToken);

        fault.Should().BeOfType<TimeoutException>().Which.Message.Should()
            .Contain("roslyn-compile").And.Contain("IO prefix did not reach the CPU lane");
        cancelled.Set.Should().Be(1, "the bound must cancel the token the prefix was started with");
    }

    [Fact(Timeout = 60_000)]
    public async Task AnAdmittedEmitPastTheBound_TimesOut_IsCancelled_AndTheLaneGoesOn()
    {
        using var lane = new IoPool(1);
        var sawCancellation = new Flag();
        var fault = await Fault(Bounded((ct, wait) => OnLane(lane, wait, ct, token =>
            {
                // A cooperative emit: it runs until it is told to stop.
                if (SpinWait.SpinUntil(() => token.IsCancellationRequested, Generous))
                    sawCancellation.Set = 1;
                return 0;
            }), lane))
            .Should().Within(Generous)
            .Emit("an admitted emit past the bound must time out", cancellationToken: TestContext.Current.CancellationToken);

        fault.Should().BeOfType<TimeoutException>().Which.Message.Should()
            .Contain("emit did not complete").And.Contain("admitted to the CPU lane");

        var next = await lane.InvokeBlocking(_ => 42)
            .Should().Within(Generous)
            .Emit("the cancelled emit must free the lane's only slot", cancellationToken: TestContext.Current.CancellationToken);
        next.Should().Be(42);
        sawCancellation.Set.Should().Be(1, "the emit must have been cancelled, not left running");
    }

    [Fact(Timeout = 60_000)]
    public async Task ACompileQueuedBehindAStalledLane_FailsNamingTheStall()
    {
        using var lane = new IoPool(1);
        var release = new Flag();
        var running = new Flag();
        try
        {
            // Holds the only slot and ignores every token: what a source generator's constructor does.
            using var stuck = lane.InvokeBlocking(_ =>
            {
                running.Set = 1;
                SpinWait.SpinUntil(() => release.Set != 0, Generous * 2);
                return 0;
            }).Subscribe(_ => { }, _ => { });
            SpinWait.SpinUntil(() => running.Set != 0, Generous)
                .Should().BeTrue("the stuck leaf must hold the slot before the compile queues");

            var fault = await Fault(Bounded((ct, wait) => OnLane(lane, wait, ct, _ => 1), lane))
                .Should().Within(Generous)
                .Emit("a compile behind a lane that stopped draining must fail, not park",
                    cancellationToken: TestContext.Current.CancellationToken);

            fault.Should().BeOfType<TimeoutException>().Which.Message.Should()
                .Contain("queued for a CPU-lane slot").And.Contain("completed NO leaf");
        }
        finally
        {
            release.Set = 1;
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task ACompileQueuedLongerThanTheBound_BehindADrainingLane_Succeeds()
    {
        using var lane = new IoPool(1);
        // Four leaves ahead, each well inside the stall window, together far longer than the bound.
        var leafRun = Bound * 0.6;
        var ahead = new IDisposable[4];
        try
        {
            for (var i = 0; i < ahead.Length; i++)
                ahead[i] = lane.InvokeBlocking(_ =>
                {
                    SpinWait.SpinUntil(() => false, leafRun);
                    return 0;
                }).Subscribe(_ => { }, _ => { });

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var queuedFor = TimeSpan.Zero;
            var value = await Bounded((ct, wait) => OnLane(lane, wait, ct, _ =>
                {
                    queuedFor = clock.Elapsed;
                    return 7;
                }), lane)
                .Should().Within(Generous)
                .Emit("a compile waiting in a draining queue must run once admitted",
                    cancellationToken: TestContext.Current.CancellationToken);

            value.Should().Be(7);
            queuedFor.Should().BeGreaterThan(Bound,
                "the compile must have waited LONGER than the bound, or this test proves nothing");
        }
        finally
        {
            foreach (var a in ahead) a?.Dispose();
        }
    }
}
