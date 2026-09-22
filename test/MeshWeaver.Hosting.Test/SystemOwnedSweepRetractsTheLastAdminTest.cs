using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>Wiring a ONE-WAY <c>_GitSync</c> retracts EVERY privileged grant on the partition — the last
/// administrator included.</b> Wiring a BIJECTIVE one retracts nothing.
///
/// <para><b>Why this test exists, and what it would have caught.</b> #5140 reported three symptoms of
/// one mechanism: a system-owned Space that kept a human <c>Admin</c> grant through two syncs, a
/// direct write by that human being accepted, and the grant being impossible to delete. Two rules
/// were holding each other up. <c>AccessAssignmentGuard.IsForbiddenOnSystemOwned</c> refuses granting
/// a SECOND Admin on such a partition; <c>SpaceAdminInvariantValidator</c> refused removing the
/// FIRST; so <c>SystemOwnedAccessRetractionHandler</c> pre-detected the guaranteed rejection and
/// SPARED that grant — on every pass, for ever, because the escape it documented ("retracted next
/// time, once another admin exists") could never arrive.</para>
///
/// <para>Exempting the invariant is only half the fix, and on its own it is the WORSE half: the
/// handler's pre-spare would still stop the delete from ever being issued, so the exemption would be
/// inert and nothing would report it. A test that drives the validator directly cannot see that — it
/// measures the half that changed. This one drives the SWEEP, which is the thing whose outcome the
/// issue is about.</para>
///
/// <para><b>Control on each side, over the same Space and the same grants:</b>
/// <see cref="AOneWaySync_LeavesNoWriteConferringGrantAtAll"/> must converge to an EMPTY set of
/// write-conferring grants and <see cref="ABijectiveSync_RetractsNothing"/> must not. A sweep that
/// retracted unconditionally would pass the first and fail the second; one that spares anything
/// fails the first. Neither is satisfiable by a constant.</para>
///
/// <para>🚨 <b>The assertion is about the SET, and the first version of this test — which named one
/// grant's path — passed against the unfixed sweep.</b> A Space's post-creation handler grants its
/// creator Admin, so two write-conferring grants exist; the pre-spare kept the earliest (the
/// creator's) and deleted the other, making "my grant is gone" true either way. That is the same
/// defect this whole change set is about, in the test written to catch it, and only running the
/// control against the unfixed source exposed it.</para>
/// </summary>
public class SystemOwnedSweepRetractsTheLastAdminTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string SpaceOwner = "space-owner";
    private const string RepoUrl = "https://github.com/test/sweep-last-admin";

    private static TimeSpan Budget => TestTimeouts.Convergence;

    private GitHubSyncService Sync => Mesh.ServiceProvider.GetRequiredService<GitHubSyncService>();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGitHubSyncTypes()
            .ConfigureServices(services =>
            {
                services.AddGitHubSyncServices();
                return services;
            });

    /// <summary>
    /// A Space carrying a human Admin grant written BEFORE the sync is wired — the whole window
    /// #5140 is about: legal when written, wrong one second later.
    ///
    /// <para>🚨 There are TWO admin grants here, not one, and that is deliberate — the Space's
    /// post-creation handler grants its CREATOR Admin as well. Asserting on one path was how the
    /// first version of this test passed against the unfixed sweep: the pre-spare kept the
    /// earliest-created grant (the creator's) and deleted the other, so "my grant is gone" was true
    /// either way. The property is about the SET, so the assertions below are about the set.</para>
    /// </summary>
    private async Task<string> ASpaceWithAHumanAdmin()
    {
        var space = "Swept" + Guid.NewGuid().ToString("N")[..8];
        await NodeFactory.CreateNode(new MeshNode(space)
        {
            NodeType = "Space", Name = "Swept space", State = MeshNodeState.Active,
            Content = new Space(),
        }).Should().Within(Budget).Emit("the Space fixture must be created",
            cancellationToken: TestContext.Current.CancellationToken);

        await NodeFactory.CreateNode(AssignmentNodeFactory.UserRole(SpaceOwner, "Admin", space))
            .Should().Within(Budget).Emit("a human Admin grant must exist BEFORE the sync is wired",
                cancellationToken: TestContext.Current.CancellationToken);

        await WriteGrantsRemain(space).Should().Within(Budget).Emit(
            "at least one write-conferring grant must be readable before the sweep runs, or every "
            + "assertion below is vacuous",
            cancellationToken: TestContext.Current.CancellationToken);

        return space;
    }

    /// <summary>
    /// Emits once NO write-conferring <c>AccessAssignment</c> remains under <c>{space}/_Access</c> —
    /// the property the sweep claims. Re-queried on an interval because a query is the read-side
    /// index and trails the store: waiting on the CONDITION, never on a delay.
    /// </summary>
    private IObservable<bool> NoWriteGrantRemains(string space) => Poll(space, anyRemain: false);

    /// <summary>Emits once at least one write-conferring grant is readable there.</summary>
    private IObservable<bool> WriteGrantsRemain(string space) => Poll(space, anyRemain: true);

    private IObservable<bool> Poll(string space, bool anyRemain)
        => Observable.Interval(TestTimeouts.Quick / 10)
            .StartWith(0L)
            .SelectMany(_ => NodeFactory
                .Query<MeshNode>(MeshQueryRequest.FromQuery(
                    $"namespace:{space}/_Access nodeType:{AccessAssignmentGuard.AccessAssignmentNodeType}"))
                .Take(1)
                .Select(change => change.Items.Any(n =>
                    AccessAssignmentGuard.ConfersWriteAccess(
                        n.ContentAs<AccessAssignment>(Mesh.JsonSerializerOptions)))))
            .Where(remain => remain == anyRemain)
            .Take(1);

    private async Task WireSync(string space, bool twoWay)
    {
        var config = await Sync.SaveConfig(
                space, RepoUrl, "main", null, false, false,
                direction: twoWay ? SyncDirection.Bidirectional : SyncDirection.ImportOnly,
                twoWay: twoWay)
            .Should().Within(Budget).Emit("the sync config must be written",
                cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(GitHubSyncService.ConfigPath(space), config.Path);
        Assert.Equal(
            !twoWay,
            AccessAssignmentGuard.IsSystemOwned(config, Mesh.JsonSerializerOptions));
    }

    [Fact]
    public async Task AOneWaySync_LeavesNoWriteConferringGrantAtAll()
    {
        var space = await ASpaceWithAHumanAdmin();

        await WireSync(space, twoWay: false);

        await NoWriteGrantRemains(space).Should().Within(Budget).Emit(
            "the repo owns this Space now, so nobody else does — INCLUDING its last administrator. "
            + "There is no second admin to wait for, because the write boundary refuses one, so "
            + "sparing any of them is what made the documented state unreachable (#5140)",
            cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ABijectiveSync_RetractsNothing()
    {
        var space = await ASpaceWithAHumanAdmin();

        await WireSync(space, twoWay: true);

        // The negative shape: the set must NOT empty out. A bijective sync preserves and commits
        // back server-side edits, so the mesh nodes are these admins' working copy and retracting
        // their write access would make the feature unusable by everyone but the importer.
        await NoWriteGrantRemains(space).Should().NotEmit(TestTimeouts.Quick,
            "a bijective sync does not make a Space system-owned, so its administrators keep write "
            + "access — the sweep keys on the DIRECTION, not on the presence of a sync");
    }
}
