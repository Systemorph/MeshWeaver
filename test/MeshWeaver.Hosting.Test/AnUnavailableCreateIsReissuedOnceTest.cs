using System;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using MeshWeaver.Testing.FaultInjection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// #7380 — a create refused as <c>Unavailable</c> (the store could not be reached) is recorded as a
/// durable intent in the <see cref="PendingWriteLedger"/>, and the platform re-issues it ONCE, as
/// the original writer, when the store answers again.
///
/// <para><b>The isolated fault.</b> <see cref="FaultInjectingStorageAdapter.RefuseWritesAsUnreachable"/>
/// on the ONE path under test: a driver connection failure (SQLSTATE 08006), so the create handler's
/// own store-unreachable arm answers <c>Unavailable</c>, the production answer. Releasing the switch is
/// the store coming back; the declared change-feed gap is what a PostgreSQL LISTEN reconnect
/// announces after a restart, and it is the only trigger: nothing here waits on a clock.</para>
/// </summary>
public class AnUnavailableCreateIsReissuedOnceTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Writer = "pending-write-writer";

    private FaultInjectingStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<FaultInjectingStorageAdapter>();

    private PendingWriteLedger Ledger => Mesh.ServiceProvider.GetRequiredService<PendingWriteLedger>();

    /// <summary>The injector goes in BEFORE the base adds in-memory persistence.</summary>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder.AddFaultInjectingStorage());

    private static string NewPath() => $"{TestPartition}/pw{Guid.NewGuid().ToString("N")[..10]}";

    private static MeshNode NodeAt(string path) => MeshNode.FromPath(path) with
    {
        NodeType = "Markdown",
        Name = "A create the store did not see",
        State = MeshNodeState.Active,
    };

    /// <summary>The create, posted as <see cref="Writer"/>, answered — materialised so a refusal can be read.</summary>
    private Task<Notification<MeshNode>> CreateAsWriter(string path) =>
        Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .RunAs(new AccessContext { ObjectId = Writer, Name = "Pending Write Writer" },
                () => NodeFactory.CreateNode(NodeAt(path)))
            .Materialize()
            .Where(n => n.Kind != NotificationKind.OnCompleted)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the create is answered", TestContext.Current.CancellationToken);

    private Task<MeshNode?> Stored(string path) =>
        Storage.Read(path, Mesh.JsonSerializerOptions)
            .Take(1).DefaultIfEmpty(null)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the store answers the read", TestContext.Current.CancellationToken);

    private Task<PendingWrite> Recorded(string path) =>
        Observable.Interval(TimeSpan.FromMilliseconds(20)).StartWith(0L)
            .Select(_ => Ledger.Pending.FirstOrDefault(w => string.Equals(w.Path, path, StringComparison.OrdinalIgnoreCase)))
            .Where(w => w is not null)
            .Select(w => w!)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the refused create is recorded as a pending write", TestContext.Current.CancellationToken);

    private void TheStoreAnswersAgain() =>
        Storage.DeliverChangeFeedGap(new ChangeFeedGap(
            "postgresql:mesh_node_changes",
            DateTimeOffset.UtcNow.AddSeconds(-40),
            DateTimeOffset.UtcNow,
            "the managed database restarted (#7380)"));

    private async Task RefusedAsUnavailable(string path)
    {
        var answer = await CreateAsWriter(path);
        answer.Kind.Should().Be(NotificationKind.OnError, "the store could not be reached");
        answer.Exception!.Data[NodeCreationFailure.RejectionReasonKey]
            .Should().Be(NodeCreationRejectionReason.Unavailable, "an unreachable store is not a verdict");
    }

    /// <summary>Store down → refused → store back → the write lands, once, as its writer.</summary>
    [Fact(Timeout = 120_000)]
    public async Task ARefusedCreate_LandsOnce_WhenTheStoreAnswersAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = NewPath();
        var refusal = Storage.RefuseWritesAsUnreachable(path);
        try
        {
            await RefusedAsUnavailable(path);
            var pending = await Recorded(path);
            pending.CreatedBy.Should().Be(Writer, "the intent remembers who was refused");
            pending.Writer!.ObjectId.Should().Be(Writer);
            (await Stored(path)).Should().BeNull("nothing was written while the store was away");
        }
        finally
        {
            refusal.Release();
        }

        var settled = Ledger.Outcomes.Where(o => o.Write.Path == path)
            .Should().Within(TestTimeouts.CrossSilo).Emit("the pending write is re-issued", ct);
        TheStoreAnswersAgain();
        var outcome = await settled;

        outcome.Verdict.Should().Be(PendingWriteVerdict.Landed, outcome.Detail ?? "");
        var landed = await Stored(path);
        landed.Should().NotBeNull("the re-issue landed");
        landed!.CreatedBy.Should().Be(Writer, "the re-issue ran as the original writer, not as the platform");
        Ledger.Pending.Should().NotContain(w => w.Path == path, "a settled intent is gone from the ledger");

        // Once: further evidence re-issues nothing for this path.
        var again = Ledger.Outcomes.Where(o => o.Write.Path == path)
            .Should().NotEmit(TestTimeouts.Quick, "a pending write is re-issued once", ct);
        TheStoreAnswersAgain();
        await again;
        (await Stored(path))!.Version.Should().Be(landed.Version, "nothing wrote the node a second time");
    }

    /// <summary>Refused again on the re-issue → reported by name and dropped, never looped.</summary>
    [Fact(Timeout = 120_000)]
    public async Task ACreateRefusedAgain_IsReportedAndDropped_NeverLooped()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = NewPath();
        var refusal = Storage.RefuseWritesAsUnreachable(path);
        try
        {
            await RefusedAsUnavailable(path);
            await Recorded(path);

            // Evidence arrives while THIS path's writes are still refused: the one re-issue is refused.
            var dropped = Ledger.Outcomes.Where(o => o.Write.Path == path)
                .Should().Within(TestTimeouts.CrossSilo).Emit("the pending write is re-issued", ct);
            TheStoreAnswersAgain();
            var outcome = await dropped;
            outcome.Verdict.Should().Be(PendingWriteVerdict.Dropped, "a re-issue refused again is reported and dropped");
            outcome.Detail.Should().Contain(nameof(NodeCreationRejectionReason.Unavailable));
            outcome.Write.WriterName.Should().Be(Writer, "the report names whose write was dropped");
            Ledger.Pending.Should().NotContain(w => w.Path == path,
                "the refused re-issue is not recorded a second time");
        }
        finally
        {
            refusal.Release();
        }

        // Not looped: the store is back and more evidence arrives, and nothing re-issues it.
        var looped = Ledger.Outcomes.Where(o => o.Write.Path == path)
            .Should().NotEmit(TestTimeouts.Quick, "a dropped write is never re-issued", ct);
        TheStoreAnswersAgain();
        await looped;
        (await Stored(path)).Should().BeNull("the dropped write was not retried");
    }

    /// <summary>The journal makes the intent durable: a ledger over the same directory loads it.</summary>
    [Fact(Timeout = 60_000)]
    public async Task APendingWrite_SurvivesInTheJournal_UntilItIsSettled()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"mw-pending-writes-{Guid.NewGuid():N}");
        try
        {
            var path = NewPath();
            var write = new PendingWrite(NodeAt(path), Writer, new AccessContext { ObjectId = Writer },
                DateTimeOffset.UtcNow, "DbException: injected");
            using (var before = new PendingWriteLedger(directory, IoPool.Unbounded))
                await before.Record(write, Mesh.JsonSerializerOptions)
                    .Should().Within(TestTimeouts.Convergence).Emit("the intent is journalled", ct);

            using var after = new PendingWriteLedger(directory, IoPool.Unbounded);
            var loaded = await after.LoadJournal(Mesh.JsonSerializerOptions)
                .Should().Within(TestTimeouts.Convergence).Emit("the journal is read", ct);
            loaded.Should().Be(1, "a process that starts finds the intent recorded before it");
            var restored = after.TakeAll().Single();
            restored.Path.Should().Be(path);
            restored.CreatedBy.Should().Be(Writer);
            restored.Writer!.ObjectId.Should().Be(Writer);

            await after.Settle(new PendingWriteOutcome(restored, PendingWriteVerdict.Landed, null))
                .Should().Within(TestTimeouts.Convergence).Emit("the intent settles", ct);
            Directory.EnumerateFiles(directory).Should().BeEmpty("a settled intent leaves no journal entry");
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// The re-issue replays the refused request AS RECORDED — its initialization argument and its
    /// authorship source included (review of #7586). Rebuilt from the node and requester alone, it
    /// would land without the initialization message and report Landed, or re-stamp a preserving
    /// copy with the mover's authorship.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task TheReissue_CarriesTheRefusedRequestsArgumentAndAuthorship()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"mw-pending-writes-{Guid.NewGuid():N}");
        try
        {
            var write = new PendingWrite(NodeAt(NewPath()), Writer, new AccessContext { ObjectId = Writer },
                DateTimeOffset.UtcNow, "DbException: injected", AuthorshipFrom: "source/original", Argument: "init-payload");
            using (var before = new PendingWriteLedger(directory, IoPool.Unbounded))
                await before.Record(write, Mesh.JsonSerializerOptions)
                    .Should().Within(TestTimeouts.Convergence).Emit("the intent is journalled", ct);
            using var after = new PendingWriteLedger(directory, IoPool.Unbounded);
            await after.LoadJournal(Mesh.JsonSerializerOptions)
                .Should().Within(TestTimeouts.Convergence).Emit("the journal is read", ct);

            var request = PendingWriteRecovery.ReissueRequest(after.TakeAll().Single());
            request.ReissuedAfterUnavailable.Should().BeTrue();
            request.AuthorshipFrom.Should().Be("source/original", "a preserving copy keeps its source's authorship");
            request.Argument.Should().NotBeNull("the initialization message is delivered on the re-issue too");
            request.Argument!.ToString().Should().Contain("init-payload");
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// A journal entry that cannot be removed is reported and left in place, and the settlement still
    /// answers — it never faults, which would end the process-wide recovery watcher (review of #7586).
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ASettlementWhoseJournalCannotBeCleaned_DoesNotFault()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"mw-pending-writes-{Guid.NewGuid():N}");
        try
        {
            using var ledger = new PendingWriteLedger(directory, IoPool.Unbounded);
            var write = new PendingWrite(NodeAt(NewPath()), Writer, new AccessContext { ObjectId = Writer },
                DateTimeOffset.UtcNow, "DbException: injected");
            await ledger.Record(write, Mesh.JsonSerializerOptions)
                .Should().Within(TestTimeouts.Convergence).Emit("the intent is journalled", ct);
            // A directory whose entries cannot be removed: the journal delete fails.
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);

            var outcome = new PendingWriteOutcome(write, PendingWriteVerdict.Landed, null);
            var faulted = await ledger.Settle(outcome).Materialize().Take(1)
                .Should().Within(TestTimeouts.Convergence).Emit("the raw settlement answers", ct);
            faulted.Kind.Should().Be(NotificationKind.OnError, "CONTROL: the journal delete really fails here");

            var settled = await PendingWriteRecovery.SettleIsolated(ledger, outcome, null).Materialize().Take(1)
                .Should().Within(TestTimeouts.Convergence).Emit("the isolated settlement answers", ct);
            settled.Kind.Should().Be(NotificationKind.OnNext, "a cleanup failure must not fault the recovery");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}

/// <summary>
/// #7380 negative control: a create refused for PERMISSION is a verdict. It is never recorded as a
/// pending write and never re-issued, however often the store answers. Built on
/// <see cref="MonolithMeshTestBase.ConfigureMeshBase"/>, so the default public-admin grant does not
/// make the writer an admin.
/// </summary>
public class ARefusedForPermissionCreateIsNeverReissuedTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private FaultInjectingStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<FaultInjectingStorageAdapter>();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder.AddFaultInjectingStorage());

    [Fact(Timeout = 120_000)]
    public async Task ACreateRefusedForPermission_IsNeverReissued()
    {
        var ct = TestContext.Current.CancellationToken;
        var ledger = Mesh.ServiceProvider.GetRequiredService<PendingWriteLedger>();
        var path = $"{TestPartition}/pw{Guid.NewGuid().ToString("N")[..10]}";

        var answer = await Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .RunAs(new AccessContext { ObjectId = "no-grants-anywhere", Name = "No Grants" },
                () => NodeFactory.CreateNode(MeshNode.FromPath(path) with
                {
                    NodeType = "Markdown",
                    Name = "A create nobody granted",
                    State = MeshNodeState.Active,
                }))
            .Materialize()
            .Where(n => n.Kind != NotificationKind.OnCompleted)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the create is answered", ct);
        answer.Kind.Should().Be(NotificationKind.OnError, "the writer holds no grant");
        answer.Exception!.Data[NodeCreationFailure.RejectionReasonKey]
            .Should().NotBe(NodeCreationRejectionReason.Unavailable, "a permission refusal is a verdict");
        ledger.Pending.Should().NotContain(w => w.Path == path, "a verdict is never recorded as a pending write");

        var reissued = ledger.Outcomes.Where(o => o.Write.Path == path)
            .Should().NotEmit(TestTimeouts.Quick, "a refusal for permission is never re-issued", ct);
        Storage.DeliverChangeFeedGap(new ChangeFeedGap(
            "postgresql:mesh_node_changes", DateTimeOffset.UtcNow.AddSeconds(-5), DateTimeOffset.UtcNow,
            "negative control (#7380)"));
        await reissued;
        (await Storage.Read(path, Mesh.JsonSerializerOptions).Take(1).DefaultIfEmpty(null)
                .Should().Within(TestTimeouts.Convergence).Emit("the store answers the read", ct))
            .Should().BeNull("nothing re-issued the refused create");
    }
}
