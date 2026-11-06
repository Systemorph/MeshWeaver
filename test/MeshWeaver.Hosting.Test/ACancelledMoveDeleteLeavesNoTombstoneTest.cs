using System;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Testing.FaultInjection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>No tombstone outlives a row that still exists</b> — the other half of
/// <see cref="AMovedAwayAddressIsGoneForGoodTest"/>.
///
/// <para>The move's delete leg tombstones the source paths it removes. It first set those marks AHEAD of
/// its <c>DeleteMany</c>, and the move's own deadline (<c>Timeout(moveDeadline, …)</c>) ends a stalled
/// leg by DISPOSING it, with no error passing through the leg. So when the move timed out with the batch
/// held before it deleted, every mark stood on rows that were still stored: reads answered
/// <c>DeleteInProgress</c> and saves were dropped — and the source's hub LATCHED that verdict in its
/// own-node cache, so even withdrawing the mark did not bring the row back (measured while fixing the
/// review of #7930). A mark is now set only from the batch's own verdict (see
/// <c>MeshExtensions.TombstonedBatchDelete</c> for why the change feed cannot be the signal).</para>
///
/// <para><b>The forced interleaving:</b> <see cref="FaultInjectingStorageAdapter.HoldDeletes"/> holds
/// the source's delete after every check and before the commit, and the move's budget is short, so the
/// move's deadline disposes the held batch. The surviving source must then read as present, well inside
/// the tombstone's 30 s lifetime (which is why the bound below is a literal below it: an unfixed run
/// would otherwise pass once the stale mark simply expired).</para>
///
/// <para><b>Controls:</b> the same hold RELEASED inside the budget lets the move finish, and the source
/// reads absent — a real delete keeps its tombstone. And a removal that lands after the
/// cancellation, followed by a re-create, leaves the replacement readable and unmarked.</para>
/// </summary>
public class ACancelledMoveDeleteLeavesNoTombstoneTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(6);

    /// <summary>The move's verdict arrives by its deadline; the slack covers its propagation.</summary>
    private static readonly TimeSpan Window = Budget + TimeSpan.FromSeconds(8);

    /// <summary>
    /// How long the surviving source may take to read as present after the verdict. Deliberately a
    /// literal BELOW <see cref="RecentlyDeletedRegistry"/>'s 30 s tombstone lifetime: a bound at or above
    /// the lifetime would let a stale mark expire into a pass.
    /// </summary>
    private static readonly TimeSpan ReconcileBound = TimeSpan.FromSeconds(10);

    private FaultInjectingStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<FaultInjectingStorageAdapter>();

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    private RecentlyDeletedRegistry Registry => Mesh.ServiceProvider.GetRequiredService<RecentlyDeletedRegistry>();

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder.AddFaultInjectingStorage())
            .ConfigureServices(services => services.AddSingleton(new MeshOperationOptions { Timeout = Budget }));

    [Fact(Timeout = 60_000)]
    public async Task AMoveWhoseDeleteLegTimesOutHeld_LeavesTheSurvivingSourceReadable()
    {
        var ct = TestContext.Current.CancellationToken;
        var (source, target) = await Seed("held", ct);

        using var hold = Storage.HoldDeletes(source);
        var response = await Move(source, target, ct);
        response.Success.Should().BeFalse("the source's delete is held past the move's deadline");
        (await hold.Arrivals.FirstAsync().Should().Within(TestTimeouts.Convergence)
            .Emit("the premise: the delete leg reached the store and was held there", ct))
            .Should().NotBeNull();

        var read = await ReadUntil(source, NodeReadStatus.Present, ct);
        read.Node!.Name.Should().Be("source", "the surviving source is served, not hidden as DeleteInProgress");
        Registry.IsRecentlyDeleted(source).Should().BeFalse("no tombstone may outlive a row that still exists");
    }

    /// <summary>The control: the same hold released inside the budget — the move finishes and its marks stand.</summary>
    [Fact(Timeout = 60_000)]
    public async Task AMoveWhoseHeldDeleteIsReleased_LeavesTheSourceGone()
    {
        var ct = TestContext.Current.CancellationToken;
        var (source, target) = await Seed("released", ct);

        var hold = Storage.HoldDeletes(source);
        var answered = Move(source, target, ct);
        await hold.Arrivals.FirstAsync().Should().Within(TestTimeouts.Convergence)
            .Emit("the delete leg reached the store and was held there", ct);
        hold.Release();

        var response = await answered;
        response.Success.Should().BeTrue(response.Error ?? "the released delete lets the move finish");
        var read = await ReadUntil(source, NodeReadStatus.Absent, NodeReadStatus.DeleteInProgress, ct);
        read.Node.Should().BeNull();
        Registry.IsRecentlyDeleted(source).Should().BeTrue("a removed source keeps its tombstone");
    }

    /// <summary>
    /// A removal the cancelled leg never heard of, followed by a durable RE-CREATE, while the change feed
    /// is held so the late <c>Deleted</c> is delivered only after the re-create superseded any tombstone.
    /// The replacement must read as present and carry no mark. When the leg tombstoned from the change
    /// feed, that late <c>Deleted</c> marked the replacement absent (review of #7930): nothing ordered a
    /// feed event against the registry's supersede. The leg now tombstones only from its batch's verdict,
    /// which a disposed batch never delivers.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ALateDeleteThenARecreate_LeavesTheReplacementReadable()
    {
        var ct = TestContext.Current.CancellationToken;
        var (source, target) = await Seed("recreated", ct);

        using (Storage.HoldDeletes(source))
        {
            var response = await Move(source, target, ct);
            response.Success.Should().BeFalse("the source's delete is held past the move's deadline");
        }

        // `using var` so a failing or cancelled assertion can never leave the hold registered; the
        // explicit Release below stays the ordered, early one (Release is idempotent).
        using var feed = Storage.HoldChangeFeed();
        try
        {
            // A removal that commits after the cancellation, beneath every guard…
            (await Storage.Inner.DeleteIfExists(source).Should().Within(TestTimeouts.Convergence)
                .Emit("the late delete commits", ct)).Should().BeTrue();
            // …and then a durable re-create of the same path, which supersedes any tombstone.
            await Access.RunAsSystem(() => NodeFactory.CreateNode(MeshNode.FromPath(source) with
                {
                    NodeType = "Markdown", Name = "replacement", State = MeshNodeState.Active,
                }))
                .Should().Within(TestTimeouts.Convergence).Emit("the replacement is created", ct);
            await feed.Arrivals.Where(a => a.Contains("Deleted", StringComparison.Ordinal))
                .Should().Within(TestTimeouts.Convergence)
                .Emit("the premise: the late Deleted is queued in the held feed, behind the re-create", ct);
        }
        finally
        {
            // Delivers the queued notifications, in order and synchronously: the late Deleted
            // reaches every feed subscriber only now, after the re-create's supersede.
            feed.Release();
        }

        Registry.IsRecentlyDeleted(source).Should().BeFalse(
            "an older delete must never tombstone a newer write over it");
        (await Storage.Read(source, Mesh.JsonSerializerOptions).Should().Within(TestTimeouts.Convergence)
            .Emit("the store answers", ct))!.Name.Should().Be("replacement", "the replacement is what is stored");
    }

    /// <summary>
    /// 🚨 The removals the batch CONFIRMED are retired even when the tombstone's presence probe outlives the
    /// move's deadline (review of #7930). The probe is a batched read held past the deadline
    /// (<see cref="FaultInjectingStorageAdapter.DelayNextBatchReadAnswer"/>, armed while the delete itself is
    /// held, so it captures the probe that follows the delete). The source row is gone, so the hub
    /// activated at the source must be retired — on the leg's own verdict, not behind the probe the
    /// deadline cancels. The probe never answered, so no tombstone is set (the conservative direction).
    /// Fails when retirement runs downstream of the probe, as it did on a9e375572b (measured with the
    /// leg's retirement moved behind the probe: the source's hub was never retired).
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AProbeOutlastingTheDeadline_StillRetiresWhatTheBatchRemoved()
    {
        var ct = TestContext.Current.CancellationToken;
        var (source, target) = await Seed("probed", ct);
        await Access.RunAsSystem(() => Mesh.GetMeshNodeStream(source)).Where(n => n?.Name == "source")
            .Should().Within(TestTimeouts.Convergence).Emit("reading the source activates its hub", ct);
        var owner = Mesh.GetHostedHub(new Address(source), HostedHubCreation.Never);
        owner.Should().NotBeNull("the premise: the source has a live hub for the move to retire");

        using var deletes = Storage.HoldDeletes(source);
        var answered = Move(source, target, ct);
        await deletes.Arrivals.FirstAsync().Should().Within(TestTimeouts.Convergence)
            .Emit("the delete leg reached the store", ct);
        // Armed while the delete is held, so the next batch read naming the source is the probe that
        // follows the delete; it is answered only after the move's deadline (never, within this test).
        using var probe = Storage.DelayNextBatchReadAnswer(source);
        deletes.Release();

        var response = await answered;
        response.Success.Should().BeFalse("the presence probe is held past the move's deadline");
        await probe.Arrivals.FirstAsync().Should().Within(TestTimeouts.Convergence)
            .Emit("the premise: the held read was the leg's presence probe", ct);
        (await Storage.Read(source, Mesh.JsonSerializerOptions).Should().Within(TestTimeouts.Convergence)
            .Emit("the store answers", ct)).Should().BeNull("the premise: the batch removed the source row");

        await owner!.DisposalCompleted.FirstOrDefaultAsync().Should().Within(TestTimeouts.Convergence)
            .Emit("the batch confirmed the removal, so the source's hub is retired whatever the probe does", ct);
        Registry.IsRecentlyDeleted(source).Should().BeFalse(
            "the probe never answered, so the leg does not know nothing serves the path: no tombstone");
    }

    private async Task<(string Source, string Target)> Seed(string name, CancellationToken ct)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var source = $"{TestPartition}/{name}-{suffix}";
        await Access.RunAsSystem(() => NodeFactory.CreateNode(MeshNode.FromPath(source) with
            {
                NodeType = "Markdown", Name = "source", State = MeshNodeState.Active,
            }))
            .Should().Within(TestTimeouts.Convergence).Emit("the source exists first", ct);
        return (source, $"{TestPartition}/{name}-landed-{suffix}");
    }

    private Task<MoveNodeResponse> Move(string source, string target, CancellationToken ct)
        => ObserveNodeOperation(new MoveNodeRequest(source, target))
            .Select(d => d.Message)
            .Do(r => Output.WriteLine($"MOVE {source} -> {target}: success={r.Success} reason={r.RejectionReason} error={r.Error}"))
            .Should().Within(Window).Emit("the move reaches a verdict by its deadline", ct);

    private Task<NodeReadOutcome> ReadUntil(string path, NodeReadStatus wanted, CancellationToken ct)
        => ReadUntil(path, wanted, wanted, ct);

    private Task<NodeReadOutcome> ReadUntil(string path, NodeReadStatus wanted, NodeReadStatus alsoWanted, CancellationToken ct)
    {
        var reader = Mesh.GetHostedHub(new Address("cancelled-move-reader", "shared"), c => c);
        return Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Select(_ => Access.RunAsSystem(() =>
                reader.GetMeshNodeOutcome(path, TimeSpan.FromSeconds(5), ReadTimeoutBehavior.EmitNull)))
            .Concat()
            .Do(o => Output.WriteLine(
                $"READ {path}: {o.Status} marked={Registry.IsRecentlyDeleted(path)} {o.Failure?.Message}"))
            .Where(o => o.Status == wanted || o.Status == alsoWanted)
            .FirstAsync()
            .Should().Within(ReconcileBound).Emit($"'{path}' reads {wanted}", ct);
    }
}
