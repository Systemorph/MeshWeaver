using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh.Threading;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// MeshWeaver#1198 — the readout that decides a pool cap must not answer "nothing was queued" while
/// the pool is holding work it accepted.
///
/// <para><b>Where this comes from.</b> #1198's last open item is the <c>pg:{adapter}</c> cap, and
/// what decides it is <see cref="IoPoolQueueReport"/>: the commit stage prints its verdict into the
/// timeout it raises, and <i>"no I/O pool had work queued … no admission during this stage waited a
/// second for a slot"</i> is the CONCLUSIVE half — it rules the pool gates out. That verdict is
/// computed from two numbers, <see cref="IIoPool.CurrentlyWaiting"/> and the
/// <see cref="IIoPool.QueueWait"/> buckets, and both used to start counting at the GATE.</para>
///
/// <para><b>The gap that leaves.</b> Since #4555 a leaf is ADMITTED on the subscriber's thread and
/// its prologue runs later, on a pool thread. Between those two moments the pool knows about the
/// leaf — it holds an admission region, and <c>Drain()</c> waits for it — while the readout counts
/// it nowhere. Measured on <c>main</c>: with one leaf parked in that interval the pool reported
/// <c>InFlight=0 Waiting=0</c> and the report returned the strong verdict; and under ThreadPool
/// saturation eight leaves waited up to <b>7,850 ms</b> from accepted to running while the
/// distribution recorded a maximum of <b>0.2 ms</b>. An instrument that cannot see the wait is the
/// thing #1198 is named after.</para>
///
/// <para><b>What changed.</b> The wait clock now starts where the admission is taken, which is what
/// <see cref="IIoPool.InvokeBlocking{T}"/> always did — so one definition, "accepted and not yet
/// running", across every entry point, and the verdict covers the latency an operator reads it as
/// covering.</para>
/// </summary>
public class IoPoolQueueReadingCoversAcceptedWorkTest
{
    /// <summary>
    /// The three entry points that defer their prologue to the ThreadPool, plus <c>InvokeBlocking</c>
    /// — which is here as the CONTROL: it has always taken its admission and started its wait clock
    /// on the subscriber's thread, so it was already right, and it is the precedent the other three
    /// now follow.
    /// </summary>
    public static TheoryData<string> EntryPoints() =>
        new("Invoke", "InvokeStream", "SubscribeThroughPool", "InvokeBlocking");

    [Theory]
    [MemberData(nameof(EntryPoints))]
    public async Task WorkThatHasBeenACCEPTED_IsReportedAsWaiting_BeforeItRuns(string entryPoint)
    {
        using var registry = new IoPoolRegistry(new IoPoolOptions());
        // 🚨 The `pg:` prefix resolves to the CAP-1 write pool (IoPoolOptions' own rule), which is
        // what lets the InvokeBlocking case hold its leaf behind an occupant — that entry point has
        // no prologue seam, because it never deferred its prologue in the first place. The other
        // three are held in their own prologue by the seams, so the cap is irrelevant to them; using
        // the same pool for all four keeps the reading being asserted identical.
        var poolName = IoPoolNames.PostgresAdapterPrefix + "probe-" + entryPoint;
        var pool = (IoPool)registry.Get(poolName);
        var parked = new AsyncSubject<Unit>();
        var completed = new AsyncSubject<Unit>();
        var release = 0;
        IDisposable? occupant = null;

        void Completed()
        {
            completed.OnNext(Unit.Default);
            completed.OnCompleted();
        }

        void Park()
        {
            parked.OnNext(Unit.Default);
            parked.OnCompleted();
            SpinWait.SpinUntil(() => Volatile.Read(ref release) == 1, TestTimeouts.Quick);
        }

        try
        {
            IObservable<int> leg;
            switch (entryPoint)
            {
                case "Invoke":
                    pool.OnLeafPrologueStarting = Park;
                    leg = pool.Invoke(_ => Task.FromResult(1));
                    break;
                case "InvokeStream":
                    pool.OnLeafPrologueStarting = Park;
                    leg = pool.InvokeStream(OneItem);
                    break;
                case "SubscribeThroughPool":
                    pool.OnSubscribeSetupLeafStarting = Park;
                    // A source that COMPLETES, so the leg terminates once its setup leaf has been
                    // granted a slot. A never-ending feed — the shape this entry point usually
                    // carries — would hold the leg open forever and the completion signal below could
                    // not fire; what is under test is the setup leaf's admission, not the feed.
                    leg = pool.SubscribeThroughPool(Observable.Return(1));
                    break;
                default:
                    // The control: the pool's ONE slot is taken by a leaf that parks, so the leaf
                    // under test is accepted and queued behind it — InvokeBlocking's own shape for
                    // "accepted, not yet running".
                    occupant = pool.InvokeBlocking(_ => { Park(); return 0; }).Subscribe(_ => { }, _ => { });
                    await parked.Should().Within(TestTimeouts.Quick).Emit(
                        "precondition: the occupant holds the pool's only slot",
                        cancellationToken: TestContext.Current.CancellationToken);
                    leg = pool.InvokeBlocking(_ => 1);
                    break;
            }

            var baseline = registry.Snapshot();
            var baselineSamples = baseline.FirstOrDefault(r => r.Name == poolName).QueueWait.Samples;
            // The interval the leaf provably spends ACCEPTED but not running. The new clock must see
            // it; the old one, which started at the gate, saw ~0 of it.
            var accepted = Stopwatch.StartNew();
            using var subscription = leg.Subscribe(_ => { }, _ => Completed(), Completed);

            if (entryPoint != "InvokeBlocking")
                await parked.Should().Within(TestTimeouts.Quick).Emit(
                    "precondition: the leaf has been accepted and is held before it can run — the "
                    + "interval this test is about",
                    cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(
                SpinWait.SpinUntil(() => pool.CurrentlyWaiting >= 1, TestTimeouts.Quick),
                $"{entryPoint}: work the pool has ACCEPTED but not yet run must count as waiting. The "
                + "cap decision in MeshWeaver#1198 rests on this gauge and on the wait buckets beside "
                + "it; while they started at the GATE, a leaf admitted on the subscriber's thread was "
                + "invisible to both — measured on main as InFlight=0 Waiting=0 with the report "
                + "returning its CONCLUSIVE verdict, and as 7,850 ms of accepted-to-running latency "
                + "recorded as 0.2 ms");

            var verdict = IoPoolQueueReport.Describe(registry, baseline);
            verdict.Should().StartWith(IoPoolQueueReport.QueuedPrefix,
                $"{entryPoint}: with accepted work outstanding the report must NAME the pool rather "
                + "than clear it. Its own contract is that 'nothing queued' is the conclusive half — "
                + "so issuing it over work the pool is holding is the instrument asserting the "
                + "opposite of the truth, which is the failure mode #1198 keeps meeting");

            // 🚨 THE OTHER INPUT TO THE SAME VERDICT. The report is built from the gauge AND the wait
            // buckets, so a change that moved only the gauge would leave the sentence half-wrong and
            // this test green (Copilot review). The leaf is released here and its RECORDED admission
            // is compared against the interval it was held: with the clock at the gate that admission
            // reads ~0 ms however long the leaf waited to run.
            accepted.Stop();
            Volatile.Write(ref release, 1);

            await completed.Should().Within(TestTimeouts.Quick).Emit(
                $"{entryPoint}: the released leaf must finish, so its admission is recorded",
                cancellationToken: TestContext.Current.CancellationToken);

            var after = registry.Snapshot().First(r => r.Name == poolName).QueueWait;
            after.Samples.Should().BeGreaterThan(baselineSamples,
                $"{entryPoint}: the leaf was granted a slot, so exactly that admission must appear in "
                + "the distribution the cap decision reads");
            after.Max.Should().BeGreaterThan(accepted.Elapsed / 2,
                $"{entryPoint}: the recorded wait must cover the interval the leaf was ACCEPTED but "
                + $"not running ({accepted.Elapsed.TotalMilliseconds:F0} ms here). Half of it, not all, "
                + "because the release and the grant are two instants on two threads — the "
                + "discrimination is against a gate-start clock, which records ~0 ms for the same leaf");
        }
        finally
        {
            // Idempotent safety: an assertion that throws above must not leave a pool thread parked.
            Volatile.Write(ref release, 1);
            occupant?.Dispose();
        }
    }

    private static async IAsyncEnumerable<int> OneItem([EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        yield return 1;
    }
}
