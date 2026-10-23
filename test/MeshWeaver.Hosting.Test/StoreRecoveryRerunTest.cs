using System;
using System.Data.Common;
using System.Net.Sockets;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Reactive.Testing;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// The re-run primitive behind MeshWeaver#7066, in isolation: a finite job that faulted on a
/// TRANSIENT store fault runs again on the store's recovery signal (or a bounded timer racing it),
/// and anything else surfaces unchanged. The end-to-end reproduction through the real hosted service
/// and the real mesh is <c>AWarmUpSweepRerunsWhenTheStoreRecoversTest</c> (MeshWeaver.FaultInjection.Test).
/// </summary>
public class StoreRecoveryRerunTest
{
    private sealed class RefusedException(string message, Exception inner) : DbException(message, inner)
    {
        public override bool IsTransient => true;
    }

    private static Exception Refused() =>
        new RefusedException("Failed to connect to 10.0.0.1:5432", new SocketException(111));

    private static ChangeFeedGap Gap() =>
        new("test", DateTimeOffset.UtcNow.AddSeconds(-40), DateTimeOffset.UtcNow, "connection refused");

    private static readonly StoreRecoveryRerunPolicy NoTimer = new() { MaxTimedReruns = 0 };

    [HubFact]
    public async Task ATransientFault_IsReRun_OnTheStoresRecoverySignal()
    {
        var ct = TestContext.Current.CancellationToken;
        var gaps = new Subject<ChangeFeedGap>();
        var scheduled = new ReplaySubject<StoreRecoveryRerunScheduled>();
        var attempts = 0;
        var result = new AsyncSubject<string>();

        using var run = StoreRecoveryRerun.RerunOnStoreRecovery(
                attempt =>
                {
                    Interlocked.Increment(ref attempts);
                    return attempt == 0
                        ? Observable.Throw<string>(Refused())
                        : Observable.Return($"attempt {attempt}");
                },
                gaps, NoTimer, System.Reactive.Concurrency.Scheduler.Immediate, scheduled.OnNext)
            .Subscribe(result);

        var pending = await scheduled.Take(1)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the re-run must be ANNOUNCED before it waits, so the gap is never silent", ct);
        pending.Rerun.Should().Be(1);
        pending.Backoff.Should().BeNull("no timed re-run is allowed — only the store's signal");
        pending.Fault.Should().BeOfType<RefusedException>();
        Volatile.Read(ref attempts).Should().Be(1, "nothing re-runs before the store has recovered");

        gaps.OnNext(Gap());

        var value = await result
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the store's recovery signal re-runs the job, and the re-run completes it", ct);
        value.Should().Be("attempt 1");
        Volatile.Read(ref attempts).Should().Be(2);
    }

    [HubFact]
    public async Task ARecoveryDeclaredWhileTheFaultIsBeingRecorded_IsNotLost()
    {
        var ct = TestContext.Current.CancellationToken;
        var gaps = new Subject<ChangeFeedGap>();
        var result = new AsyncSubject<string>();

        // The signal arrives INSIDE the caller's recording of the fault — the earliest instant it can.
        using var run = StoreRecoveryRerun.RerunOnStoreRecovery(
                attempt => attempt == 0
                    ? Observable.Throw<string>(Refused())
                    : Observable.Return("re-ran"),
                gaps, NoTimer, System.Reactive.Concurrency.Scheduler.Immediate, _ => gaps.OnNext(Gap()))
            .Subscribe(result);

        (await result.Should().Within(TestTimeouts.Convergence)
                .Emit("a recovery declared while the fault is recorded must still re-run the job", ct))
            .Should().Be("re-ran");
    }

    [HubFact]
    public void TheTimer_RacesTheSignal_ForTheTimedReRunsOnly_ThenOnlyTheSignalCanReRun()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var scheduler = new TestScheduler();
        var gaps = new Subject<ChangeFeedGap>();
        var announced = new System.Collections.Generic.List<StoreRecoveryRerunScheduled>();
        var attempts = 0;
        var policy = new StoreRecoveryRerunPolicy { MaxTimedReruns = 2, Backoff = n => TimeSpan.FromSeconds(n) };

        using var run = StoreRecoveryRerun.RerunOnStoreRecovery(
                _ =>
                {
                    Interlocked.Increment(ref attempts);
                    return Observable.Throw<Unit>(Refused());
                },
                gaps, policy, scheduler, announced.Add)
            .Subscribe(_ => { }, _ => { });

        attempts.Should().Be(1);
        scheduler.AdvanceBy(TimeSpan.FromSeconds(1).Ticks);
        attempts.Should().Be(2, "timed re-run 1 fires after its 1 s backoff");
        scheduler.AdvanceBy(TimeSpan.FromSeconds(2).Ticks);
        attempts.Should().Be(3, "timed re-run 2 fires after its 2 s backoff");
        scheduler.AdvanceBy(TimeSpan.FromHours(1).Ticks);
        attempts.Should().Be(3, "the timed re-runs are spent: no clock re-runs the job again");
        announced[^1].Backoff.Should().BeNull();
        announced[^1].Trigger.Should().Contain("no clock");

        gaps.OnNext(Gap());
        attempts.Should().Be(4, "the store's recovery signal still re-runs it — the fault is never final");
    }

    [HubFact]
    public async Task ANonTransientFault_SurfacesUnchanged_AndIsNeverReRun()
    {
        var ct = TestContext.Current.CancellationToken;
        var gaps = new Subject<ChangeFeedGap>();
        var announced = 0;
        var attempts = 0;
        var terminal = new AsyncSubject<Exception>();

        using var run = StoreRecoveryRerun.RerunOnStoreRecovery(
                _ =>
                {
                    Interlocked.Increment(ref attempts);
                    return Observable.Throw<Unit>(new InvalidOperationException("CS0103: a real defect"));
                },
                gaps, StoreRecoveryRerunPolicy.Default, System.Reactive.Concurrency.Scheduler.Immediate,
                _ => Interlocked.Increment(ref announced))
            .Subscribe(_ => { }, ex => { terminal.OnNext(ex); terminal.OnCompleted(); });

        (await terminal.Should().Within(TestTimeouts.Convergence)
                .Emit("a defect ends the job on its first occurrence", ct))
            .Should().BeOfType<InvalidOperationException>();
        gaps.OnNext(Gap());
        Volatile.Read(ref attempts).Should().Be(1, "re-running a defect only repeats it");
        Volatile.Read(ref announced).Should().Be(0);
    }

    /// <summary>
    /// 🚨 The incident's EXACT fault: the owner met the refusal, so the caller holds only the wire
    /// record — <c>MeshNode Unknown at 'Admin/Build': NpgsqlException: Failed to connect</c> with no
    /// inner exception. It is transient because the OWNER said so on the record, never because of
    /// its message text.
    /// </summary>
    [Fact]
    public void AnOwnersTransientStoreFault_CrossesTheWire_AsTransient_AndOnlyThen()
    {
        var transient = new MeshNodeStreamException(new MeshNodeError(
            MeshNodeErrorCode.Unknown, "Admin/Build", "NpgsqlException: Failed to connect to 10.0.0.1:5432")
        { Transient = true });
        var sameTextUnclassified = new MeshNodeStreamException(new MeshNodeError(
            MeshNodeErrorCode.Unknown, "Admin/Build", "NpgsqlException: Failed to connect to 10.0.0.1:5432"));

        StandingWatchRecovery.IsTransient(transient).Should().BeTrue();
        StandingWatchRecovery.IsTransient(sameTextUnclassified).Should().BeFalse(
            "the message text is not a contract — only the owner's classification counts");
        StandingWatchRecovery.IsTransient(new AggregateException(transient, new InvalidOperationException("defect")))
            .Should().BeFalse("a transient branch must not launder a defect in another branch");
    }

    /// <summary>The gate's half: a re-run puts a strict pod back to measuring, and a completed
    /// re-run supersedes the fault — while a pod its fault already let serve is never taken out.</summary>
    [Fact]
    public void TheGate_ReRunsOutOfAFault_WithoutTakingAServingPodOut()
    {
        var strict = new NodeTypeBakeGateState { GatesReadiness = true };
        strict.MarkRunning("enumerating dynamic NodeTypes");
        strict.MarkFaulted("transient store fault — re-runs when the store is back");
        strict.ReadinessGranted.Should().BeFalse();
        strict.MarkRerunning("re-running the NodeType sweep (re-run 1)");
        strict.Phase.Should().Be(BakePhase.Running, "a re-running strict pod is measuring again");
        strict.Admission.Should().Be(MeshAdmission.Provisional, "measuring means provisional, not refused");
        strict.MarkComplete("baked");
        strict.Phase.Should().Be(BakePhase.Complete);
        strict.ReadinessGranted.Should().BeTrue("the re-run measured what the faulted attempt could not");

        var served = new NodeTypeBakeGateState { GatesReadiness = true };
        served.MarkServedBefore();
        served.MarkRunning("enumerating dynamic NodeTypes");
        served.MarkFaulted("transient store fault — re-runs when the store is back");
        served.ReadinessGranted.Should().BeTrue("an image that has served here is relaxed by a fault (#5544)");
        served.MarkRerunning("re-running the NodeType sweep (re-run 1)");
        served.ReadinessGranted.Should().BeTrue("a re-run must never take a pod out that its fault left in");
        served.Detail.Should().Contain("re-running");
    }
}
