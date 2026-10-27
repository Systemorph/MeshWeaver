using System;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Testing.FaultInjection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.FaultInjection.Test;

/// <summary>
/// 🚨 A move or a delete whose storage batch delete (<c>DeleteMany</c>) fails PART-WAY retires the
/// per-node hubs at exactly the paths that batch removed, and leaves every other hub alone
/// (MeshWeaver#7396, the follow-up of #7248 / #7171).
///
/// <para><b>The defect.</b> <c>DeleteMany</c> reported its removed set only when the whole batch
/// succeeded. A batch that removed some rows and then faulted gave its caller a bare exception, so
/// the move's delete leg and the delete commit's record-satellite batches retired nothing: the
/// hubs activated at the removed paths went on serving under addresses that name no node, the
/// moved-away-address shape of #7171, surviving into the failure path.</para>
///
/// <para><b>Isolated fault:</b> <see cref="FaultInjectingStorageAdapter.FailDeleteMany"/> lets the
/// batch remove every path but one and then fault, naming the rows it removed as the contract now
/// requires. Nothing else is perturbed.</para>
///
/// <para><b>Negative control in each fact:</b> the hub at the path the batch did NOT remove is
/// still the live hosted hub afterwards, so the fix is "retire what is gone", never "retire every
/// planned path".</para>
///
/// <para><b>Fails on unfixed code:</b> the removed path's hub never reaches
/// <c>DisposalCompleted</c>, and it is still hosted at its address after the operation answered.</para>
/// </summary>
public class APartWayBatchDeleteRetiresWhatItRemovedTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private FaultInjectingStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<FaultInjectingStorageAdapter>();

    /// <summary>The injector goes in BEFORE the base adds in-memory persistence.</summary>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder.AddFaultInjectingStorage());

    [Fact(Timeout = 120_000)]
    public async Task AMoveWhoseDeleteLegDiesPartWay_RetiresTheRemovedDescendants_NotTheSurvivingRoot()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid().ToString("N")[..8];
        var source = $"{TestPartition}/source-{id}";
        var target = $"{TestPartition}/target-{id}";
        var child = $"{source}/child";
        await Create(source, ct);
        await Create(child, ct);

        var childHub = await Activate(child, ct);
        var rootHub = await Activate(source, ct);

        MoveNodeResponse moved;
        using (var fault = Storage.FailDeleteMany(source))
        {
            moved = await ObserveNodeOperation(new MoveNodeRequest(source, target))
                .Select(d => d.Message)
                .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
            await fault.Arrivals.Should().Within(TestTimeouts.Convergence)
                .Emit("the delete leg's batch reached the fault", ct);
        }
        Output.WriteLine($"MOVE {source} -> {target}: success={moved.Success} reason={moved.RejectionReason} error={moved.Error}");

        moved.Success.Should().BeFalse("the delete leg failed part-way");
        (await StoredAt(child, ct)).Should().BeNull("PRECONDITION: the batch removed the child before it faulted");
        (await StoredAt(source, ct)).Should().NotBeNull("PRECONDITION: the batch stopped at the root");

        await childHub.DisposalCompleted.FirstOrDefaultAsync()
            .Should().Within(TestTimeouts.Convergence).Emit(
                "the batch removed the child's row, so the hub serving that address must go, as it does "
                + "when the whole batch succeeds; otherwise it serves under an address that names no node",
                cancellationToken: ct);
        Mesh.GetHostedHub(new Address(child), HostedHubCreation.Never).Should().BeNull(
            "nothing is hosted at an address whose row the batch removed");

        // 🚨 The control: the root's row survived the batch, so its hub stays exactly as it was.
        ((int)rootHub.RunLevel).Should().BeLessThan((int)MessageHubRunLevel.DisposeHostedHubs,
            "the batch did not remove the root, so its hub is not retired");
        Mesh.GetHostedHub(new Address(source), HostedHubCreation.Never).Should().BeSameAs(rootHub);
    }

    [Fact(Timeout = 120_000)]
    public async Task ADeleteWhoseRecordBatchDiesPartWay_RetiresTheRemovedRecords_NotTheSurvivingOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid().ToString("N")[..8];
        var root = $"{TestPartition}/records-{id}";
        var removedRecord = $"{root}/_Activity/compile-a";
        var survivingRecord = $"{root}/_Activity/compile-b";
        await Create(root, ct);
        var options = new JsonSerializerOptions();
        // Straight into the store of record: the delete plans its paths from storage, and these are
        // record satellites, which leave through DeleteMany batches rather than one hub each.
        await Storage.Inner.Write(Activity("compile-a", root), options)
            .Merge(Storage.Inner.Write(Activity("compile-b", root), options))
            .ToList()
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);

        var removedHub = await Activate(removedRecord, ct);
        var survivingHub = await Activate(survivingRecord, ct);

        DeleteNodeResponse deleted;
        using (var fault = Storage.FailDeleteMany(survivingRecord))
        {
            deleted = await ObserveNodeOperation(new DeleteNodeRequest(root))
                .Select(d => d.Message)
                .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
            await fault.Arrivals.Should().Within(TestTimeouts.Convergence)
                .Emit("the record batch reached the fault", ct);
        }
        Output.WriteLine($"DELETE {root}: success={deleted.Success} reason={deleted.RejectionReason} error={deleted.Error}");

        deleted.Success.Should().BeFalse("the record batch failed part-way");
        (await StoredAt(removedRecord, ct)).Should().BeNull("PRECONDITION: the batch removed this record before it faulted");
        (await StoredAt(survivingRecord, ct)).Should().NotBeNull("PRECONDITION: the batch stopped at this record");

        await removedHub.DisposalCompleted.FirstOrDefaultAsync()
            .Should().Within(TestTimeouts.Convergence).Emit(
                "the batch removed this record's row, so the hub serving it must go, exactly as it does "
                + "when the batch completes",
                cancellationToken: ct);
        Mesh.GetHostedHub(new Address(removedRecord), HostedHubCreation.Never).Should().BeNull(
            "nothing is hosted at an address whose row the batch removed");

        // 🚨 The control: the record the batch stopped at is still stored, and its hub is untouched.
        ((int)survivingHub.RunLevel).Should().BeLessThan((int)MessageHubRunLevel.DisposeHostedHubs,
            "the batch did not remove this record, so its hub is not retired");
        Mesh.GetHostedHub(new Address(survivingRecord), HostedHubCreation.Never).Should().BeSameAs(survivingHub);
    }

    private static MeshNode Activity(string id, string owner) => new(id, $"{owner}/_Activity")
    {
        Name = id,
        NodeType = "Activity",
        MainNode = owner,
        State = MeshNodeState.Active,
        Content = new ActivityLog(ActivityCategory.Compilation) { Id = id, Status = ActivityStatus.Succeeded },
    };

    private async Task<IMessageHub> Activate(string path, CancellationToken ct)
    {
        (await ReadNode(path).Timeout(ReadNodeTimeout).FirstAsync().Await(ct)).Should().NotBeNull(
            $"PRECONDITION: '{path}' is readable");
        var hub = Mesh.GetHostedHub(new Address(path), HostedHubCreation.Never);
        hub.Should().NotBeNull($"PRECONDITION: a read activates the hub at '{path}'");
        return hub!;
    }

    private Task<MeshNode?> StoredAt(string path, CancellationToken ct) =>
        Storage.Inner.Read(path, Mesh.JsonSerializerOptions)
            .DefaultIfEmpty(null)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);

    private Task<MeshNode> Create(string path, CancellationToken ct) =>
        NodeFactory.CreateNode(MeshNode.FromPath(path) with
            {
                Name = path,
                NodeType = "Markdown",
                State = MeshNodeState.Active,
            })
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
}
