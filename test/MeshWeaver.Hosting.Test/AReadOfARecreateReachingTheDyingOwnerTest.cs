using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
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

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// <b>A read of a recreated node that reaches the DELETED node's dying owner waits for the new owner —
/// it is never told the node will not come back.</b>
///
/// <para>The shape behind <c>InboxOpenApprovalsTest.APreferencesNodeDeletedAndRecreated_NeverEndsTheFeed</c>:
/// a node is deleted, which tears its owner down, and created again while that owner is still tearing
/// down. The store publishes the recreated row on its change feed INSIDE the commit, so a reader that
/// follows a listing opens the node's stream before the recreate has superseded the delete tombstone.
/// Its request is routed to the old owner, which is still hosted. That owner asks
/// <see cref="IAddressTombstones.IsDeleted"/> whether its address is gone for good; if it is told yes, it
/// answers "the node was deleted, so this address will not reactivate", the read ends, and the reader
/// (whose listing already said "exists") never asks again. Measured on the inbox test in about 1 run in
/// 200, with that exact answer in the log.</para>
///
/// <para>The verdict the dying owner must give is "shutting down, ask again": a recreate the storage
/// guard has ADMITTED is on its way, so the address is not gone for good
/// (<see cref="RecentlyDeletedRegistry.IsDeleted"/>). The reader rides the teardown out and is served
/// the recreate by the next activation.</para>
///
/// <para><b>The forced interleaving.</b> The owner hosts a child whose Quiescing drain owes a reply the
/// test answers only in a <c>finally</c>, with a quiesce budget longer than the test, so the delete leaves
/// the owner tearing down (refusing, still hosted) until the test lets go.
/// <see cref="FaultInjectingStorageAdapter.HoldWrites"/> holds the recreate after the guard admitted it
/// and before the store commits it, so the read is opened in exactly the window the inbox opened it in.
/// The owner's tombstone question is RECORDED, so the test waits for the refusal to happen and reads the
/// verdict it was given, rather than inferring either from elapsed time.</para>
/// </summary>
public class AReadOfARecreateReachingTheDyingOwnerTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Accepted and answered only when the test releases it — parks one pending callback on its sender.</summary>
    private record HoldRequest : IRequest<HoldResponse>;

    private record HoldResponse;

    /// <summary>
    /// The mesh's tombstone verdict, with every question about a watched path recorded with its answer.
    /// Delegates to the real registry, so the verdict under test is the production one.
    /// </summary>
    private sealed class RecordingTombstones(RecentlyDeletedRegistry inner) : IAddressTombstones
    {
        private string? watched;

        public ReplaySubject<bool> Verdicts { get; } = new();

        public void Watch(string path) => Volatile.Write(ref watched, path);

        public bool IsDeleted(string? path)
        {
            var answer = inner.IsDeleted(path);
            if (path is not null && string.Equals(path, Volatile.Read(ref watched), StringComparison.Ordinal))
                Verdicts.OnNext(answer);
            return answer;
        }
    }

    /// <summary>
    /// How long the teardown hold may last: longer than the whole test (its <c>Timeout</c> is 120 s), for BOTH
    /// budgets that could end it without the test — the child's quiesce drain and its request timeout (an
    /// expired request removes the pending callback and lets the owner finish).
    /// </summary>
    private static readonly TimeSpan HoldBudget = TimeSpan.FromSeconds(240);

    private FaultInjectingStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<FaultInjectingStorageAdapter>();

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    /// <summary>
    /// The injector goes in BEFORE the base adds in-memory persistence: it is the store of record. The
    /// recording verdict is registered after the base, so it is the <see cref="IAddressTombstones"/> every
    /// hub resolves (asserted as a premise).
    /// </summary>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder.AddFaultInjectingStorage())
            .ConfigureServices(services => services.AddSingleton<IAddressTombstones>(
                sp => new RecordingTombstones(sp.GetRequiredService<RecentlyDeletedRegistry>())));

    [Fact(Timeout = 120_000)]
    public async Task AReadOpenedWhileTheRecreateIsAdmitted_RidesTheDyingOwnerOut_AndSeesTheRecreate()
    {
        var ct = TestContext.Current.CancellationToken;
        var tombstones = Mesh.ServiceProvider.GetRequiredService<IAddressTombstones>() as RecordingTombstones;
        tombstones.Should().NotBeNull("the premise: hubs resolve the recording verdict");

        var path = $"{TestPartition}/dyr{Guid.NewGuid().ToString("N")[..10]}";
        await Access.RunAsSystem(() => NodeFactory.CreateNode(MeshNode.FromPath(path) with
            {
                NodeType = "Markdown", Name = "first", State = MeshNodeState.Active,
            }))
            .Should().Within(TestTimeouts.Convergence).Emit("the node exists first", ct);
        await Access.RunAsSystem(() => Mesh.GetMeshNodeStream(path)).Where(n => n?.Name == "first")
            .Should().Within(TestTimeouts.Convergence).Emit("reading the node activates its owner", ct);
        var owner = Mesh.GetHostedHub(new Address(path), HostedHubCreation.Never);
        owner.Should().NotBeNull("the premise: the read activated the node's owner");

        // Hold the owner in teardown: a child whose Quiescing drain owes a reply that only the test sends.
        var holdEntered = new ReplaySubject<IMessageDelivery>(1);
        var holder = Mesh.GetHostedHub(
            new Address("dying-owner-read-hold", Guid.NewGuid().ToString("N")[..8]),
            c => c.WithTypes(typeof(HoldRequest), typeof(HoldResponse))
                .WithHandler<HoldRequest>((_, d) =>
                {
                    holdEntered.OnNext(d);
                    return d.Processed();
                }));
        var child = owner!.GetHostedHub(new Address($"{path}/_hold"),
            c => c.WithTypes(typeof(HoldRequest), typeof(HoldResponse))
                .WithQuiesceTimeout(HoldBudget)
                .WithRequestTimeout(HoldBudget));
        using var held = child.Observe<HoldResponse>(new HoldRequest(), o => o.WithTarget(holder.Address))
            .Subscribe(_ => { }, ex => Output.WriteLine($"hold ended: {ex.GetType().Name}"));
        var owed = await holdEntered.Should().Within(TestTimeouts.Convergence)
            .Emit("the child owes a reply before the delete", ct);
        var teardownReleased = 0;
        void ReleaseTeardown()
        {
            if (Interlocked.Exchange(ref teardownReleased, 1) == 0)
                holder.Post(new HoldResponse(), o => o.ResponseFor(owed!));
        }
        try
        {
            await Access.RunAsSystem(() => NodeFactory.DeleteNode(path))
                .Should().Within(TestTimeouts.Convergence).Emit("the node is deleted", ct);
            await owner.RunLevelChanged.Where(level => level > MessageHubRunLevel.Started)
                .Should().Within(TestTimeouts.Convergence).Emit("the premise: the delete is tearing the owner down", ct);

            // The recreate is admitted by the guard and held before the store commits it.
            using var hold = Storage.HoldWrites(path);
            // The recreate's outcome is recorded from the moment it is issued, so awaiting it never depends on
            // when the write reached the hold.
            var recreated = new ReplaySubject<Notification<MeshNode>>();
            using var recreate = Access.RunAsSystem(() => NodeFactory.CreateNode(MeshNode.FromPath(path) with
                {
                    NodeType = "Markdown", Name = "second", State = MeshNodeState.Active,
                }))
                .Materialize()
                .Subscribe(recreated);
            await hold.Arrivals.Should().Within(TestTimeouts.Convergence)
                .Emit("the recreate was admitted and reached the store", ct);

            // The reader that saw the row: its request goes to the deleted node's owner, still hosted, which
            // refuses it and asks the tombstone how to classify the refusal.
            tombstones!.Watch(path);
            var seen = new ReplaySubject<Notification<MeshNode>>();
            using var read = Access.RunAsSystem(() => Mesh.GetMeshNodeStream(path)).Materialize().Subscribe(seen);
            var verdict = await tombstones.Verdicts.Should().Within(TestTimeouts.Convergence)
                .Emit("the dying owner refuses the read and classifies the refusal", ct);
            verdict.Should().BeFalse(
                "the recreate is on its way: the dying owner must answer 'ask again', never 'this address will not reactivate'");
            (owner.RunLevel < MessageHubRunLevel.Dead).Should().BeTrue(
                "the premise: the refusal came while the deleted owner was still tearing down");

            // The recreate commits FIRST, and only then may the old owner finish its teardown: the reader's next
            // ask then reaches an activation that finds the row. (Released the other way round, a re-ask after
            // the teardown could find the row still absent — a true "not found" for that instant.)
            hold.Release();
            var outcome = await recreated.Where(n => n.Kind != NotificationKind.OnCompleted)
                .Should().Within(TestTimeouts.Convergence).Emit("the recreate commits once released", ct);
            outcome.Kind.Should().Be(NotificationKind.OnNext, $"the recreate succeeds ({outcome.Exception?.Message})");
            ReleaseTeardown();
            var first = await seen.Where(n => n.Kind == NotificationKind.OnError || n.Value is not null)
                .Select(n => n.Kind == NotificationKind.OnNext ? n.Value!.Name ?? string.Empty : $"FAILED: {n.Exception?.Message}")
                .Should().Within(TestTimeouts.Convergence).Emit("the reader is answered", ct);
            first.Should().Be("second", "the reader rides the old owner's teardown out and is served the recreate");
        }
        finally
        {
            // Let the owner finish its teardown, whatever the outcome above.
            ReleaseTeardown();
        }
    }
}
