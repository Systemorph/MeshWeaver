using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>A partition teardown supersedes the queries anchored to it — it does not END their
/// readers</b> (MeshWeaver#7719, #7720).
///
/// <para><b>The defect.</b> <c>MeshNodeStreamCache.InvalidatePartition</c> — run by every partition
/// teardown, a package uninstall included — drops and RELEASES each chain anchored to the partition
/// (<c>$security-access:{partition}</c>, <c>$security-policy:{partition}</c>, …). The released chain
/// was the very observable callers had been handed, so every reader still on it — attached, or
/// subscribing later through a reference it had composed earlier (the permission fold resolves its
/// queries when it is COMPOSED) — was terminated by <c>AutoConnectOwnedBy</c> with
/// <see cref="ObjectDisposedException"/>: "the owner is disposed … a subscriber arriving now would
/// otherwise wait on a replay nothing will feed". The cache was NOT disposed. Measured on a
/// production portal: a viewer confirmed the uninstall of the package whose partition they had open;
/// within a second the page's render failed with that exception (#7719), and the page's standing
/// read follow — which treats a faulted verdict as a refusal (<c>AdmitEachRevision</c>) — ended the
/// layout stream with "Access denied … lacks Read permission" on every area element (#7720).</para>
///
/// <para><b>The contract pinned here.</b> A reader of a superseded chain is handed on to the
/// registry's fresh chain for the same key and keeps receiving values; a reader that subscribes
/// through a handle obtained before the teardown gets a live chain and a value; and the fold built
/// on top keeps following grants across the teardown. The negative control is that an entry still
/// referenced and NOT evicted is shared by a second caller, not duplicated.</para>
/// </summary>
public class PartitionTeardownHandsQueryReadersOnTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string SubjectId = "teardown-handover-subject";

    private MeshNodeStreamCache Cache =>
        (MeshNodeStreamCache)Mesh.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    /// <summary><see cref="MonolithMeshTestBase.ConfigureMeshBase"/>, not the default mesh: the
    /// default grants Public → Admin, under which the subject would hold Read before any grant and the
    /// fold test could not observe the runtime grant arriving.</summary>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => ConfigureMeshBase(builder);

    [Fact(Timeout = 240_000)]
    public async Task ATornDownPartitionsQuery_HandsItsReadersToAFreshChain_AndSharesWhileReferenced()
    {
        var ct = TestContext.Current.CancellationToken;
        var cache = Cache;
        var options = Mesh.JsonSerializerOptions;
        // A partition of its own, so the teardown below reaches only the ids this test opened.
        var partition = $"handover{Guid.NewGuid().ToString("N")[..8]}";
        var id = SecurityQueries.PartitionAssignmentsQueryId(partition);
        var query = $"nodeType:Markdown namespace:{TestPartition}";

        // Subscriber 1 — the handle a composed fold holds, attached for the whole test.
        var held = cache.GetQuery(id, options, query);
        var recorded = new ReplaySubject<Notification<IEnumerable<MeshNode>>>();
        using var attached = held.Materialize().Subscribe(recorded);
        var first = await recorded.FirstAsync().Should().Within(TestTimeouts.Convergence).Emit(
            "precondition: the synced query connects and answers on the live mesh", cancellationToken: ct);
        first.Kind.Should().Be(NotificationKind.OnNext, "precondition: the first frame is a value");

        // NEGATIVE CONTROL — a still-referenced entry is SHARED: a second caller for the same key is
        // handed the same registry chain and opens no second upstream connection.
        var connectionsBefore = cache.LiveQueryConnections;
        var second = await cache.GetQuery(id, options, query).FirstAsync()
            .Should().Within(TestTimeouts.Convergence).Emit("a second caller rides the shared chain", cancellationToken: ct);
        second.Should().NotBeNull();
        cache.LiveQueryConnections.Should().Be(connectionsBefore,
            "an entry that is still registered must be shared, not duplicated — a second connection "
            + "here would mean every reader opened its own upstream");

        // The teardown. Before the fix this released the chain the handle WAS, and both readers
        // below met "MeshNodeStreamCache query '…' has released the shared connection".
        cache.InvalidatePartition(partition);

        // 🚨 Identified by DATA, never by position: `recorded` replays the whole history, so "the
        // next notification" could be a pre-teardown snapshot (review on #7726). A node written AFTER
        // the teardown can only reach this reader through a live chain — the released one's upstream
        // is unsubscribed — so seeing it proves the hand-over, and a terminal ends the wait instead.
        Access.SetCircuitContext(TestUsers.Admin);
        var written = $"{TestPartition}/handover-{Guid.NewGuid().ToString("N")[..8]}";
        await NodeFactory.CreateNode(MeshNode.FromPath(written) with
        {
            Name = written, NodeType = "Markdown", State = MeshNodeState.Active,
        }).Should().Within(TestTimeouts.Convergence).Emit("the admin may create a Markdown node", cancellationToken: ct);

        var afterTeardown = await recorded
            .Where(n => n.Kind != NotificationKind.OnNext || n.Value.Any(node => node.Path == written))
            .FirstAsync().Should().Within(TestTimeouts.Convergence).Emit(
                "the attached reader must see the node written after the teardown — or, before the fix, "
                + "its terminal", cancellationToken: ct);
        afterTeardown.Kind.Should().Be(NotificationKind.OnNext,
            $"an attached reader of a superseded chain is handed to the fresh chain, not ended with "
            + $"'the owner is disposed' — the cache is alive. Got: {afterTeardown.Exception}");

        var late = await held.Materialize()
            .Where(n => n.Kind != NotificationKind.OnNext || n.Value.Any(node => node.Path == written))
            .FirstAsync().Should().Within(TestTimeouts.Convergence).Emit(
                "a reader subscribing through a handle obtained before the teardown must answer from "
                + "the live chain, or terminate", cancellationToken: ct);
        late.Kind.Should().Be(NotificationKind.OnNext,
            $"a new subscriber through an old handle must get a LIVE entry and a value, never the "
            + $"released corpse. Got: {late.Exception}");

        // Nothing in the attached reader's whole history may be a terminal: a hand-over that faulted
        // AFTER delivering the post-teardown value would still be the #7720 shape.
        // A ReplaySubject replays its whole history synchronously inside Subscribe, so this reads
        // every notification recorded so far with no wait and no timer.
        Notification<IEnumerable<MeshNode>>? terminal = null;
        using (recorded.Where(n => n.Kind != NotificationKind.OnNext).Subscribe(n => terminal = n))
        { }
        terminal.Should().BeNull(
            "the attached reader stays live across the teardown — no error, no completion");
        cache.QueryConnectionsReleased.Should().BeFalse("the cache itself was never disposed");
    }

    [Fact(Timeout = 240_000)]
    public async Task ALivePermissionFold_KeepsFollowingGrantsAcrossAPartitionTeardown()
    {
        var ct = TestContext.Current.CancellationToken;
        Access.SetCircuitContext(TestUsers.Admin);
        var area = $"{TestPartition}/handover-{Guid.NewGuid().ToString("N")[..8]}";

        // The standing verdict a page's read follow holds: ONE long-lived fold subscription.
        var recorded = new ReplaySubject<Notification<Permission>>();
        using var fold = PermissionEvaluator.GetEffectivePermissions(Mesh, area, SubjectId)
            .Materialize()
            .Subscribe(recorded);
        var before = await recorded.FirstAsync().Should().Within(TestTimeouts.Convergence).Emit(
            "precondition: the fold answers", cancellationToken: ct);
        before.Kind.Should().Be(NotificationKind.OnNext);
        before.Value.HasFlag(Permission.Read).Should().BeFalse(
            "CONTROL ARM: the subject holds no grant yet — otherwise the grant below proves nothing");

        // The teardown's in-memory half, exactly as PartitionDropPostDeletionHandler runs it.
        Cache.InvalidatePartition(TestPartition);

        await NodeFactory.CreateNode(Assignment($"{SubjectId}_Viewer", TestPartition, Role.Viewer.Id))
            .Should().Within(TestTimeouts.Convergence).Emit("the admin may grant Viewer", cancellationToken: ct);

        var decided = await recorded
            .Where(n => n.Kind != NotificationKind.OnNext || n.Value.HasFlag(Permission.Read))
            .FirstAsync()
            .Should().Within(TestTimeouts.Convergence).Emit(
                "the live fold must either see the grant or end — never sit silent", cancellationToken: ct);
        decided.Kind.Should().Be(NotificationKind.OnNext,
            $"the fold's partition leg must ride the fresh chain across the teardown and see the new "
            + $"grant; a fault here is what a read follow turns into a false 'Access denied'. Got: {decided.Exception}");
    }

    private static MeshNode Assignment(string id, string scope, string role) =>
        new(id, $"{scope}/_Access")
        {
            NodeType = "AccessAssignment",
            Name = $"{SubjectId} — {role} on {scope}",
            MainNode = scope,
            Content = new AccessAssignment
            {
                AccessObject = SubjectId,
                DisplayName = SubjectId,
                Roles = [new RoleAssignment { Role = role, Denied = false }],
            },
        };
}
