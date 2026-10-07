using System;
using System.Linq;
using System.Reactive;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh.Threading;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// MeshWeaver#5057 — a release create missed its 10 s bound on memex-cloud and the census printed
/// <c>pg-read:Postgres(cap 16) 94 waiting, 10 in flight</c>. That reading cannot say what the queue
/// was waiting FOR: ten leaves held permits and six permits were neither running nor shown free, and
/// the Orleans "Thread Pool execution stalled" line and a routing report reading "waiting for a pool
/// slot 60, subscribing 0" sat in the same eleven seconds. A queue behind an exhausted cap and a
/// queue behind free permits waiting for a thread to run or resume them look identical as a depth,
/// and they call for opposite remedies — which is why the census now splits them.
///
/// <para>Both halves are produced by construction, never by racing the ThreadPool: the
/// thread-wait case holds an accepted leaf in its prologue through
/// <c>IoPool.OnLeafPrologueStarting</c> (the leaf is accepted and has NOT reached the gate, with
/// every permit free); the cap case fills a cap-1 pool with a running occupant so the second leaf
/// waits at the gate itself. The second case is this test's negative control: the same report, the
/// same pool family, and the opposite verdict.</para>
/// </summary>
public class IoPoolQueueReportSaysCapOrThreadTest(ITestOutputHelper output)
{
    [Fact]
    public async Task QueuedWorkWithPermitsFree_IsReportedAsAThreadWait_NotAsTheCap()
    {
        using var registry = new IoPoolRegistry(new IoPoolOptions());
        var poolName = IoPoolNames.PostgresReadAdapterPrefix + "probe";
        var pool = (IoPool)registry.Get(poolName);
        var parked = new AsyncSubject<Unit>();
        var release = 0;
        pool.OnLeafPrologueStarting = () =>
        {
            parked.OnNext(Unit.Default);
            parked.OnCompleted();
            SpinWait.SpinUntil(() => Volatile.Read(ref release) == 1, TestTimeouts.Quick);
        };

        try
        {
            var baseline = registry.Snapshot();
            using var leaf = pool.Invoke(_ => Task.FromResult(1)).Subscribe(_ => { }, _ => { });
            await parked.Should().Within(TestTimeouts.Quick).Emit(
                "precondition: the leaf is accepted and held before it can reach the gate",
                cancellationToken: TestContext.Current.CancellationToken);

            var admission = registry.Snapshot().Single(r => r.Name == poolName).Admission;
            admission.Should().NotBeNull("a reading taken from a live pool must carry its admission split");
            admission!.Value.AsyncWaiting.Should().Be(1);
            admission.Value.PermitsFree.Should().Be(pool.MaxConcurrency,
                "nothing holds a permit: the queued leaf has not reached the gate");
            admission.Value.BeforeGate.Should().Be(1, "the leaf is held before it reaches the gate");
            admission.Value.BehindCap.Should().Be(0);

            var report = IoPoolQueueReport.Describe(registry, baseline);
            output.WriteLine(report);
            report.Should().StartWith(IoPoolQueueReport.QueuedPrefix);
            report.Should().Contain(IoPoolQueueReport.WaitsForAThread,
                "work queued while every permit is free is waiting for a thread to run it; calling "
                + "that a cap problem sends the reader to raise a bound that changes nothing");
            report.Should().NotContain(IoPoolQueueReport.WaitsForTheCap);
            report.Should().Contain("ThreadPool at that moment:",
                "a thread wait is only half explained without the ThreadPool's own depth beside it");
        }
        finally
        {
            Volatile.Write(ref release, 1);
        }
    }

    [Fact]
    public async Task QueuedWorkBehindAnExhaustedGate_IsReportedAsTheCap()
    {
        using var registry = new IoPoolRegistry(new IoPoolOptions());
        // `pg:` resolves to the cap-1 write pool, so ONE running leaf exhausts the gate.
        var poolName = IoPoolNames.PostgresAdapterPrefix + "probe";
        var pool = (IoPool)registry.Get(poolName);
        var occupying = new AsyncSubject<Unit>();
        var release = 0;

        try
        {
            using var occupant = pool.Invoke(_ =>
            {
                occupying.OnNext(Unit.Default);
                occupying.OnCompleted();
                SpinWait.SpinUntil(() => Volatile.Read(ref release) == 1, TestTimeouts.Quick);
                return Task.FromResult(0);
            }).Subscribe(_ => { }, _ => { });
            await occupying.Should().Within(TestTimeouts.Quick).Emit(
                "precondition: the occupant holds the pool's only permit",
                cancellationToken: TestContext.Current.CancellationToken);

            var baseline = registry.Snapshot();
            using var queued = pool.Invoke(_ => Task.FromResult(1)).Subscribe(_ => { }, _ => { });
            Assert.True(
                SpinWait.SpinUntil(() => pool.AdmissionReading.BehindCap == 1 && pool.CurrentInFlight == 1, TestTimeouts.Quick),
                "precondition: one leaf running, one inside the gate wait behind it");

            var admission = registry.Snapshot().Single(r => r.Name == poolName).Admission!.Value;
            admission.PermitsFree.Should().Be(0, "the occupant holds the only permit");
            admission.GrantedNotRunning.Should().Be(0, "the one held permit belongs to a RUNNING leaf");
            admission.BehindCap.Should().Be(1, "the queued leaf is inside the gate wait with no permit");
            admission.BeforeGate.Should().Be(0);

            var report = IoPoolQueueReport.Describe(registry, baseline);
            output.WriteLine(report);
            report.Should().Contain(IoPoolQueueReport.WaitsForTheCap,
                "a leaf queued behind a gate with no free permit is the one case where the cap IS "
                + "the constraint, and the report must still say so");
            report.Should().NotContain(IoPoolQueueReport.WaitsForAThread);
        }
        finally
        {
            Volatile.Write(ref release, 1);
        }
    }

    /// <summary>
    /// The review case (Copilot, #6260): a cap-1 gate held by a running leaf, one leaf waiting AT the
    /// gate, and one newly accepted leaf held BEFORE it. Permit availability alone reads this as
    /// "the cap is exhausted" — and that is wrong for the leaf that has not reached the gate. The
    /// report must name both parts of a mixed queue.
    /// </summary>
    [Fact]
    public async Task AMixedQueue_NamesTheThreadWaitAndTheCapWaitSeparately()
    {
        using var registry = new IoPoolRegistry(new IoPoolOptions());
        var poolName = IoPoolNames.PostgresAdapterPrefix + "mixed";
        var pool = (IoPool)registry.Get(poolName);
        var occupying = new AsyncSubject<Unit>();
        var parked = new AsyncSubject<Unit>();
        var release = 0;

        try
        {
            using var occupant = pool.Invoke(_ =>
            {
                occupying.OnNext(Unit.Default);
                occupying.OnCompleted();
                SpinWait.SpinUntil(() => Volatile.Read(ref release) == 1, TestTimeouts.Quick);
                return Task.FromResult(0);
            }).Subscribe(_ => { }, _ => { });
            await occupying.Should().Within(TestTimeouts.Quick).Emit(
                "precondition: the occupant holds the pool's only permit",
                cancellationToken: TestContext.Current.CancellationToken);

            var baseline = registry.Snapshot();
            using var atGate = pool.Invoke(_ => Task.FromResult(1)).Subscribe(_ => { }, _ => { });
            Assert.True(SpinWait.SpinUntil(() => pool.AdmissionReading.BehindCap == 1, TestTimeouts.Quick),
                "precondition: the second leaf is inside the gate wait");

            // Only now: hold the NEXT accepted leaf before it can reach the gate.
            pool.OnLeafPrologueStarting = () =>
            {
                parked.OnNext(Unit.Default);
                parked.OnCompleted();
                SpinWait.SpinUntil(() => Volatile.Read(ref release) == 1, TestTimeouts.Quick);
            };
            using var beforeGate = pool.Invoke(_ => Task.FromResult(2)).Subscribe(_ => { }, _ => { });
            await parked.Should().Within(TestTimeouts.Quick).Emit(
                "precondition: the third leaf is accepted and held before the gate",
                cancellationToken: TestContext.Current.CancellationToken);

            var admission = registry.Snapshot().Single(r => r.Name == poolName).Admission!.Value;
            admission.PermitsFree.Should().Be(0);
            admission.BehindCap.Should().Be(1, "one leaf waits at the gate with no permit");
            admission.BeforeGate.Should().Be(1, "one leaf has not been given a thread to reach the gate");

            var report = IoPoolQueueReport.Describe(registry, baseline);
            output.WriteLine(report);
            report.Should().Contain("1 " + IoPoolQueueReport.WaitsForAThread,
                "a leaf that has not reached the gate waits for a thread even when the cap is full");
            report.Should().Contain("1 " + IoPoolQueueReport.WaitsForTheCap,
                "and the leaf at the gate is the cap's");
        }
        finally
        {
            Volatile.Write(ref release, 1);
        }
    }

    [Fact]
    public async Task BlockingLeavesQueuedOnTheLane_AreNotReadAsAsyncGateWaits()
    {
        using var registry = new IoPoolRegistry(new IoPoolOptions());
        var poolName = IoPoolNames.PostgresAdapterPrefix + "blocking";
        var pool = (IoPool)registry.Get(poolName);
        var occupying = new AsyncSubject<Unit>();
        var release = 0;

        try
        {
            using var occupant = pool.InvokeBlocking(_ =>
            {
                occupying.OnNext(Unit.Default);
                occupying.OnCompleted();
                SpinWait.SpinUntil(() => Volatile.Read(ref release) == 1, TestTimeouts.Quick);
                return 0;
            }).Subscribe(_ => { }, _ => { });
            await occupying.Should().Within(TestTimeouts.Quick).Emit(
                "precondition: the blocking occupant holds the lane's only slot",
                cancellationToken: TestContext.Current.CancellationToken);

            using var queued = pool.InvokeBlocking(_ => 1).Subscribe(_ => { }, _ => { });
            Assert.True(SpinWait.SpinUntil(() => pool.CurrentlyWaiting >= 1, TestTimeouts.Quick),
                "precondition: a second blocking leaf is queued on the lane");

            var admission = registry.Snapshot().Single(r => r.Name == poolName).Admission!.Value;
            admission.BlockingWaiting.Should().Be(1);
            admission.AsyncWaiting.Should().Be(0,
                "a blocking leaf never passes the gate, so reading it against the gate's free permits "
                + "would report a thread wait the pool does not have");
            admission.WaitingForAThread.Should().Be(0);
            admission.BehindCap.Should().Be(0);
        }
        finally
        {
            Volatile.Write(ref release, 1);
        }

        Assert.True(SpinWait.SpinUntil(() => pool.CurrentlyWaiting == 0, TestTimeouts.Quick),
            "both blocking leaves ran, so the queue drains");
        registry.Snapshot().Single(r => r.Name == poolName).Admission!.Value.BlockingWaiting
            .Should().Be(0, "the blocking half of the gauge leaves exactly once, like the whole gauge");
    }
}
