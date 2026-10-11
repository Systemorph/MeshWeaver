using System;
using System.Diagnostics;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Systemorph/MeshWeaver#1198 - a recursive delete's commit stage failed a leaf for 'no progress' while the leaf
/// was merely QUEUED behind a write lane that kept serving other writers.
///
/// <para><b>Production shape</b> (memex 2026-10-09): <c>[DeleteNode:commit] ... made no progress for 25s - 0 of 1
/// planned path(s) removed ... pg:Postgres(cap 1) 219 waiting, 1 in flight ... 1325 admission(s) waited >= 1 s
/// during this stage</c>. The cap-1 <c>pg:</c> write pool is one process-wide gate; a leaf's removal is one write on
/// it, so it cannot remove anything before its turn, and the commit's watchdog - which counts only this delete's
/// own removals - read the wait as a stall although the lane was advancing the whole time.</para>
///
/// <para><b>The repro.</b> A one-path delete whose store write is admitted through a cap-1 <c>pg:</c> pool that is
/// kept loaded with <see cref="Holders"/> unrelated writes of <see cref="HoldEach"/> each - about 7 s of queue
/// against a 4 s stage budget. The lane never stops: a slot is granted every 600 ms. Before the change the
/// watchdog fires at 4 s; after it, the lane's advance credits the watchdog and the delete completes in its turn.
/// The sibling test <c>DeleteCommitTimeoutSeparatesStarvedFromStuckTest</c> pins the other half: a leaf that is
/// granted and then hangs is still failed at one budget, naming the pool.</para>
///
/// <para><b>What "loaded" has to mean here.</b> A pool counts a leaf as WAITING from the moment it is accepted,
/// which is one ThreadPool hop before the leaf reaches the gate (<see cref="IoPoolAdmission.BeforeGate"/>), and
/// the gate is first-come-first-served among the leaves that have REACHED it - not among the ones accepted. So
/// <c>CurrentlyWaiting</c> says nothing about who is ahead of the delete: with the ThreadPool busy (this project
/// run whole, in one process) the delete's leaf reached the gate behind three of twelve accepted holders, waited
/// 1.8 s, and the test failed its own discriminator although the watchdog was never involved. The load is
/// therefore read where the order is decided (<see cref="IoPoolAdmission.BehindCap"/>), it is kept at depth
/// until the delete's leaf asks for its slot - however long the stages before the commit took - and the wait
/// the test compares with the budget is the leaf's own, measured by the store it queued in.</para>
///
/// <para><b>The queue ahead of the leaf is PINNED, not hoped for.</b> A replacement holder takes the same
/// ThreadPool hop as any other leaf, so "kept at depth" is itself only an accepted count. The instant the leaf
/// asks - before the pool has heard of it - the test reads how many holders are at the gate (all of them ahead
/// of the leaf, by construction) and stops the lane draining: a holder granted the slot keeps it. The lane is
/// released once nothing accepted is still before the gate, which is the leaf having reached the gate behind
/// exactly the holders that were counted. From there the lane moves at one grant per <see cref="HoldEach"/>,
/// which is the stretch the credit is about. The pause is one ThreadPool hop long and costs the watchdog
/// nothing it is not supposed to pay: a lane that stands still with work earns no credit.</para>
/// </summary>
public class DeleteCommitQueuedBehindAMovingLaneTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string NodeId = "commit-queued-lane";

    /// <summary>A pg: name, so the pool gets the same cap 1 as the real Postgres write pool.</summary>
    private const string LaneName = "pg:1198-moving-lane";

    private const int Holders = 16;

    /// <summary>
    /// How many holders must be AT THE GATE, behind the cap, both before the delete may start and at the instant
    /// its leaf asks for a slot: 9 x 600 ms = 5.4 s of queue ahead of the leaf against the 4 s budget. Well under
    /// <see cref="Holders"/> on purpose - around each grant one holder is between the gate and running, and a
    /// replacement needs a ThreadPool thread before it has a place in the queue.
    /// </summary>
    private const int QueuedAhead = 9;

    /// <summary>1 once the delete's leaf has asked for its slot: a holder that is granted the lane keeps it.</summary>
    private int pinned;

    /// <summary>1 once the watch for the leaf reaching the gate has been started.</summary>
    private int watching;

    /// <summary>Completed when the leaf has reached the gate; pinned holders let go on it.</summary>
    private readonly AsyncSubject<Unit> released = new();

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(4);

    private static readonly TimeSpan HoldEach = TimeSpan.FromMilliseconds(600);

    private readonly LatentDeleteStorageAdapter storage = new(new InMemoryStorageAdapter());

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder.ConfigureServices(services =>
        {
            services.AddSingleton<IStorageAdapter>(storage);
            services.AddSingleton(new MeshOperationOptions { Timeout = Budget });
            return services;
        }));

    [Fact(Timeout = 120000)]
    public async Task ADeleteQueuedBehindAMovingWriteLane_IsNotFailedForWaitingItsTurn()
    {
        var registry = Mesh.ServiceProvider.GetRequiredService<IoPoolRegistry>();
        var lane = registry.Get(LaneName);

        await NodeFactory.CreateNode(
                new MeshNode(NodeId, TestPartition) { Name = NodeId, NodeType = "Markdown" })
            .Should().Within(TestTimeouts.Convergence).Emit(
                cancellationToken: TestContext.Current.CancellationToken);
        var path = $"{TestPartition}/{NodeId}";
        storage.LatencyRoot = path;
        storage.DeleteLane = lane;

        // Unrelated writers: each holds the single slot for HoldEach, reactively (a timer, never a sleep),
        // so the lane keeps GRANTING slots while the delete's own write waits for its turn. Every holder that
        // finishes is replaced until the delete's leaf has asked for its slot, so the queue the leaf joins is
        // as deep as the one the precondition read, whatever the stages before the commit cost.
        using var holders = new CompositeDisposable();
        for (var i = 0; i < Holders; i++)
            Hold(lane, holders, replaceWhile: () => storage.LaneAsked == 0,
                afterHold: () => Volatile.Read(ref pinned) == 1 ? released.Select(_ => 0) : Observable.Return(0));

        // PIN the queue ahead of the leaf. Asking: the pool has not heard of the leaf, so every holder at the
        // gate is ahead of it - count them and stop the lane draining. Accepted: the leaf is counted by the
        // pool, so "nothing accepted is still before the gate" now means the leaf is AT the gate, behind them.
        var aheadAtAsk = -1;
        storage.OnLaneAsking = () =>
        {
            if (Interlocked.Exchange(ref pinned, 1) == 0)
                Volatile.Write(ref aheadAtAsk, BehindCap(registry, LaneName));
        };
        storage.OnLaneAccepted = () =>
        {
            if (Interlocked.Exchange(ref watching, 1) == 0)
                holders.Add(Observable.Interval(TimeSpan.FromMilliseconds(50))
                    .Where(_ => BeforeGate(registry, LaneName) == 0)
                    .Take(1)
                    .Subscribe(_ =>
                    {
                        released.OnNext(Unit.Default);
                        released.OnCompleted();
                    }));
        };

        SpinWait.SpinUntil(() => BehindCap(registry, LaneName) >= QueuedAhead, TestTimeouts.Convergence);
        var loaded = registry.Snapshot().First(reading => reading.Name == LaneName);
        Output.WriteLine($"LOADED: {loaded} - before the gate {loaded.Admission?.BeforeGate}, "
                         + $"behind the cap {loaded.Admission?.BehindCap}");
        (loaded.Admission?.BehindCap ?? 0).Should().BeGreaterThanOrEqualTo(QueuedAhead,
            "the holders must be AT THE GATE before the delete runs - a holder that was only accepted has no "
            + "place in the queue yet, and the delete's leaf would be served ahead of it");

        var deleted = await NodeFactory.DeleteNode(path).Should().Within(TestTimeouts.WriteConvergence).Emit(
            "a leaf queued behind a lane that keeps serving other writers is waiting its turn, not stuck",
            cancellationToken: TestContext.Current.CancellationToken);
        var waited = storage.LongestLaneWait;
        Output.WriteLine($"The leaf waited {waited.TotalSeconds:0.0}s for its slot, behind "
                         + $"{Volatile.Read(ref aheadAtAsk)} holder(s) that were at the gate when it asked");

        deleted.Should().BeTrue();
        Volatile.Read(ref aheadAtAsk).Should().BeGreaterThanOrEqualTo(QueuedAhead,
            "the queue AHEAD of the leaf is what makes it wait - counted at the gate the instant the leaf asked, "
            + "and pinned there until the leaf had joined it");
        waited.Should().BeGreaterThan(Budget,
            "the test only discriminates if the leaf really was queued for longer than the stage budget - "
            + $"it waited {waited.TotalSeconds:0.0}s for its slot against {Budget.TotalSeconds:0}s");
        (await storage.Inner.Exists(path).Should().Within(TestTimeouts.Convergence).Emit(
                cancellationToken: TestContext.Current.CancellationToken))
            .Should().BeFalse("the node must be gone");
    }

    /// <summary>
    /// One unrelated writer on <paramref name="lane"/>: it holds the slot for <see cref="HoldEach"/> once granted,
    /// then for as long as <paramref name="afterHold"/> takes to answer (not at all when it is null), and is
    /// replaced by another when it finishes for as long as <paramref name="replaceWhile"/> holds.
    /// </summary>
    private static void Hold(
        IIoPool lane, CompositeDisposable holders, Func<bool> replaceWhile, Func<IObservable<int>>? afterHold = null)
    {
        var holder = new SingleAssignmentDisposable();
        holders.Add(holder);
        holder.Disposable = lane
            .InvokeObservable(ct => Observable.Timer(HoldEach)
                .SelectMany(tick => afterHold?.Invoke() ?? Observable.Return(0)))
            .Subscribe(
                _ => { },
                _ => holders.Remove(holder),
                () =>
                {
                    holders.Remove(holder);
                    if (replaceWhile())
                        Hold(lane, holders, replaceWhile, afterHold);
                });
    }

    /// <summary>
    /// How many leaves the pool <paramref name="name"/> has accepted that have not reached its gate yet. Zero
    /// when the pool is unknown would read as "all at the gate", so an unread pool counts as one.
    /// </summary>
    private static int BeforeGate(IoPoolRegistry registry, string name)
        => registry.Snapshot().FirstOrDefault(reading => reading.Name == name).Admission?.BeforeGate ?? 1;

    /// <summary>
    /// How many leaves of the pool <paramref name="name"/> are at its gate with no permit - the part of its queue
    /// that is ordered, and therefore the only part a later arrival is certain to be behind.
    /// </summary>
    private static int BehindCap(IoPoolRegistry registry, string name)
        => registry.Snapshot().FirstOrDefault(reading => reading.Name == name).Admission?.BehindCap ?? 0;

    /// <summary>A second cap-1 pg: lane, unrelated to the delete, that keeps advancing with queued work.</summary>
    private const string UnrelatedLaneName = "pg:1198-unrelated-lane";

    /// <summary>The delete's own lane, in which its leaf is admitted and then hangs.</summary>
    private const string StuckLaneName = "pg:1198-stuck-lane";

    /// <summary>
    /// The other half of the credit's contract (Copilot review on #6403): a leaf that was ADMITTED to its write
    /// lane and then hung is stuck, not queued, and an UNRELATED lane that keeps advancing must not reset its
    /// watchdog. The leaf holds its cap-1 lane's only slot, so that lane stands still with work in it, and the
    /// delete still fails at one budget. Against the first version of the credit - any advancing cap-1 lane
    /// counted - the unrelated lane kept the watchdog alive for up to four budgets.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task ALeafAdmittedAndHungInItsLane_IsNotCreditedByAnUnrelatedLaneThatAdvances()
    {
        var registry = Mesh.ServiceProvider.GetRequiredService<IoPoolRegistry>();
        var stuckLane = registry.Get(StuckLaneName);
        var unrelatedLane = registry.Get(UnrelatedLaneName);

        const string id = "commit-admitted-hung";
        await NodeFactory.CreateNode(
                new MeshNode(id, TestPartition) { Name = id, NodeType = "Markdown" })
            .Should().Within(TestTimeouts.Convergence).Emit(
                cancellationToken: TestContext.Current.CancellationToken);
        var path = $"{TestPartition}/{id}";
        storage.LatencyRoot = path;
        storage.DeleteLane = stuckLane;
        storage.HangInsideLane = true;

        // The unrelated lane: queued work granted a slot every HoldEach for far longer than four budgets.
        const int unrelatedHolders = 40;
        using var holders = new CompositeDisposable();
        for (var i = 0; i < unrelatedHolders; i++)
            Hold(unrelatedLane, holders, replaceWhile: () => false);

        // At the gate, not merely accepted: only a lane with QUEUED work that advances can earn the credit.
        SpinWait.SpinUntil(() => BehindCap(registry, UnrelatedLaneName) >= QueuedAhead, TestTimeouts.Convergence);
        BehindCap(registry, UnrelatedLaneName).Should().BeGreaterThanOrEqualTo(QueuedAhead,
            "the unrelated lane must hold queued work, or nothing could have credited the watchdog");

        long failedAt = 0;
        var failure = new AsyncSubject<Exception>();
        using var deleting = NodeFactory.DeleteNode(path).Subscribe(
            _ => { },
            ex =>
            {
                // Stamped where the failure arrives, so the reading carries no continuation latency.
                Interlocked.Exchange(ref failedAt, Stopwatch.GetTimestamp());
                failure.OnNext(ex);
                failure.OnCompleted();
            });
        // POSITIVE CONTROL: the leaf really is admitted and holds the stuck lane's only slot - otherwise this
        // would measure a queued leaf, which the credit is allowed to cover.
        SpinWait.SpinUntil(() => stuckLane.CurrentInFlight == 1, TestTimeouts.Convergence);
        stuckLane.CurrentInFlight.Should().Be(1,
            "the leaf must be admitted to its lane and holding it before the watchdog fires");

        var stuck = await failure.Should().Within(TestTimeouts.WriteConvergence).Emit(
            "a leaf admitted to its lane and then silent must end the delete with the watchdog",
            cancellationToken: TestContext.Current.CancellationToken);
        // From the instant the leaf was ADMITTED, which is where "stuck" begins. Until then the leaf is queued
        // or on its way, the unrelated lane may rightly credit the watchdog, and the stages before the commit
        // are not the watchdog's to answer for - a clock started at the request charges all of that to it.
        var elapsed = Stopwatch.GetElapsedTime(storage.LaneAdmittedAt, Interlocked.Read(ref failedAt));
        Output.WriteLine($"STUCK {elapsed.TotalSeconds:0.0}s after the leaf was admitted: {stuck.Message}");

        stuck.Message.Should().Contain("made no progress for",
            "this must be the commit stage's no-progress watchdog");
        elapsed.Should().BeLessThan(Budget * 2,
            "an unrelated lane advancing must not credit a leaf that is stuck inside its own lane - "
            + $"the delete failed {elapsed.TotalSeconds:0.0}s after the leaf was admitted, against a "
            + $"{Budget.TotalSeconds:0}s budget");
    }
}
