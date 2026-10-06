using System;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Threading;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh.Threading;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 Issue #5057: <b>a release create that misses its bound says whether it was queued for an
/// I/O slot</b>.
///
/// <para><b>Why this is the next instrument.</b> On memex, 2026-10-05, `Hosting/ModuleInventory` and
/// `Hosting/PlatformBuildInbox` each missed the 10 s bound twice. Each create reached its pre-write
/// stamp (`createdDate`) within a second of being minted. Its `Node created at …` line came 12–29 s
/// later, and the handler logs that line only after the storage write emits. So the bound was spent
/// in the write leg. The only write gate in that leg is the `pg:{provider}` pool: capped at one,
/// FIFO, and shared by every partition in the process. Being queued there and being slow while
/// holding the slot are different defects with opposite fixes. The expiry line could not tell
/// them apart, because it carried no reading of any pool.</para>
///
/// <para><b>The control.</b> The production seam <see cref="NodeTypeBuildState.Bounded"/> runs on a
/// <see cref="HistoricalScheduler"/>, over a create that never lands, with a REAL
/// <see cref="IoPoolRegistry"/>. In the case, a cap-1 `pg:` pool holds one leaf and queues a
/// second, the shape of a write waiting behind another. The expiry must then name that pool, its
/// cap and its depth. The negative control is the same expiry over an idle registry, which must
/// read as the window-wide clean answer and name no pool. Before this change, both outcomes
/// carried the same sentence with no reading in it, so the case is red against the previous
/// code.</para>
///
/// <para>🚨 The park is a volatile int polled under a bounded <see cref="SpinWait.SpinUntil(Func{bool}, TimeSpan)"/>
/// and released in a <c>finally</c>, the shape `IoPoolQueueReportTest` uses. It is never a
/// semaphore or an event.</para>
/// </summary>
public class AReleaseCreateExpiryNamesTheQueuedPoolTest(ITestOutputHelper output)
{
    private const string ReleasePath = "Hosting/ModuleInventory/Release/20261005174622-9wQzFEsa";
    private const string WritePool = "pg:Postgres";

    /// <summary>Backstop for a parked leaf; the <c>finally</c> releases it long before.</summary>
    private static readonly TimeSpan ParkCeiling = TestTimeouts.Convergence;

    /// <summary>Bound on the setup step that queues the second leaf.</summary>
    private static readonly TimeSpan Established = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Runs the production bounded wait over a create that never lands, and expires it on the
    /// test's own clock. Returns the outcome's reason.
    /// </summary>
    private static string ExpireAndReadReason(IoPoolRegistry registry)
    {
        var clock = new HistoricalScheduler();
        NodeTypeBuildState.ReleaseCreateOutcome? outcome = null;
        using var subscription = NodeTypeBuildState
            .Bounded(Observable.Never<Unit>(), ReleasePath, clock, logger: null, registry)
            .Subscribe(o => outcome = o);

        clock.AdvanceBy(NodeTypeBuildState.CreateBound + TimeSpan.FromMilliseconds(1));

        outcome.Should().NotBeNull("the bound expired on the test's clock, so the wait has answered");
        outcome!.Succeeded.Should().BeFalse("the create never landed");
        outcome.AttemptedPath.Should().Be(ReleasePath);
        return outcome.Failure!;
    }

    /// <summary>
    /// 🚨 THE CASE: the write lane holds one leaf and has another waiting when the bound expires.
    /// The reason must name the pool, its cap and the queue.
    /// </summary>
    [Fact]
    public void AnExpiryWhileTheWriteLaneHasWorkQueued_NamesThatPool()
    {
        using var registry = new IoPoolRegistry();
        var lane = registry.Get(WritePool);

        var release = 0;
        IObservable<int> Park() => lane.InvokeBlocking(ct =>
        {
            SpinWait.SpinUntil(() => Volatile.Read(ref release) != 0 || ct.IsCancellationRequested, ParkCeiling);
            return 0;
        });

        // The snapshot Bounded takes when the wait OPENS is the baseline. The lane fills after it,
        // just as a release create's write queues behind writes accepted while it waits.
        var clock = new HistoricalScheduler();
        NodeTypeBuildState.ReleaseCreateOutcome? outcome = null;
        using var waiting = NodeTypeBuildState
            .Bounded(Observable.Never<Unit>(), ReleasePath, clock, logger: null, registry)
            .Subscribe(o => outcome = o);

        using var holding = Park().Subscribe(_ => { }, _ => { });
        using var queued = Park().Subscribe(_ => { }, _ => { });
        try
        {
            SpinWait.SpinUntil(() => lane.CurrentlyWaiting > 0, Established);
            lane.CurrentlyWaiting.Should().BeGreaterThan(0, "the second write must be queued behind the first");

            clock.AdvanceBy(NodeTypeBuildState.CreateBound + TimeSpan.FromMilliseconds(1));

            outcome.Should().NotBeNull();
            var reason = outcome!.Failure!;
            output.WriteLine(reason);

            reason.Should().Contain("did not land within", "it is still the expiry");
            reason.Should().Contain("At the expiry: " + IoPoolQueueReport.QueuedPrefix,
                "an expiry with the write lane queued must say so");
            reason.Should().Contain($"{WritePool}(cap 1)", "a depth means nothing without the pool and its cap");
            reason.Should().Contain("waiting");
            reason.Should().NotContain(IoPoolQueueReport.NothingQueued);
        }
        finally
        {
            Volatile.Write(ref release, 1);
        }
    }

    /// <summary>
    /// The NEGATIVE CONTROL: the same expiry over an idle lane reads as the window-wide clean
    /// answer, so the case above can only pass because the lane was queued.
    /// </summary>
    [Fact]
    public void AnExpiryWithEveryPoolIdle_SaysNothingWasQueuedOverTheWindow()
    {
        using var registry = new IoPoolRegistry();
        registry.Get(WritePool);

        var reason = ExpireAndReadReason(registry);
        output.WriteLine(reason);

        reason.Should().Contain("At the expiry: " + IoPoolQueueReport.NothingQueued,
            "a baseline was taken when the wait opened, so the reading covers the window");
        reason.Should().NotContain(IoPoolQueueReport.QueuedPrefix);
    }

    /// <summary>
    /// With no registry the expiry says the pools were NOT measured. That is never the same as a
    /// clean reading.
    /// </summary>
    [Fact]
    public void AnExpiryWithNoRegistry_SaysItWasNotMeasured()
    {
        var clock = new HistoricalScheduler();
        NodeTypeBuildState.ReleaseCreateOutcome? outcome = null;
        using var subscription = NodeTypeBuildState
            .Bounded(Observable.Never<Unit>(), ReleasePath, clock, logger: null, pools: null)
            .Subscribe(o => outcome = o);

        clock.AdvanceBy(NodeTypeBuildState.CreateBound + TimeSpan.FromMilliseconds(1));

        outcome!.Failure.Should().Contain("At the expiry: " + IoPoolQueueReport.NotMeasured);
    }
}
