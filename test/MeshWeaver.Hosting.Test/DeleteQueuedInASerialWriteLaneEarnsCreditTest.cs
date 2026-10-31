using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Threading;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// MeshWeaver#7464 — a delete's no-progress watchdog must see a delete WAITING in a
/// <see cref="SerialWriteLane"/>. The Postgres backend writes each partition schema through one, so
/// a delete leaf queued behind a write parked on a database lock waits in a lane that does not move.
/// Before the lanes were read, that delete earned no queue credit and failed its budget for a wait
/// it was not responsible for.
///
/// <para>The parked head is a real leaf on the real budget pool, released through a volatile flag
/// polled under a bounded <see cref="SpinWait.SpinUntil(Func{bool}, TimeSpan)"/> and written in a
/// <c>finally</c>.</para>
/// </summary>
public class DeleteQueuedInASerialWriteLaneEarnsCreditTest
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private int _release;

    private Task<int> ParkedLeaf(CancellationToken ct) => Task.Run(() =>
    {
        SpinWait.SpinUntil(() => Volatile.Read(ref _release) == 1 || ct.IsCancellationRequested, Bound);
        return 1;
    }, CancellationToken.None);

    private static Task<int> Instant(CancellationToken _) => Task.FromResult(1);

    private static IObservable<SerialWriteLaneReading> LaneReading(SerialWriteLane lane, Func<SerialWriteLaneReading, bool> state) =>
        Observable.Interval(TimeSpan.FromMilliseconds(10)).StartWith(0L)
            .Select(_ => lane.Read())
            .Where(state);

    [Fact]
    public async Task ADeleteQueuedBehindAParkedWriteOfAnotherKind_EarnsCredit()
    {
        using var registry = new IoPoolRegistry();
        var lane = registry.GetSerialLane("pg:Postgres:schemaa", registry.Get(IoPoolNames.PostgresWriteBudgetPrefix + "Postgres"));
        var head = new ReplaySubject<int>();
        var delete = new ReplaySubject<int>();
        IDisposable? headWrite = null, deleteWrite = null;
        try
        {
            headWrite = lane.Run(ParkedLeaf).Subscribe(head);
            deleteWrite = lane.Run(Instant, isDelete: true).Subscribe(delete);
            await LaneReading(lane, r => r.InFlight == 1 && r.WaitingDeletes == 1 && !r.HeadIsDelete)
                .Should().Within(Bound).Emit("the delete must be queued behind the parked non-delete write");

            var first = MeshExtensions.WriteLaneAdmissions(registry);
            var second = MeshExtensions.WriteLaneAdmissions(registry);
            MeshExtensions.EarnsQueueCredit(first, second).Should().BeTrue(
                "🚨 the delete is waiting its turn behind a write of another kind; without credit it fails its budget "
                + "for a wait outside it (MeshWeaver#7464)");
        }
        finally
        {
            Volatile.Write(ref _release, 1);
        }
        await delete.Should().Within(Bound).Emit("the queued delete runs once the head is released");
        headWrite?.Dispose();
        deleteWrite?.Dispose();
    }

    [Fact]
    public async Task ADeleteQueuedBehindAParkedDelete_EarnsNoCredit_ItMayBeItsOwnHungLeaf()
    {
        using var registry = new IoPoolRegistry();
        var lane = registry.GetSerialLane("pg:Postgres:schemab", registry.Get(IoPoolNames.PostgresWriteBudgetPrefix + "Postgres"));
        var head = new ReplaySubject<int>();
        var delete = new ReplaySubject<int>();
        IDisposable? headWrite = null, deleteWrite = null;
        try
        {
            headWrite = lane.Run(ParkedLeaf, isDelete: true).Subscribe(head);
            deleteWrite = lane.Run(Instant, isDelete: true).Subscribe(delete);
            await LaneReading(lane, r => r.InFlight == 1 && r.WaitingDeletes == 1 && r.HeadIsDelete)
                .Should().Within(Bound).Emit("a delete must be queued behind the parked delete");

            var first = MeshExtensions.WriteLaneAdmissions(registry);
            var second = MeshExtensions.WriteLaneAdmissions(registry);
            MeshExtensions.EarnsQueueCredit(first, second).Should().BeFalse(
                "a lane whose head is a delete that stood still may be this delete's own admitted leaf hanging — "
                + "the #1198 rule, unchanged");
        }
        finally
        {
            Volatile.Write(ref _release, 1);
        }
        await delete.Should().Within(Bound).Emit("the queued delete runs once the head is released");
        headWrite?.Dispose();
        deleteWrite?.Dispose();
    }

    [Fact]
    public void ADeleteWithNoWait_EarnsNoCredit_AsBefore()
    {
        using var registry = new IoPoolRegistry();
        var lane = registry.GetSerialLane("pg:Postgres:schemac", registry.Get(IoPoolNames.PostgresWriteBudgetPrefix + "Postgres"));
        lane.Read().Waiting.Should().Be(0);

        var first = MeshExtensions.WriteLaneAdmissions(registry);
        var second = MeshExtensions.WriteLaneAdmissions(registry);
        MeshExtensions.EarnsQueueCredit(first, second).Should().BeFalse(
            "nothing waits in any lane, so there is no queue wait to credit — the watchdog measures removals as before");
    }

    [Fact]
    public async Task AQueuedWrite_KeepsOrder_AndADisposedQueuedWrite_IsSkipped()
    {
        using var registry = new IoPoolRegistry();
        var lane = registry.GetSerialLane("pg:Postgres:schemad", registry.Get(IoPoolNames.PostgresWriteBudgetPrefix + "Postgres"));
        var order = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var head = new ReplaySubject<int>();
        var last = new ReplaySubject<int>();
        IDisposable? headWrite = null, lastWrite = null;
        try
        {
            headWrite = lane.Run(async ct => { var r = await ParkedLeaf(ct); order.Enqueue("head"); return r; }).Subscribe(head);
            var skipped = lane.Run(_ => { order.Enqueue("skipped"); return Task.FromResult(2); }).Subscribe(_ => { order.Enqueue("skipped-emitted"); });
            lastWrite = lane.Run(_ => { order.Enqueue("last"); return Task.FromResult(3); }).Subscribe(last);
            await LaneReading(lane, r => r.InFlight == 1 && r.Waiting == 2)
                .Should().Within(Bound).Emit("two writes must queue behind the parked head");
            skipped.Dispose();
        }
        finally
        {
            Volatile.Write(ref _release, 1);
        }
        await last.Should().Within(Bound).Emit("the last write runs after the head");
        order.Should().Equal(["head", "last"], "a lane runs its writes in order and skips one given up while queued");
        headWrite?.Dispose();
        lastWrite?.Dispose();
    }
}
