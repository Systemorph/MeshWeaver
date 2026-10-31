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
        // `using` as well as the explicit Dispose below: a failing precondition must still dispose
        // the registry (its budget pool owns a canceller thread). Dispose is idempotent.
        using var registry = new IoPoolRegistry();
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
    public async Task ALaneAskedForAfterTheRegistryIsDisposed_RefusesWork_AndIsNotKept()
    {
        var registry = new IoPoolRegistry();
        var budget = registry.Get(IoPoolNames.PostgresWriteBudgetPrefix + "Postgres");
        registry.Dispose();

        var lane = registry.GetSerialLane("pg:Postgres:late", budget);
        registry.SerialLaneSnapshot().Should().BeEmpty("🚨 a closed registry must not be repopulated by a lane nobody will dispose");

        var outcome = new ReplaySubject<int>();
        using var write = lane.Run(_ => Task.FromResult(1)).Subscribe(outcome);
        (await Fault(outcome).Should().Within(Bound).Emit("a refused lane answers every write instead of running it"))
            .Should().BeOfType<ObjectDisposedException>();
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
        var registered = 0;
        var outcome = new ReplaySubject<int>();
        var write = lane.Run(ct =>
            {
                ct.Register(() => Volatile.Write(ref cancelledOn, Environment.CurrentManagedThreadId));
                // Signalled only AFTER the callback is installed, so the dispose below cancels an
                // installed callback — never one registered after an early cancel, which would run
                // inline on the leaf's thread and pass for the wrong reason.
                Volatile.Write(ref registered, 1);
                return ParkedLeaf(ct);
            })
            .Subscribe(outcome);
        try
        {
            await Observable.Interval(TimeSpan.FromMilliseconds(5)).StartWith(0L)
                .Where(_ => Volatile.Read(ref registered) == 1)
                .Should().Within(Bound).Emit("the leaf has installed its cancellation callback");
            var disposingThread = Environment.CurrentManagedThreadId;
            write.Dispose();
            Volatile.Read(ref cancelledOn).Should().NotBe(disposingThread,
                "🚨 the leaf's cancellation callbacks (a database cancel request) must not run on the thread giving up");
            await LaneReading(lane, r => r.InFlight == 0).Should().Within(Bound)
                .Emit("the cancelled write still unwinds and releases the lane");
            // Only "the cancel ran" is asserted here: once Dispose has returned, the disposing thread is
            // a pool thread like any other and may legitimately be the one the scheduled cancel lands on.
            // The off-thread property is the assertion made immediately after Dispose, above.
            Volatile.Read(ref cancelledOn).Should().NotBe(0, "the write was cancelled");
        }
        finally
        {
            Volatile.Write(ref _release, 1);
        }
    }
    [Fact]
    public async Task DrainAll_LetsHealthyQueuedWritesFinish_AndRefusesLaterOnes()
    {
        using var registry = new IoPoolRegistry();
        var lane = registry.GetSerialLane("pg:Postgres:drain", registry.Get(IoPoolNames.PostgresWriteBudgetPrefix + "Postgres"));
        var outcomes = Enumerable.Range(0, 4).Select(_ => new ReplaySubject<int>()).ToArray();
        var writes = new List<IDisposable>();
        var drained = new ReplaySubject<(int Leaked, int CancelledLanes)>();
        var late = new ReplaySubject<int>();
        try
        {
            writes.Add(lane.Run(ParkedLeaf).Subscribe(outcomes[0]));
            for (var i = 1; i < outcomes.Length; i++)
            {
                var value = i;
                writes.Add(lane.Run(_ => Task.FromResult(value)).Subscribe(outcomes[i]));
            }
            await LaneReading(lane, r => r.InFlight == 1 && r.Waiting == outcomes.Length - 1)
                .Should().Within(Bound).Emit("one write runs and three wait on the same lane");

            // DrainAll blocks (it joins), so it runs off the test thread while the head is released.
            writes.Add(Observable.Start(() =>
                {
                    var leaked = registry.DrainAll(out _, out var cancelled);
                    return (leaked, cancelled.Count(c => c.Pool.StartsWith("lane:", StringComparison.Ordinal)));
                }, System.Reactive.Concurrency.TaskPoolScheduler.Default)
                .Subscribe(drained));
            await Observable.Interval(TimeSpan.FromMilliseconds(5)).StartWith(0L)
                .Where(_ => lane.IsDraining)
                .Should().Within(Bound).Emit("the drain has closed the lane while the head is still parked");
            writes.Add(lane.Run(_ => Task.FromResult(9)).Subscribe(late));
        }
        finally
        {
            Volatile.Write(ref _release, 1);
        }

        var result = await drained.Should().Within(Bound).Emit("the drain completes once the lane empties");
        result.CancelledLanes.Should().Be(0, "a lane that kept advancing is not reported as cancelled");
        result.Leaked.Should().Be(0);
        for (var i = 0; i < outcomes.Length; i++)
            (await outcomes[i].Should().Within(Bound)
                .Emit($"🚨 write {i}, queued before the drain, must FINISH — not be refused when the pool closes"))
                .Should().Be(i == 0 ? 1 : i);
        (await Fault(late).Should().Within(Bound).Emit("a write subscribed after the drain began is refused, not run"))
            .Should().BeOfType<OperationCanceledException>();
        foreach (var write in writes)
            write.Dispose();
    }
    [Fact]
    public async Task DrainAll_WithAStuckHead_EndsAfterOneGrace_HoweverManyLateWritesArrive()
    {
        var grace = TimeSpan.FromMilliseconds(400);
        using var registry = new IoPoolRegistry(new IoPoolOptions { DrainGrace = grace });
        var lane = registry.GetSerialLane("pg:Postgres:latearrivals", registry.Get(IoPoolNames.PostgresWriteBudgetPrefix + "Postgres"));
        var writes = new System.Collections.Concurrent.ConcurrentQueue<IDisposable>();
        var drained = new ReplaySubject<int>();
        try
        {
            writes.Enqueue(lane.Run(ParkedLeaf).Subscribe(_ => { }, _ => { }));
            await LaneReading(lane, r => r.InFlight == 1).Should().Within(Bound).Emit("the head is stuck");

            writes.Enqueue(Observable.Start(() => registry.DrainAll(), System.Reactive.Concurrency.TaskPoolScheduler.Default)
                .Subscribe(drained));
            // A steady stream of late writes for as long as the drain runs.
            writes.Enqueue(Observable.Interval(TimeSpan.FromMilliseconds(50))
                .TakeUntil(drained)
                .Subscribe(_ => writes.Enqueue(lane.Run(_ => Task.FromResult(0)).Subscribe(_ => { }, _ => { }))));

            await drained.Should().Within(TimeSpan.FromSeconds(20)).Emit(
                "🚨 late arrivals behind a stuck head are refused, not progress — they must not keep restarting the grace");
        }
        finally
        {
            Volatile.Write(ref _release, 1);
            foreach (var write in writes.ToArray())
                write.Dispose();
        }
    }

    [Fact]
    public async Task DrainAll_WithManyStalledLanes_SpendsOneGrace_NotOnePerLane()
    {
        const int laneCount = 20;
        var grace = TimeSpan.FromMilliseconds(500);
        using var registry = new IoPoolRegistry(new IoPoolOptions { DrainGrace = grace, PostgresWrite = laneCount });
        var budget = registry.Get(IoPoolNames.PostgresWriteBudgetPrefix + "Postgres");
        var writes = new List<IDisposable>();
        try
        {
            for (var i = 0; i < laneCount; i++)
            {
                var lane = registry.GetSerialLane($"pg:Postgres:stalled{i}", budget);
                writes.Add(lane.Run(ParkedLeaf).Subscribe(_ => { }, _ => { }));
                writes.Add(lane.Run(_ => Task.FromResult(0)).Subscribe(_ => { }, _ => { }));
            }
            await Observable.Interval(TimeSpan.FromMilliseconds(10)).StartWith(0L)
                .Where(_ => registry.SerialLaneSnapshot().Count(l => l.InFlight == 1) == laneCount)
                .Should().Within(Bound).Emit("every lane's head is stuck");

            var elapsed = await Observable.Start(() =>
                {
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    registry.DrainAll();
                    return clock.Elapsed;
                }, System.Reactive.Concurrency.TaskPoolScheduler.Default)
                .Should().Within(Bound).Emit("the drain completes");
            elapsed.Should().BeLessThan(TimeSpan.FromTicks(grace.Ticks * laneCount / 2),
                "🚨 stalled lanes are watched together under ONE grace — a grace per lane would take {0}",
                TimeSpan.FromTicks(grace.Ticks * laneCount));
        }
        finally
        {
            Volatile.Write(ref _release, 1);
            foreach (var write in writes)
                write.Dispose();
        }
    }
    [Fact]
    public async Task DrainAll_ADrainedLanesLateRefusals_DoNotKeepAStuckLaneWaiting()
    {
        var grace = TimeSpan.FromMilliseconds(400);
        using var registry = new IoPoolRegistry(new IoPoolOptions { DrainGrace = grace });
        var budget = registry.Get(IoPoolNames.PostgresWriteBudgetPrefix + "Postgres");
        var stuck = registry.GetSerialLane("pg:Postgres:stuck", budget);
        var idle = registry.GetSerialLane("pg:Postgres:idle", budget);
        var writes = new System.Collections.Concurrent.ConcurrentQueue<IDisposable>();
        var drained = new ReplaySubject<int>();
        try
        {
            writes.Enqueue(stuck.Run(ParkedLeaf).Subscribe(_ => { }, _ => { }));
            await LaneReading(stuck, r => r.InFlight == 1).Should().Within(Bound).Emit("lane A's head is stuck");

            writes.Enqueue(Observable.Start(() => registry.DrainAll(), System.Reactive.Concurrency.TaskPoolScheduler.Default)
                .Subscribe(drained));
            // Late writes on the drained lane B, each refused — and each refusal ends a turn on B.
            writes.Enqueue(Observable.Interval(TimeSpan.FromMilliseconds(20))
                .TakeUntil(drained)
                .Subscribe(_ => writes.Enqueue(idle.Run(_ => Task.FromResult(0)).Subscribe(_ => { }, _ => { }))));

            await drained.Should().Within(TimeSpan.FromSeconds(20)).Emit(
                "🚨 refusals on an already-drained lane are not progress — they must not restart the grace for the stuck lane");
        }
        finally
        {
            Volatile.Write(ref _release, 1);
            foreach (var write in writes.ToArray())
                write.Dispose();
        }
    }
    [Fact]
    public async Task ADeleteWaitingForASaturatedBudget_IsNotAStalledExecutingDelete()
    {
        using var registry = new IoPoolRegistry(new IoPoolOptions { PostgresWrite = 1 });
        var budget = registry.Get(IoPoolNames.PostgresWriteBudgetPrefix + "Postgres");
        var holder = registry.GetSerialLane("pg:Postgres:holder", budget);
        var waiter = registry.GetSerialLane("pg:Postgres:waiter", budget);
        var writes = new List<IDisposable>();
        try
        {
            writes.Add(holder.Run(ParkedLeaf).Subscribe(_ => { }, _ => { }));
            await LaneReading(holder, r => r.InFlight == 1).Should().Within(Bound).Emit("the holder takes the only budget slot");
            writes.Add(waiter.Run(_ => Task.FromResult(2), isDelete: true).Subscribe(_ => { }, _ => { }));
            await LaneReading(waiter, r => r.AwaitingBudget && r.AwaitingDelete)
                .Should().Within(Bound).Emit("the delete's turn has come, and it waits for a budget slot");

            var reading = waiter.Read();
            reading.InFlight.Should().Be(0, "a delete waiting for a slot is not executing");
            reading.HeadIsDelete.Should().BeFalse();
            MeshExtensions.EarnsQueueCredit(MeshExtensions.WriteLaneAdmissions(registry), MeshExtensions.WriteLaneAdmissions(registry))
                .Should().BeTrue("🚨 the delete waits on a budget held by another lane's non-delete write — waiting its turn");
        }
        finally
        {
            Volatile.Write(ref _release, 1);
            foreach (var write in writes)
                write.Dispose();
        }
    }

    [Fact]
    public async Task ALaneCreatedWhileADrainRuns_IsClosed()
    {
        var grace = TimeSpan.FromMilliseconds(400);
        using var registry = new IoPoolRegistry(new IoPoolOptions { DrainGrace = grace });
        var budget = registry.Get(IoPoolNames.PostgresWriteBudgetPrefix + "Postgres");
        var stuck = registry.GetSerialLane("pg:Postgres:draining", budget);
        var writes = new List<IDisposable>();
        var drained = new ReplaySubject<int>();
        try
        {
            writes.Add(stuck.Run(ParkedLeaf).Subscribe(_ => { }, _ => { }));
            await LaneReading(stuck, r => r.InFlight == 1).Should().Within(Bound).Emit("a lane is stuck, so the drain waits");
            writes.Add(Observable.Start(() => registry.DrainAll(), System.Reactive.Concurrency.TaskPoolScheduler.Default).Subscribe(drained));
            await Observable.Interval(TimeSpan.FromMilliseconds(5)).StartWith(0L)
                .Where(_ => stuck.IsDraining).Should().Within(Bound).Emit("the drain has begun");

            var late = registry.GetSerialLane("pg:Postgres:newduringdrain", budget);
            late.IsDraining.Should().BeTrue("🚨 a lane created while a drain runs must be closed, not run writes outside it");
            var outcome = new ReplaySubject<int>();
            writes.Add(late.Run(_ => Task.FromResult(1)).Subscribe(outcome));
            (await Fault(outcome).Should().Within(Bound).Emit("its writes are refused"))
                .Should().BeOfType<OperationCanceledException>();
        }
        finally
        {
            Volatile.Write(ref _release, 1);
            foreach (var write in writes)
                write.Dispose();
        }
    }
}
