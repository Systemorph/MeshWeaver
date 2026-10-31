using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Threading;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// <see cref="SerialWriteLane"/> never leaves a caller without a verdict (review of
/// MeshWeaver.Plugins #4803): a caller whose observer throws does not stop the lane, and disposing
/// the lane — alone or with its registry at mesh teardown — answers the running, the queued and the
/// later writes instead of dropping them.
///
/// <para>The parked head is a real leaf on the real budget pool, released through a volatile flag
/// polled under a bounded <see cref="SpinWait.SpinUntil(Func{bool}, TimeSpan)"/> and written in a
/// <c>finally</c>.</para>
/// </summary>
public class SerialWriteLaneLifecycleTest
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private int _release;

    private Task<int> ParkedLeaf(CancellationToken ct) => Task.Run(() =>
    {
        SpinWait.SpinUntil(() => Volatile.Read(ref _release) == 1 || ct.IsCancellationRequested, Bound);
        ct.ThrowIfCancellationRequested();
        return 1;
    }, CancellationToken.None);

    private static IObservable<SerialWriteLaneReading> LaneReading(SerialWriteLane lane, Func<SerialWriteLaneReading, bool> state) =>
        Observable.Interval(TimeSpan.FromMilliseconds(10)).StartWith(0L)
            .Select(_ => lane.Read())
            .Where(state);

    private static IObservable<Exception> Fault<T>(ReplaySubject<T> outcome) =>
        outcome.Materialize().Where(n => n.Kind == System.Reactive.NotificationKind.OnError).Select(n => n.Exception!);

    [Fact]
    public async Task ACallerWhoseObserverThrows_DoesNotStopTheLane()
    {
        using var registry = new IoPoolRegistry();
        var lane = registry.GetSerialLane("pg:Postgres:throwing", registry.Get(IoPoolNames.PostgresWriteBudgetPrefix + "Postgres"));

        using var throwing = lane.Run(_ => Task.FromResult(1))
            .Subscribe(_ => throw new InvalidOperationException("the caller's own defect"));
        var next = new ReplaySubject<int>();
        using var nextWrite = lane.Run(_ => Task.FromResult(2)).Subscribe(next);

        (await next.Should().Within(Bound).Emit(
            "🚨 a caller whose observer throws must not fault the lane's drain — every later write would get no verdict"))
            .Should().Be(2);
    }

    [Fact]
    public async Task DisposingTheLane_AnswersRunningQueuedAndLaterWrites()
    {
        using var registry = new IoPoolRegistry();
        var lane = registry.GetSerialLane("pg:Postgres:disposed", registry.Get(IoPoolNames.PostgresWriteBudgetPrefix + "Postgres"));
        var running = new ReplaySubject<int>();
        var queued = new ReplaySubject<int>();
        var later = new ReplaySubject<int>();
        IDisposable? runningWrite = null, queuedWrite = null, laterWrite = null;
        try
        {
            runningWrite = lane.Run(ParkedLeaf).Subscribe(running);
            queuedWrite = lane.Run(_ => Task.FromResult(2)).Subscribe(queued);
            await LaneReading(lane, r => r.InFlight == 1 && r.Waiting == 1)
                .Should().Within(Bound).Emit("one write runs and one waits");

            lane.Dispose();
            laterWrite = lane.Run(_ => Task.FromResult(3)).Subscribe(later);
        }
        finally
        {
            Volatile.Write(ref _release, 1);
        }

        (await running.Should().Within(Bound).Emit("the running write is drained, not cut off")).Should().Be(1);
        (await Fault(queued).Should().Within(Bound).Emit("the queued write is answered, not dropped"))
            .Should().BeOfType<ObjectDisposedException>();
        (await Fault(later).Should().Within(Bound).Emit("a write subscribed after disposal is answered, not ignored"))
            .Should().BeOfType<ObjectDisposedException>();
        runningWrite?.Dispose();
        queuedWrite?.Dispose();
        laterWrite?.Dispose();
    }

    [Fact]
    public async Task MeshTeardown_WithSeveralQueuedWritesOnOneLane_TerminatesEveryCaller()
    {
        var registry = new IoPoolRegistry();
        var lane = registry.GetSerialLane("pg:Postgres:teardown", registry.Get(IoPoolNames.PostgresWriteBudgetPrefix + "Postgres"));
        var outcomes = Enumerable.Range(0, 4).Select(_ => new ReplaySubject<int>()).ToArray();
        var writes = new List<IDisposable>();
        try
        {
            writes.Add(lane.Run(ParkedLeaf).Subscribe(outcomes[0]));
            for (var i = 1; i < outcomes.Length; i++)
            {
                var value = i;
                writes.Add(lane.Run(_ => Task.FromResult(value)).Subscribe(outcomes[i]));
            }
            await LaneReading(lane, r => r.InFlight == 1 && r.Waiting == outcomes.Length - 1)
                .Should().Within(Bound).Emit("one write runs and the rest wait on the same lane");

            registry.Dispose();
        }
        finally
        {
            Volatile.Write(ref _release, 1);
        }

        foreach (var outcome in outcomes)
            await outcome.Materialize().Where(n => n.Kind != System.Reactive.NotificationKind.OnNext)
                .Should().Within(Bound)
                .Emit("🚨 every caller of a lane torn down with its mesh gets a terminal — none waits forever");
        foreach (var write in writes)
            write.Dispose();
    }
    [Fact]
    public void ALaneAskedForAfterTheRegistryIsDisposed_RefusesWork_AndIsNotKept()
    {
        var registry = new IoPoolRegistry();
        var budget = registry.Get(IoPoolNames.PostgresWriteBudgetPrefix + "Postgres");
        registry.Dispose();

        var lane = registry.GetSerialLane("pg:Postgres:late", budget);
        registry.SerialLaneSnapshot().Should().BeEmpty("🚨 a closed registry must not be repopulated by a lane nobody will dispose");

        Exception? refusal = null;
        using var write = lane.Run(_ => Task.FromResult(1)).Subscribe(_ => { }, ex => refusal = ex);
        refusal.Should().BeOfType<ObjectDisposedException>("a refused lane answers every write instead of running it");
    }

    [Fact]
    public async Task ADeleteGivenUpWhileQueued_LeavesTheCountersAtOnce()
    {
        using var registry = new IoPoolRegistry();
        var lane = registry.GetSerialLane("pg:Postgres:givenup", registry.Get(IoPoolNames.PostgresWriteBudgetPrefix + "Postgres"));
        var head = new ReplaySubject<int>();
        IDisposable? headWrite = null;
        try
        {
            headWrite = lane.Run(ParkedLeaf).Subscribe(head);
            var delete = lane.Run(_ => Task.FromResult(2), isDelete: true).Subscribe(_ => { }, _ => { });
            await LaneReading(lane, r => r.InFlight == 1 && r.WaitingDeletes == 1)
                .Should().Within(Bound).Emit("the delete waits behind the parked head");

            delete.Dispose();
            var reading = lane.Read();
            reading.WaitingDeletes.Should().Be(0,
                "🚨 a delete given up while queued will never run, so it must not keep earning queue credit");
            reading.Waiting.Should().Be(0);
            MeshExtensions.EarnsQueueCredit(MeshExtensions.WriteLaneAdmissions(registry), MeshExtensions.WriteLaneAdmissions(registry))
                .Should().BeFalse("no delete waits any more");
        }
        finally
        {
            Volatile.Write(ref _release, 1);
        }
        await head.Should().Within(Bound).Emit("the head completes once released");
        lane.Read().Waiting.Should().Be(0, "the given-up write left the counters once, not twice");
        headWrite?.Dispose();
    }

    private static readonly AsyncLocal<string?> Caller = new();

    [Fact]
    public async Task AQueuedWrite_RunsAndIsAnsweredUnderItsOwnCallersIdentity()
    {
        using var registry = new IoPoolRegistry();
        var lane = registry.GetSerialLane("pg:Postgres:identity", registry.Get(IoPoolNames.PostgresWriteBudgetPrefix + "Postgres"));
        string? leafSaw = null, observerSaw = null;
        var head = new ReplaySubject<int>();
        var queued = new ReplaySubject<string?>();
        IDisposable? headWrite = null, queuedWrite = null;
        try
        {
            Caller.Value = "first";
            headWrite = lane.Run(ParkedLeaf).Subscribe(head);
            Caller.Value = "second";
            queuedWrite = lane.Run(_ => { leafSaw = Caller.Value; return Task.FromResult(2); })
                .Select(_ => { observerSaw = Caller.Value; return observerSaw; })
                .Subscribe(queued);
            Caller.Value = null;
        }
        finally
        {
            Volatile.Write(ref _release, 1);
        }

        await queued.Should().Within(Bound).Emit("the queued write runs after the head");
        leafSaw.Should().Be("second", "🚨 the queued leaf must run under its OWN caller's identity, not the preceding write's");
        observerSaw.Should().Be("second", "and its caller must be answered under that identity");
        headWrite?.Dispose();
        queuedWrite?.Dispose();
    }

    [Fact]
    public async Task GivingUpARunningWrite_DoesNotRunItsCancellationOnTheDisposingThread()
    {
        using var registry = new IoPoolRegistry();
        var lane = registry.GetSerialLane("pg:Postgres:offthread", registry.Get(IoPoolNames.PostgresWriteBudgetPrefix + "Postgres"));
        var cancelledOn = 0;
        var outcome = new ReplaySubject<int>();
        var write = lane.Run(ct =>
            {
                ct.Register(() => Volatile.Write(ref cancelledOn, Environment.CurrentManagedThreadId));
                return ParkedLeaf(ct);
            })
            .Subscribe(outcome);
        try
        {
            await LaneReading(lane, r => r.InFlight == 1).Should().Within(Bound).Emit("the write is running");
            var disposingThread = Environment.CurrentManagedThreadId;
            write.Dispose();
            Volatile.Read(ref cancelledOn).Should().NotBe(disposingThread,
                "🚨 the leaf's cancellation callbacks (a database cancel request) must not run on the thread giving up");
            await LaneReading(lane, r => r.InFlight == 0).Should().Within(Bound)
                .Emit("the cancelled write still unwinds and releases the lane");
            Volatile.Read(ref cancelledOn).Should().NotBe(0, "the write was cancelled");
            Volatile.Read(ref cancelledOn).Should().NotBe(disposingThread);
        }
        finally
        {
            Volatile.Write(ref _release, 1);
        }
    }
}
