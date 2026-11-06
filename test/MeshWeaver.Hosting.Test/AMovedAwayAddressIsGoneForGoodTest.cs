using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>A move leaves its source address GONE FOR GOOD, exactly as a delete does</b> — the move's
/// delete leg tombstones every path it removes in the mesh's <see cref="RecentlyDeletedRegistry"/>.
///
/// <para><b>The defect.</b> <c>HandleDeleteNodeRequest</c> marks every path it deletes in the
/// registry before it removes anything, and three things read that mark: the dying owner's
/// abandoned-delivery NACK (<see cref="IAddressTombstones"/> — "the address will not reactivate",
/// an authoritative <see cref="ErrorType.NotFound"/>, instead of the transient
/// <see cref="ErrorType.ShuttingDown"/>), the per-node read path (<c>DeleteInProgress</c>), and the
/// "delete wins" guards that drop a resurrecting save. The MOVE's delete leg removed the source rows
/// with <c>IStorageAdapter.DeleteMany</c> and marked nothing, so a read that reached the source's
/// owner while it was being retired was told "shutting down, ask again" about an address that will
/// never come back — the reader rode the teardown out and spent its budget (#1029, re-entered
/// through the move). Measured in MeshWeaver.Plugins CI (run 42223717088,
/// <c>CrossPartitionMoveCopyAccessContextTest.Move_SubtreeCrossPartition_AllDescendantsMoveAndSourceIsGone</c>):
/// the move answered "Moved", every target node was readable at once, and the source root stayed
/// unread-as-absent for the test's whole 20 s wait.</para>
///
/// <para><b>The forced interleaving</b> (the pattern of <see cref="AReadOfARecreateReachingTheDyingOwnerTest"/>):
/// the source's owner hosts a child whose Quiescing drain owes a reply the test sends only in a
/// <c>finally</c>, so once the move retires the owner it stays hosted and refusing — past
/// <see cref="MessageHubRunLevel.DisposeHostedHubs"/>, where it answers deliveries only with a NACK —
/// until the test lets go. A read issued in that window reaches the dying owner deterministically.</para>
///
/// <para><b>Negative control in the same class:</b> the identical interleaving with a DELETE in place
/// of the move is answered absent — it is the contract the move has to meet, and it proves the
/// harness can see a correct verdict. <b>Fails on unfixed code:</b> the move variant's read is
/// answered <c>ShuttingDown</c> on every probe and settles <see cref="NodeReadStatus.Unavailable"/>
/// when its budget runs out.</para>
/// </summary>
public class AMovedAwayAddressIsGoneForGoodTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Accepted and answered only when the test releases it — parks one pending callback on its sender.</summary>
    private record HoldRequest : IRequest<HoldResponse>;

    private record HoldResponse;

    /// <summary>
    /// How long the teardown hold may last: longer than the whole test, for BOTH budgets that could end
    /// it without the test — the child's quiesce drain and its request timeout.
    /// </summary>
    private static readonly TimeSpan HoldBudget = TimeSpan.FromSeconds(240);

    /// <summary>
    /// The read's own budget. Shorter than the assertion's bound, so an unfixed run fails on the read's
    /// verdict (Unavailable) rather than on the assertion timing out.
    /// </summary>
    private static readonly TimeSpan ReadBudget = TimeSpan.FromSeconds(10);

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    [Fact(Timeout = 120_000)]
    public Task AReadReachingTheMovedAwaySourcesDyingOwner_IsAnsweredAbsent()
        => ReadTheDyingOwnerAfter(
            TestContext.Current.CancellationToken,
            (source, suffix) => ObserveNodeOperation(
                    new MoveNodeRequest(source, $"{TestPartition}/landed-{suffix}"),
                    // The production shape (MeshOperations.Move): the move executes ON the source's hub.
                    o => o.WithTarget(new Address(source)))
                .Select(d => d.Message.Success
                    ? (string?)null
                    : d.Message.Error ?? "move failed"));

    /// <summary>The control: the same interleaving with a delete, whose tombstone is the contract.</summary>
    [Fact(Timeout = 120_000)]
    public Task AReadReachingTheDeletedNodesDyingOwner_IsAnsweredAbsent()
        => ReadTheDyingOwnerAfter(
            TestContext.Current.CancellationToken,
            (source, _) => Access.RunAsSystem(() => NodeFactory.DeleteNode(source))
                .Select(_ => (string?)null));

    private async Task ReadTheDyingOwnerAfter(CancellationToken ct, Func<string, string, IObservable<string?>> remove)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var source = $"{TestPartition}/gone-{suffix}";

        await Access.RunAsSystem(() => NodeFactory.CreateNode(MeshNode.FromPath(source) with
            {
                NodeType = "Markdown", Name = "source", State = MeshNodeState.Active,
            }))
            .Should().Within(TestTimeouts.Convergence).Emit("the source exists first", ct);
        await Access.RunAsSystem(() => Mesh.GetMeshNodeStream(source)).Where(n => n?.Name == "source")
            .Should().Within(TestTimeouts.Convergence).Emit("reading the node activates its owner", ct);
        var owner = Mesh.GetHostedHub(new Address(source), HostedHubCreation.Never);
        owner.Should().NotBeNull("the premise: the read activated the source's owner");

        // Hold the owner in teardown: a child whose Quiescing drain owes a reply that only the test sends.
        var holdEntered = new ReplaySubject<IMessageDelivery>(1);
        var holder = Mesh.GetHostedHub(
            new Address("moved-away-hold", suffix),
            c => c.WithTypes(typeof(HoldRequest), typeof(HoldResponse))
                .WithHandler<HoldRequest>((_, d) =>
                {
                    holdEntered.OnNext(d);
                    return d.Processed();
                }));
        var child = owner!.GetHostedHub(new Address($"{source}/_hold"),
            c => c.WithTypes(typeof(HoldRequest), typeof(HoldResponse))
                .WithQuiesceTimeout(HoldBudget)
                .WithRequestTimeout(HoldBudget));
        using var held = child.Observe<HoldResponse>(new HoldRequest(), o => o.WithTarget(holder.Address))
            .Subscribe(_ => { }, ex => Output.WriteLine($"hold ended: {ex.GetType().Name}"));
        var owed = await holdEntered.Should().Within(TestTimeouts.Convergence)
            .Emit("the child owes a reply before the source is removed", ct);
        var teardownReleased = 0;
        void ReleaseTeardown()
        {
            if (Interlocked.Exchange(ref teardownReleased, 1) == 0)
                holder.Post(new HoldResponse(), o => o.ResponseFor(owed!));
        }

        try
        {
            var error = await remove(source, suffix)
                .Should().Within(TestTimeouts.Convergence).Emit("the source is removed", ct);
            error.Should().BeNull("nothing objects to removing the source");

            await owner.RunLevelChanged.StartWith(owner.RunLevel)
                .Where(level => level >= MessageHubRunLevel.DisposeHostedHubs)
                .Should().Within(TestTimeouts.Convergence).Emit(
                    "the premise: the removal retires the owner, which now only NACKs what reaches it", ct);

            var reader = Mesh.GetHostedHub(new Address("moved-away-reader", suffix), c => c);
            var outcome = await Access.RunAsSystem(() =>
                    reader.GetMeshNodeOutcome(source, ReadBudget, ReadTimeoutBehavior.EmitNull))
                .Should().Within(TestTimeouts.Convergence).Emit("the read reaches a verdict", ct);
            (owner.RunLevel < MessageHubRunLevel.Dead).Should().BeTrue(
                "the premise: the read was answered while the owner was still tearing down");
            (outcome.Status is NodeReadStatus.Absent or NodeReadStatus.DeleteInProgress).Should().BeTrue(
                $"the read settled {outcome.Status}, but the node left this address for good, so the dying owner must say so — 'shutting down, "
                + $"ask again' makes the reader spend its whole budget on an address that never comes back "
                + $"(error: {outcome.Failure?.Message})");
        }
        finally
        {
            ReleaseTeardown();
        }
    }
}
