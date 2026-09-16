using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh.Threading;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Focused coverage for the readout MeshWeaver#1198 waits on — <see cref="IoPoolRegistry.Snapshot"/>
/// and <see cref="IoPoolQueueReport.Describe"/>.
///
/// <para><b>Why these deserve their own tests rather than the delete suite's.</b> The delete
/// regression drives this through a real mesh, which always HAS a registry and produces one queued
/// pool — so the branches that decide whether the reading can be trusted at all (no registry; no
/// baseline; more pools than the report will name) are never reached there, and a regression in any
/// of them would pass. The distinction those branches carry IS the subject: this issue's whole
/// history is instruments that cannot tell "I did not measure" from "I measured, and it was
/// clean".</para>
/// </summary>
public class IoPoolQueueReportTest(ITestOutputHelper output)
{
    /// <summary>
    /// 🚨 A pool a test PARKS must be released even when an assertion throws, so every park in this
    /// file is a volatile int polled under a bounded <see cref="SpinWait.SpinUntil"/> and written in
    /// a <c>finally</c> — never a <see cref="SemaphoreSlim"/> or an event used as a gate.
    ///
    /// <para>The ceiling is <see cref="TestTimeouts.Convergence"/>, never a hand-written 30 s: the
    /// literal is the copied convention <c>TestTimeoutLiteralRatchetGuard</c> exists to drive out,
    /// and it is also wrong on CI, which runs slower than the machine any such number was picked
    /// on. This is a backstop the <c>finally</c> should always beat anyway.</para>
    /// </summary>
    private static readonly TimeSpan ParkCeiling = TestTimeouts.Convergence;

    /// <summary>
    /// Bound on the test's own wait for a park to take effect. A reasoned, deliberately SHORTER
    /// value than <see cref="ParkCeiling"/> — it bounds a SETUP step, and blowing it must fail the
    /// test loudly rather than let a case about a queued pool run against an idle one.
    /// </summary>
    private static readonly TimeSpan Established = TimeSpan.FromSeconds(10);

    [Fact]
    public void NoRegistry_SaysItWasNotMeasured_NeverThatNothingWasQueued()
    {
        // The whole point of the third sentence. "Nobody asked" must not be reported as a clean
        // reading — that is how an unmeasured pool comes to look like an idle one.
        IoPoolQueueReport.Describe(null).Should().Be(IoPoolQueueReport.NotMeasured);
        IoPoolQueueReport.NotMeasured.Should().NotBe(IoPoolQueueReport.NothingQueued);
        IoPoolQueueReport.NotMeasured.Should().NotBe(IoPoolQueueReport.NothingQueuedNow);
    }

    [Fact]
    public void AnIdleRegistry_ReadsAsAWindowOnlyWithABaseline()
    {
        using var registry = new IoPoolRegistry();
        registry.Get("pg:idle");

        // Without a baseline the honest answer is the WEAKER one: a depth sampled once says nothing
        // about a leaf that waited earlier and was then granted its slot.
        IoPoolQueueReport.Describe(registry).Should().Be(IoPoolQueueReport.NothingQueuedNow,
            "with no baseline the reading is an instant, and an instant cannot exonerate a cap");

        // With one, the sentence may cover the window — and must say so differently.
        IoPoolQueueReport.Describe(registry, registry.Snapshot())
            .Should().Be(IoPoolQueueReport.NothingQueued,
                "a baseline turns the reading into a window, which is the only form that rules "
                + "anything out");
    }

    [Fact]
    public void SnapshotEnumeratesWhatExists_AndMintsNothing()
    {
        using var registry = new IoPoolRegistry();

        registry.Snapshot().Should().BeEmpty("a registry nobody has used holds no pools");

        // 🚨 THE SUBJECT. Reading must not be a way to CREATE. `Get` is a resolver and answers an
        // unknown name by minting that pool, which then reports itself as idle — so a readout built
        // on `Get` cannot tell a typo from a quiet pool, and leaves a phantom behind either way.
        _ = IoPoolQueueReport.Describe(registry, registry.Snapshot());
        registry.Snapshot().Should().BeEmpty(
            "describing a registry must not bring a pool into existence");

        registry.Get("pg:real");
        var reading = registry.Snapshot().Should().ContainSingle().Which;
        reading.Name.Should().Be("pg:real");
        reading.MaxConcurrency.Should().Be(1, "the `pg:` prefix caps a pool at one");
        reading.Waiting.Should().Be(0);
        reading.QueueWait.Samples.Should().Be(0);
    }

    [Fact]
    public void AQueuedPool_IsNamedWithItsCapAndDepth()
    {
        using var registry = new IoPoolRegistry();
        var pool = registry.Get("pg:queued");
        var baseline = registry.Snapshot();

        var release = 0;
        IObservable<int> Park() => pool.InvokeBlocking(ct =>
        {
            SpinWait.SpinUntil(() => Volatile.Read(ref release) != 0 || ct.IsCancellationRequested, ParkCeiling);
            return 0;
        });

        using var holding = Park().Subscribe(_ => { }, _ => { });
        using var queued = Park().Subscribe(_ => { }, _ => { });
        try
        {
            SpinWait.SpinUntil(() => pool.CurrentlyWaiting > 0, Established);
            pool.CurrentlyWaiting.Should().BeGreaterThan(0, "the second leaf must be queued behind the first");

            var report = IoPoolQueueReport.Describe(registry, baseline);
            output.WriteLine(report);

            report.Should().StartWith(IoPoolQueueReport.QueuedPrefix);
            report.Should().Contain("pg:queued(cap 1)", "a depth means nothing without the cap beside it");
            report.Should().Contain("waiting");
            report.Should().NotContain(IoPoolQueueReport.NothingQueued,
                "reporting a queued pool as a clean reading would be the conclusive half of this "
                + "instrument asserting the opposite of the truth");
        }
        finally
        {
            Volatile.Write(ref release, 1);
        }
    }

    [Fact]
    public void MoreQueuedPoolsThanItWillName_AreCountedNotDropped()
    {
        const int PoolCount = 8;
        using var registry = new IoPoolRegistry();
        var pools = Enumerable.Range(0, PoolCount)
            .Select(i => registry.Get($"pg:many-{i:00}"))
            .ToArray();
        var baseline = registry.Snapshot();

        var release = 0;
        IObservable<int> Park(IIoPool p) => p.InvokeBlocking(ct =>
        {
            SpinWait.SpinUntil(() => Volatile.Read(ref release) != 0 || ct.IsCancellationRequested, ParkCeiling);
            return 0;
        });

        // Two per pool: the first takes the single slot, the second queues behind it.
        var subs = pools
            .SelectMany(p => new[] { Park(p).Subscribe(_ => { }, _ => { }), Park(p).Subscribe(_ => { }, _ => { }) })
            .ToArray();
        try
        {
            SpinWait.SpinUntil(() => pools.All(p => p.CurrentlyWaiting > 0), Established);
            pools.Should().OnlyContain(p => p.CurrentlyWaiting > 0,
                "every pool must be queueing before the truncation can be the thing under test");

            var report = IoPoolQueueReport.Describe(registry, baseline);
            output.WriteLine(report);

            // 🚨 The remainder is COUNTED, never silently dropped — a list that quietly stops at
            // five reads as "these five are all of them", which is a smaller version of the
            // `unanswered=-` untruth this issue already fixed once.
            report.Should().Contain($"(+{PoolCount - 5} more pool(s))",
                "the pools it did not name must still be accounted for");
        }
        finally
        {
            Volatile.Write(ref release, 1);
            foreach (var s in subs)
                s.Dispose();
        }
    }

    [Fact]
    public void ABlockingAdmissionCancelledBeforeItStarts_DoesNotLeakTheWaitingGauge()
    {
        // 🚨 REGRESSION (Copilot review on #4504). InvokeBlocking creates its task WITH the
        // subscription's token, so a subscription disposed while the task is still QUEUED on the
        // limited-concurrency scheduler is completed as Canceled and the delegate NEVER RUNS — and
        // the decrement of the waiting gauge used to live only inside that delegate. Every such
        // admission leaked a permanent +1, so a pool that had long gone idle kept reporting a queue
        // that did not exist. CurrentlyWaiting is read as EVIDENCE by the delete's commit-stage
        // timeout, so a gauge that only climbs would answer "something was queued" forever after one
        // cancelled leaf.
        //
        // 🚨 THE ORDER OF THE LAST TWO STEPS IS THE TEST. A cancelled task is not dequeued the
        // moment it is cancelled: the limited-concurrency scheduler only reaches it when a slot
        // frees, and until then it is genuinely still queued — CurrentlyWaiting == 1 there is
        // CORRECT, not a leak. So the park must be RELEASED first; the scheduler then processes the
        // cancelled entry and the gauge must return to zero. Asserting before the release measures
        // the honest intermediate state and fails against the fix as well as without it.
        using var registry = new IoPoolRegistry();
        var pool = registry.Get("pg:cancelled");

        var release = 0;
        IObservable<int> Park() => pool.InvokeBlocking(ct =>
        {
            SpinWait.SpinUntil(() => Volatile.Read(ref release) != 0 || ct.IsCancellationRequested, ParkCeiling);
            return 0;
        });

        var holding = Park().Subscribe(_ => { }, _ => { });
        try
        {
            // The second leaf can never start: the first holds the pool's only slot.
            var neverStarts = Park().Subscribe(_ => { }, _ => { });
            // 🚨 WAIT FOR THE STATE THIS ASSERTS, NOT A WEAKER PROXY. `CurrentlyWaiting > 0` is
            // already true the instant the FIRST leaf is queued — the gauge is raised on subscribe,
            // before the scheduler grants it a slot — so a wait on it can return with BOTH leaves
            // still queued and the assertion then reads 2, having measured a state the test is not
            // about. The state it IS about is "one running, one queued", and `CurrentInFlight == 1`
            // is what says the first leaf's delegate has started: the delegate leaves the waiting
            // gauge and records its wait BEFORE it increments the in-flight count, so the two
            // readings can only be (1, 1) together once that has happened.
            SpinWait.SpinUntil(() => pool.CurrentInFlight == 1 && pool.CurrentlyWaiting == 1, Established);
            pool.CurrentInFlight.Should().Be(1,
                "the first leaf must be RUNNING — if it is still queued the test never reached the "
                + "state it is about");
            pool.CurrentlyWaiting.Should().Be(1,
                "exactly one leaf is queued behind the running one — if this is 0 the test never "
                + "reached the state it is about");

            // Cancel it WHILE QUEUED. This is the ordinary path: it is what an unsubscribe and every
            // lapsed bound does.
            neverStarts.Dispose();
        }
        finally
        {
            // Released here rather than at the end: freeing the slot is what lets the scheduler
            // reach the cancelled entry, which is the transition under test. In a `finally` so a
            // failed assertion above cannot strand the parked leaf.
            Volatile.Write(ref release, 1);
        }

        SpinWait.SpinUntil(() => pool.CurrentlyWaiting == 0, Established);
        pool.CurrentlyWaiting.Should().Be(0,
            "once the scheduler has reached the cancelled entry the gauge must be back where it "
            + "started — before the fix nothing ever decremented it and it stayed at 1 for the life "
            + "of the pool");

        // NEGATIVE CONTROL — it was cancelled, not run. A cancelled wait contributes NOTHING to the
        // distribution (it never became an admission), so a gauge balanced by RECORDING it would be
        // right for the wrong reason: counting a teardown as work.
        SpinWait.SpinUntil(() => pool.QueueWait.Samples > 0, Established);
        pool.QueueWait.Samples.Should().Be(1,
            "only the leaf that actually ran is an admission — the cancelled one must not have been "
            + "folded into the wait distribution");

        // And the report agrees: nothing is queued any more. A phantom entry here would make every
        // later reading on this pool name it, which is the failure this whole change exists to stop.
        IoPoolQueueReport.Describe(registry, registry.Snapshot())
            .Should().Be(IoPoolQueueReport.NothingQueued,
                "a phantom queue entry would make every later reading name this pool");

        holding.Dispose();
    }
}
