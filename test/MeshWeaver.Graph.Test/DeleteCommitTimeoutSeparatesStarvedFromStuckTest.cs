using System;
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
/// Systemorph/MeshWeaver#1198, the LAST item — a commit-stage delete timeout said the drain "made
/// no progress" and could not say whether the drain was STUCK or had simply never been ADMITTED.
///
/// <para><b>The two production lines this is about</b>, memex-cloud, verbatim:</para>
/// <code>
/// 2026-09-06 [DeleteNode:commit] the bottom-up delete of
///     'OpenStreetMap/Gallery/_Access/Public_Access' did not drain within 30s
///     — 0 of 1 planned path(s) were already removed from storage
/// 2026-09-14 [DeleteNode:commit] the bottom-up delete of 'Hosting/TriageStatus' made no progress
///     for 30s — 3 path(s) removed from storage so far
/// </code>
///
/// <para><b>Why neither could be acted on.</b> "No progress for 30 s" has two causes that call for
/// opposite responses: the store took the call and went silent (look at the store, or at that
/// node's hub), or the next leaf removal never got an I/O pool slot because unrelated writes were
/// ahead of it — the cap-1 <c>pg:{provider}</c> WRITE pool is ONE process-wide gate shared by every
/// partition, so a delete queues behind writes it has nothing to do with, and those do not tick
/// THIS delete's progress. The issue narrowed to exactly that and stopped, for a year of calendar
/// and eight comments, because the report named neither and the queue was not instrumented.
/// <c>IIoPool.QueueWait</c> / <c>CurrentlyWaiting</c> (#4460) built the instrument and no consumer,
/// so the reading still could not be taken at the moment it decides anything — which is this one.</para>
///
/// <para>🚨 <b>The two answers are not symmetric and this test pins both.</b> "Nothing was queued"
/// is CONCLUSIVE — work that is not waiting for a slot was not starved by a cap — while "these
/// pools had work queued" is a lead. A report that only ever produced the second sentence would
/// look like an instrument and decide nothing, which is the defect class this whole issue is made
/// of. So the subject here is that the SAME failure produces DIFFERENT sentences in the two states,
/// and each case is the other's negative control.</para>
///
/// <para><b>The repro.</b> A single node whose storage delete never answers — the
/// <c>0 of 1 planned</c> shape of the 2026-09-06 occurrence exactly, with no fan-out to confuse the
/// attribution. Deleted twice: once with every pool idle, once with a cap-1 pool deliberately
/// occupied so one admission is queued. Nothing is widened and nothing is raced — the watchdog's
/// own budget ends both waits, and the assertions are about what it SAYS.</para>
///
/// <para>Against <c>main</c> both messages are byte-identical past the path: the commit timeout
/// carries no pool reading at all, so the first case's assertion that the report states nothing was
/// queued fails outright, and no wording of the second could have distinguished them.</para>
/// </summary>
public class DeleteCommitTimeoutSeparatesStarvedFromStuckTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string StuckId = "commit-idle-pools";
    private const string StarvedId = "commit-queued-pool";

    /// <summary>
    /// A <c>pg:</c> name, so <c>IoPoolOptions.MaxConcurrencyFor</c> gives it the SAME cap 1 the real
    /// Postgres write pool has — the shape under test — while the suffix keeps it off every name a
    /// storage backend would resolve. Nothing else in the mesh touches it, so its queue depth is
    /// this test's and only this test's.
    /// </summary>
    private const string ParkedPoolName = "pg:1198-parked";

    /// <summary>
    /// The one configured value; every nested rung derives from it. Small enough that two timed-out
    /// deletes fit well inside the class's soft deadline, large enough to be a real wait.
    /// </summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Bound on the test's own wait for the park to take effect. It is not a budget for the
    /// SUBJECT — it bounds a setup step, and blowing it fails the test loudly rather than letting
    /// the "starved" case run against an idle pool and pass for the wrong reason.
    /// </summary>
    private static readonly TimeSpan ParkEstablished = TimeSpan.FromSeconds(10);

    private readonly LatentDeleteStorageAdapter storage = new(new InMemoryStorageAdapter());

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder.ConfigureServices(services =>
        {
            services.AddSingleton<IStorageAdapter>(storage);
            services.AddSingleton(new MeshOperationOptions { Timeout = Budget });
            return services;
        }));

    [Fact]
    public async Task AStalledCommit_SaysWhetherAnythingWasWaitingForAPoolSlot()
    {
        var registry = Mesh.ServiceProvider.GetRequiredService<IoPoolRegistry>();

        // ───────────────────────── case 1: STUCK — every pool idle ─────────────────────────
        var stuckPath = await SeedSingleNode(StuckId);
        storage.LatencyRoot = stuckPath;
        storage.StallAfterDeletes = 0;

        var stuck = await DeleteAndCaptureFailure(stuckPath,
            "the store took the delete and never answered, so the no-progress watchdog must end it");
        Output.WriteLine("STUCK: " + stuck.Message);

        // POSITIVE CONTROL — this really is the commit stage's no-progress watchdog and not some
        // earlier stage running out of time. Without it every assertion below could be satisfied by
        // a different failure that happens to mention a pool.
        stuck.Message.Should().Contain("[DeleteNode:commit]",
            "the subject is the COMMIT stage — the pre-flight is a sibling report about a "
            + "different failure");
        stuck.Message.Should().Contain("made no progress for",
            "the bound is a no-progress watchdog, not a total-duration cap (#3392) — a report "
            + "reading 'did not drain within' would mean this test measures the wrong mechanism");
        stuck.Message.Should().Contain("0 of 1 planned path(s) removed",
            "this is the 2026-09-06 occurrence's shape exactly: one planned path, none removed, no "
            + "fan-out — so nothing but the store can be what went silent");

        // THE SUBJECT, half one. The conclusive answer: nothing was waiting for a slot, so no cap
        // starved this drain and an operator can stop looking at the pools.
        stuck.Message.Should().Contain(IoPoolQueueReport.NothingQueued,
            "with every pool idle the report must SAY so — that is the half of the reading that is "
            + "conclusive, and against main the line carries no pool reading at all");

        // NEGATIVE CONTROL — the reading was actually TAKEN. "Nothing was queued" and "nobody
        // asked" are different sentences precisely so a registry that could not be resolved cannot
        // masquerade as a clean reading; if this fires, the assertion above passed vacuously.
        stuck.Message.Should().NotContain(IoPoolQueueReport.NotMeasured,
            "the mesh registers IoPoolRegistry, so a report claiming it was not measured means the "
            + "reading never happened and the clean answer above is worth nothing");
        // 🚨 The PREFIX, not the shared phrase. Both outcomes say "work queued at that moment" —
        // they answer the same question — so a search for that phrase matches either and settles
        // nothing. Only the prefix separates them.
        stuck.Message.Should().NotContain(IoPoolQueueReport.QueuedPrefix,
            "no pool held queued work here — naming one would send an operator to investigate a "
            + "gate that was wide open");

        // ─────────────────── case 2: STARVED — a cap-1 pool has work queued ───────────────────
        var pool = registry.Get(ParkedPoolName);

        // POSITIVE CONTROL for the snapshot itself — the enumeration sees a pool that EXISTS, with
        // the cap the `pg:` prefix gives it. Case 1's "nothing was queued" is only worth something
        // if Snapshot() can return a pool at all; this is where that is established.
        registry.Snapshot().Should().ContainSingle(r => r.Name == ParkedPoolName,
                "the registry must enumerate the pool that was just resolved — a snapshot that "
                + "returns nothing would make every 'nothing was queued' reading vacuous")
            .Which.MaxConcurrency.Should().Be(1,
                "the `pg:` prefix caps a pool at one in-flight operation, which is the shape of the "
                + "real process-wide Postgres write gate this case stands in for");

        // 🚨 The release INTO the parked worker is a volatile int polled under a BOUNDED
        // SpinWait.SpinUntil and written in a `finally` — never a SemaphoreSlim, a
        // ManualResetEvent or a TaskCompletionSource used as a gate. The cancellation arm is what
        // keeps mesh teardown honest: a drain cancels the leaf even if an assertion throws before
        // the finally runs.
        var release = 0;
        IObservable<int> Park() => pool.InvokeBlocking(ct =>
        {
            SpinWait.SpinUntil(
                () => Volatile.Read(ref release) != 0 || ct.IsCancellationRequested,
                TestTimeouts.Convergence);
            return 0;
        });

        // The first leaf takes the pool's single slot; the second reaches the admission point
        // behind it and is therefore QUEUED, which is what CurrentlyWaiting counts.
        using var holding = Park().Subscribe(_ => { }, _ => { });
        using var queued = Park().Subscribe(_ => { }, _ => { });

        try
        {
            SpinWait.SpinUntil(() => pool.CurrentlyWaiting > 0, ParkEstablished);
            pool.CurrentlyWaiting.Should().BeGreaterThan(0,
                "the second leaf must be queued behind the first before the delete runs — "
                + "otherwise the 'starved' case is measuring an idle pool and would pass for "
                + "exactly the wrong reason");

            var starvedPath = await SeedSingleNode(StarvedId);
            storage.LatencyRoot = starvedPath;

            var starved = await DeleteAndCaptureFailure(starvedPath,
                "the store is silent for this subtree too, so the same watchdog must end this one");
            Output.WriteLine("STARVED: " + starved.Message);

            // Same stage, same shape — only the pool state differs. Asserted so the contrast below
            // is between two readings of ONE report, not between two different failures.
            starved.Message.Should().Contain("[DeleteNode:commit]",
                "both cases must reach the same bound, or the two messages are not comparable");
            starved.Message.Should().Contain("0 of 1 planned path(s) removed",
                "the drain made the same (zero) progress in both cases — the pool reading is the "
                + "only thing that may differ");

            // THE SUBJECT, half two. The pool is NAMED, with the pair that makes a depth mean
            // something: a cap and a queue.
            starved.Message.Should().Contain(IoPoolQueueReport.QueuedPrefix,
                "this is the outcome that names pools, and the prefix is the only thing that "
                + "distinguishes it from the clean reading — see the note in case 1");
            starved.Message.Should().Contain(ParkedPoolName,
                "a queued pool must be named — 'something was waiting' identifies nothing, which "
                + "is the failure mode every earlier round of this issue produced");
            starved.Message.Should().Contain($"{ParkedPoolName}(cap 1)",
                "a depth is meaningless without the cap beside it: '1 waiting' says nothing until "
                + "you know the pool runs one at a time");
            starved.Message.Should().Contain("waiting",
                "the reading is the queue DEPTH, not merely that a pool exists");

            // NEGATIVE CONTROL — the two states really do produce different sentences. Against main
            // both messages are identical past the path, so this is the assertion that fails there,
            // and it is what makes the pair an instrument rather than a decoration.
            starved.Message.Should().NotContain(IoPoolQueueReport.NothingQueued,
                "a queued pool reported as 'nothing was queued' would be the conclusive half of "
                + "the reading asserting the opposite of the truth — strictly worse than silence");
            starved.Message.Should().NotBe(stuck.Message,
                "the whole point is that a stuck drain and a starved one no longer look the same "
                + "to whoever reads the line");
        }
        finally
        {
            // Written in the finally so a failing assertion above cannot strand the pool's leaves.
            Volatile.Write(ref release, 1);
        }
    }

    private async Task<string> SeedSingleNode(string id)
    {
        await NodeFactory.CreateNode(
                new MeshNode(id, TestPartition) { Name = id, NodeType = "Markdown" })
            .Should().Within(TestTimeouts.Convergence).Emit(
                cancellationToken: TestContext.Current.CancellationToken);
        return $"{TestPartition}/{id}";
    }

    /// <summary>
    /// Producer → test signal: the delete's ERROR arm completes an <see cref="AsyncSubject{T}"/>
    /// the assertion helpers await. A success emission leaves it empty and times the wait out,
    /// which is itself the failure this test must report — never a <c>.Wait()</c> on it.
    /// </summary>
    private async Task<Exception> DeleteAndCaptureFailure(string path, string because)
    {
        var failure = new AsyncSubject<Exception>();
        using var deleting = NodeFactory.DeleteNode(path).Subscribe(
            _ => { },
            ex =>
            {
                failure.OnNext(ex);
                failure.OnCompleted();
            });

        return await failure.Should().Within(TestTimeouts.WriteConvergence).Emit(
            because, cancellationToken: TestContext.Current.CancellationToken);
    }
}
