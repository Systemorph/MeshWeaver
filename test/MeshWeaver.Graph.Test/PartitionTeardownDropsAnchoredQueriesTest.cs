using System.Reactive.Linq;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>Tearing a partition down drops the process-wide queries ANCHORED to it</b> — the memory
/// half of a teardown whose store half is the <c>DROP SCHEMA … CASCADE</c>
/// (Systemorph/MeshWeaver.Plugins#1870, MeshWeaver#4061 finding 2).
///
/// <para><b>The defect.</b> <c>$security-access:{partition}</c> is the ONE query every permission
/// check on a partition reads (<c>PermissionEvaluator.ObserveEffectiveAssignments</c>). It is a
/// FOLD: seeded from a single store listing taken when the chain is built, kept current by change
/// events afterwards, and connected for the life of the process (<c>AutoConnect(1)</c> never
/// disconnects). <c>PartitionDropPostDeletionHandler</c> destroyed the store that fold mirrors and
/// left the fold in place — so a partition recreated later under the same id was decided by the
/// OLD chain. That is not symmetrical with a first-ever create: there the query is minted AFTER
/// <c>SpacePostCreationHandler</c> writes the creator's <c>{id}/_Access</c> grant, so its seeding
/// listing contains the grant BY CONSTRUCTION, whereas the surviving fold could only learn the new
/// grant from a change event racing the rest of the create. Measured in the PostgreSQL harness
/// (<c>SpaceRecreateGrantVisibilityPgTests</c>, MeshWeaver.Plugins): 8 of 8 runs left the query
/// resident across the drop holding an EMPTY fold while the store held the grant, and the CI
/// symptom is <c>Access denied: Create permission required for node '{id}/page'</c> on the create
/// that follows.</para>
///
/// <para><b>Why this test lives here and not only in the PG harness.</b> The eviction is a property
/// of the teardown handler, not of Postgres: the in-memory mesh runs the same
/// <c>PartitionDropPostDeletionHandler</c> (it matches every partition root structurally) and the
/// same process-wide <c>IMeshNodeStreamCache</c>. What the in-memory mesh cannot show is the
/// CONSEQUENCE — its fold is not fed by a schema that gets dropped — which is why the behavioural
/// half is asserted in the PG harness and the structural half here.</para>
///
/// <para>The first child create is the control arm: without it the assertion below would be reading
/// "no such query was ever built" rather than "the teardown dropped one that existed".</para>
/// </summary>
public class PartitionTeardownDropsAnchoredQueriesTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string SpaceId = "teardownqueries";

    private static string AccessQueryId => $"$security-access:{SpaceId}";
    private static string PolicyQueryId => $"$security-policy:{SpaceId}";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => Bootstrap.Bootstrap(builder)
            .AddRowLevelSecurity()
            .AddGraph()
            .AddSpaceType()
            .ConfigureHub(c => c
                .WithQuiesceTimeout(TestQuiesceTimeout)
                .WithRequestTimeout(TimeSpan.FromSeconds(60)));

    private static MeshNode Space() => new(SpaceId)
    {
        Name = "Teardown Queries",
        NodeType = SpaceNodeType.NodeType,
        State = MeshNodeState.Active,
        Content = new Space(),
    };

    private static MeshNode Child(string id) => new(id, SpaceId)
    {
        Name = id, NodeType = "Markdown", State = MeshNodeState.Active,
    };

    /// <summary>
    /// Reads the cache's lookup-only surface as SYSTEM, so the answer is "is a chain registered
    /// under this id" and not "may this user read what it holds" — the per-user RLS wrap
    /// short-circuits under System impersonation (<c>SyncedQueryDataSourceExtensions
    /// .WrapWithPerUserRls</c>).
    /// </summary>
    private bool IsResident(string queryId)
    {
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        using var _ = accessService.ImpersonateAsSystem();
        return Mesh.GetQuery(queryId) is not null;
    }

    [Fact(Timeout = 180_000)]
    public async Task DeletingAPartitionRoot_DropsTheQueriesAnchoredToIt()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        await meshService.CreateNode(Space())
            .Should().Within(TestTimeouts.CrossSilo).Emit("the creator may create a Space");

        // CONTROL ARM. The child create is what takes a permission decision ON the partition, and
        // that decision is what mints `$security-access:{id}`. Without this the assertion after the
        // delete would be satisfied by a query that never existed.
        await meshService.CreateNode(Child("first"))
            .Should().Within(TestTimeouts.CrossSilo).Emit(
                "the creator holds Admin on the Space it just created, so a child create is permitted");
        IsResident(AccessQueryId).Should().BeTrue(
            "CONTROL ARM: the child create's permission decision reads $security-access:{partition}, "
            + "so the chain must be registered here — a false makes the post-delete assertion vacuous");

        await meshService.DeleteNode(SpaceId)
            .Should().Within(TestTimeouts.CrossSilo).Emit("the creator may delete its own Space");

        IsResident(AccessQueryId).Should().BeFalse(
            "the partition teardown destroyed the store this query mirrors, so the chain must go "
            + "with it — leaving it registered means every later decision on a partition recreated "
            + "under this id is taken from a fold of a store that no longer exists, repaired only "
            + "by a change event that races the create");
        IsResident(PolicyQueryId).Should().BeFalse(
            "the _Policy twin is anchored to the same partition and lived in the same dropped store");
    }

    [Fact(Timeout = 180_000)]
    public async Task TheRootScopeQueryIsNotAnchoredToAnyPartition_AndSurvivesATeardown()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        await meshService.CreateNode(Space())
            .Should().Within(TestTimeouts.CrossSilo).Emit("the creator may create a Space");
        await meshService.CreateNode(Child("first"))
            .Should().Within(TestTimeouts.CrossSilo).Emit("a child create takes a decision on the partition");

        // The root-scope leg is read on EVERY decision, whatever the partition (ObserveEffective-
        // Assignments combines it with the partition leg), so the control arm is the same create.
        IsResident("$security-access:").Should().BeTrue(
            "CONTROL ARM: the root-scope grants are read on every permission decision");

        await meshService.DeleteNode(SpaceId)
            .Should().Within(TestTimeouts.CrossSilo).Emit("the creator may delete its own Space");

        IsResident("$security-access:").Should().BeTrue(
            "the root scope belongs to NO partition — its grants live in the registered global "
            + "schema, which no partition teardown touches. An eviction that reached it would drop "
            + "the platform-wide grants of every user on every Space delete");
    }
}
