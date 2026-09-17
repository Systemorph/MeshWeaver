using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive;
using System.Reactive.Subjects;
using MeshWeaver.Mesh.Threading;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <see cref="IoPool.Drain"/>'s GRACE measures PROGRESS, and issue #4541 is about what it reads
/// progress off. The grace is a stall bound, not a budget: every completion restarts it, so work
/// that keeps finishing is never cancelled. That promise held only while the pool's admission
/// census moved in ONE direction.
///
/// <para><b>It does not.</b> The drain took a baseline of the live admission count and then waited
/// for that count to fall BELOW it. The count also RISES — every entry point defers its prologue
/// to the ThreadPool (<c>SubscribeOn</c>), so a leaf whose <c>Subscribe()</c> returned before the
/// drain enters its gate region after the drain has taken its baseline. Each such arrival cancels
/// out a completion one for one, so the predicate can only fire once EVERY arrival has also
/// finished: the per-completion grace silently becomes a single total budget for the whole queue,
/// and whatever is left when it expires is cancelled — accepted work discarded, which is the one
/// thing the grace exists to prevent (#3291: teardown lets accepted work finish and NAMES what it
/// had to stop).</para>
///
/// <para>Measured as a 1-in-5 failure of
/// <c>IoPoolTest.Drain_restartsTheGraceOnEveryCompletion_SoABurstOfShortLeavesIsNeverCancelled</c>
/// under CPU saturation — "Expected 3 … but found 1". Saturation is the CONDITION, not the cause:
/// it only delays the prologues enough for the arrivals to land on the far side of the baseline.
/// These tests arrange that ordering STRUCTURALLY, through the drain's own baseline seam, so the
/// defect reproduces on an idle machine and the assertion pins the mechanism rather than the load.
/// </para>
/// </summary>
public class IoPoolDrainGraceTest
{
    private static readonly TimeSpan Timeout5 = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The bound on every release travelling INTO a leaf this test deliberately parks. Generous
    /// because it is a safety net, never a wait anything is expected to spend: the flag it polls is
    /// written before the leaf can need it, and the test's own <c>finally</c> writes it again so a
    /// failing assertion can never strand a pool thread into the next test.
    /// </summary>
    private static readonly TimeSpan ReleaseBound = TimeSpan.FromSeconds(10);

    /// <summary>A drain grace for a test whose leaf can only end by cancellation.</summary>
    private static readonly TimeSpan ShortGrace = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// 🚨 The numbers are chosen so the RED is structural, not a race that load decides.
    ///
    /// <para><see cref="QueuedLeaves"/> × <see cref="LeafWork"/> = 2400 ms STRICTLY EXCEEDS
    /// <see cref="Grace"/> = 2000 ms, and <c>Task.Delay</c> never fires early — so against the
    /// defect the last leaf is still running when a single total budget expires, on any machine.
    /// Load can only push it later, i.e. make it redder. Against the fixed pool each leaf has to
    /// finish within one whole grace of the previous one, which is 800 ms of work against a
    /// 2000 ms bound — 1200 ms of slack per step, where the pre-existing burst test has 200 ms.
    /// Neither number is a margin bought against load: widening the grace is exactly the band-aid
    /// #4541 forbids, and none of these values is the subject of any assertion.</para>
    /// </summary>
    private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(2000);

    /// <inheritdoc cref="Grace"/>
    private static readonly TimeSpan LeafWork = TimeSpan.FromMilliseconds(800);

    /// <inheritdoc cref="Grace"/>
    private const int QueuedLeaves = 3;

    /// <summary>
    /// 🚨 #4541 — A LEAF THAT REACHES THE GATE AFTER THE DRAIN'S BASELINE MUST GET A WHOLE GRACE,
    /// NOT WHAT IS LEFT OF SOMEONE ELSE'S.
    ///
    /// <para>One slot, one holder occupying it, and three leaves that reach the gate after the
    /// drain has taken its baseline. Every one of them is accepted work — each holds a gate region,
    /// each was issued before the drain, and each finishes in 800 ms, comfortably inside the
    /// 2000 ms grace. They run serially, so the whole queue takes 2400 ms.</para>
    ///
    /// <para>Against the defect the drain waits for the live admission count to fall below a
    /// baseline of ONE, which cannot happen until the last of the three has also finished — so the
    /// three share a single 2000 ms budget instead of getting 2000 ms each, and the last one is
    /// cancelled 400 ms from the end of work it was going to finish. The pool made progress four
    /// times in that window and the drain read it as a stall.</para>
    /// </summary>
    [Fact]
    public async Task Drain_aLeafThatReachesTheGateAfterTheBaseline_getsAWholeGraceOfItsOwn()
    {
        using var pool = new IoPool(1, IoPool.DefaultDrainTimeout, Grace);

        var holderAdmitted = new AsyncSubject<Unit>();
        var releaseHolder = 0;
        var completed = 0;
        var cancelled = 0;

        // THE HOLDER. Admitted before the drain, so the drain's baseline counts exactly one unit of
        // accepted work — which is the whole point: everything else arrives on the far side of it.
        // It owns the pool's only slot until the seam releases it, so the queued leaves are still
        // AT THE GATE when the grace clock starts, and it does no timed work of its own, so the
        // clock and the queue start together.
        pool.Invoke(ct =>
        {
            holderAdmitted.OnNext(Unit.Default);
            holderAdmitted.OnCompleted();
            SpinWait.SpinUntil(
                () => Volatile.Read(ref releaseHolder) == 1 || ct.IsCancellationRequested,
                ReleaseBound);
            if (ct.IsCancellationRequested)
            {
                Interlocked.Increment(ref cancelled);
                throw new OperationCanceledException(ct);
            }
            Interlocked.Increment(ref completed);
            return Task.FromResult(0);
        }).Subscribe(_ => { }, _ => { });

        // ISSUED BEFORE THE DRAIN — the pool's build-time refusal (`_draining`) rejects anything
        // issued after it, and these are deliberately not that case: they are work the pool had
        // already accepted, subscribed and queued. Only their arrival AT THE GATE is deferred, to
        // the far side of the baseline, which is what a saturated ThreadPool does on its own.
        var queued = Enumerable.Range(0, QueuedLeaves)
            .Select(_ => pool.Invoke(async ct =>
            {
                try
                {
                    await Task.Delay(LeafWork, ct);
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref cancelled);
                    throw;
                }
                Interlocked.Increment(ref completed);
                return 0;
            }))
            .ToArray();

        try
        {
            await holderAdmitted.Should().Within(Timeout5).Emit();

            pool.OnDrainGraceBaselineTaken = () =>
            {
                foreach (var leaf in queued)
                    leaf.Subscribe(_ => { }, _ => { });
                // Every queued leaf is provably AT THE GATE: it holds a region, so the drain can
                // see it, and it is waiting for the slot the holder still owns. Structural, not a
                // guess about ThreadPool dispatch order.
                SpinWait.SpinUntil(() => pool.CurrentlyWaiting == QueuedLeaves, ReleaseBound);
                Volatile.Write(ref releaseHolder, 1);
            };

            var sw = Stopwatch.StartNew();
            var residual = pool.Drain();
            sw.Stop();

            residual.Should().Be(0, "nothing ignored its token — the drain's join is real");
            sw.Elapsed.Should().BeGreaterThan(Grace,
                "the queue outlasts one whole grace BY CONSTRUCTION (3 × 800 ms of work against a "
                + "2000 ms grace, and Task.Delay never fires early) — a drain that returned inside "
                + "one grace never exercised the restart, and every assertion below would be vacuous");
            Volatile.Read(ref cancelled).Should().Be(0,
                "every leaf was accepted before the drain and finished well inside one grace; a "
                + "leaf that arrives after the baseline must EXTEND the drain, never consume the "
                + "grace of the leaf that finished before it");
            Volatile.Read(ref completed).Should().Be(QueuedLeaves + 1,
                "the holder plus every queued leaf finished on its own");
            pool.LeavesCancelledAfterGrace.Should().Be(0);
            pool.CancelledLeafSites.Should().BeEmpty();
        }
        finally
        {
            // Never leave a pool thread parked into the next test, whatever the assertions did.
            Volatile.Write(ref releaseHolder, 1);
        }
    }

    /// <summary>
    /// 🚨 THE CONTROL: the grace still EXPIRES, and it still names what it killed.
    ///
    /// <para>The fix above must not become unconditional patience. Same construction — a holder
    /// admitted before the baseline, a leaf reaching the gate after it — except this leaf can only
    /// end by cancellation. The holder's completion restarts the grace, the wedged leaf then gets
    /// a whole one of its own, and when that passes with nothing finishing the drain cancels it and
    /// SAYS SO. A grace that counted an arrival as progress, or that never expired, would leave
    /// this at zero.</para>
    /// </summary>
    [Fact]
    public async Task Drain_stillCancelsAndNamesALeafThatReachesTheGateAndThenWedges()
    {
        using var pool = new IoPool(1, IoPool.DefaultDrainTimeout, ShortGrace);

        var holderAdmitted = new AsyncSubject<Unit>();
        var releaseHolder = 0;
        var wedgedWasCancelled = false;

        pool.Invoke(ct =>
        {
            holderAdmitted.OnNext(Unit.Default);
            holderAdmitted.OnCompleted();
            SpinWait.SpinUntil(
                () => Volatile.Read(ref releaseHolder) == 1 || ct.IsCancellationRequested,
                ReleaseBound);
            return Task.FromResult(0);
        }).Subscribe(_ => { }, _ => { });

        var wedged = pool.Invoke(async ct =>
        {
            try
            {
                await Task.Delay(System.Threading.Timeout.Infinite, ct); // never completes on its own
            }
            catch (OperationCanceledException)
            {
                wedgedWasCancelled = true;
                throw;
            }
            return 0;
        });

        try
        {
            await holderAdmitted.Should().Within(Timeout5).Emit();

            pool.OnDrainGraceBaselineTaken = () =>
            {
                wedged.Subscribe(_ => { }, _ => { });
                SpinWait.SpinUntil(() => pool.CurrentlyWaiting == 1, ReleaseBound);
                Volatile.Write(ref releaseHolder, 1);
            };

            pool.Drain();

            wedgedWasCancelled.Should().BeTrue(
                "a leaf that outlives a whole grace with nothing finishing is wedged, and the drain "
                + "stops it — patience for accepted work is not patience for work that never ends");
            pool.LeavesCancelledAfterGrace.Should().Be(1,
                "the drain must SAY it killed a unit of accepted work, not discard it silently");
            string.Join(" | ", pool.CancelledLeafSites).Should().Contain(
                nameof(Drain_stillCancelsAndNamesALeafThatReachesTheGateAndThenWedges),
                "the killed leaf is named by its site, so the report points at the work that did not finish");
            pool.CurrentInFlight.Should().Be(0, "Drain joins synchronously");
        }
        finally
        {
            Volatile.Write(ref releaseHolder, 1);
        }
    }
}
