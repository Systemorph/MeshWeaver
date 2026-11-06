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
/// review of #7930). A mark is now set only once a removal is known: from the store's change feed at the
/// commit, or from the batch's own report.</para>
///
/// <para><b>The forced interleaving:</b> <see cref="FaultInjectingStorageAdapter.HoldDeletes"/> holds
/// the source's delete after every check and before the commit, and the move's budget is short, so the
/// move's deadline disposes the held batch. The surviving source must then read as present, well inside
/// the tombstone's 30 s lifetime (which is why the bound below is a literal below it: an unfixed run
/// would otherwise pass once the stale mark simply expired).</para>
///
/// <para><b>Controls:</b> the same hold RELEASED inside the budget lets the move finish, and the source
/// reads absent — a real delete keeps its tombstone. And a delete that lands AFTER the
/// cancellation is tombstoned again from the change feed.</para>
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

    /// <summary>A delete that lands AFTER the cancellation is tombstoned again, from the change feed.</summary>
    [Fact(Timeout = 60_000)]
    public async Task ADeleteLandingAfterTheCancellation_IsTombstoned()
    {
        var ct = TestContext.Current.CancellationToken;
        var (source, target) = await Seed("late", ct);

        using (var hold = Storage.HoldDeletes(source))
        {
            var response = await Move(source, target, ct);
            response.Success.Should().BeFalse("the source's delete is held past the move's deadline");
            await ReadUntil(source, NodeReadStatus.Present, ct);
        }

        // The commit that was already on its way lands now, beneath every guard.
        (await Storage.Inner.DeleteIfExists(source).Should().Within(TestTimeouts.Convergence)
            .Emit("the late delete commits", ct)).Should().BeTrue();
        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Select(_ => Registry.IsRecentlyDeleted(source))
            .Where(marked => marked)
            .FirstAsync()
            .Should().Within(ReconcileBound).Emit(
                "a source row removed after the cancellation is gone for good, and is marked so", ct);
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
