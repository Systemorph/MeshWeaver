using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
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
/// A node DECLARED in configuration (<c>AddMeshNodes</c>, <c>IStaticNodeProvider</c>) is served to
/// queries from memory by <c>StaticNodeQueryProvider</c>. That provider takes the parsed query and
/// the context string and nothing else: it never reads the viewer, and it never runs the
/// per-row Read validators that <c>StorageAdapterMeshQueryProvider.ValidateRead</c> runs for a
/// STORED row. So a declared access grant under a satellite path (the platform-admin seed declares
/// <c>Admin/_Access/{user}_Access</c> per <c>Auth:GlobalAdmins</c> id) is listed to ANY caller of
/// <c>search</c> / <c>query-nodes</c> - who holds platform admin is thereby disclosed to every
/// authenticated user.
///
/// <para>The mesh declares one <c>AccessAssignment</c> on a satellite path of the test partition,
/// MainNode = the partition. The viewer without any grant must not be handed it (the second case);
/// the admin and the System identity still are (the third case, which also proves the filter did
/// not simply empty the listing). The first case pins the premise both rest on.</para>
/// </summary>
public class ADeclaredSatelliteNodeIsRowFilteredTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string GrantId = "declaredadmin_Access";
    private const string SatelliteNamespace = $"{TestPartition}/_Access";
    private const string GrantPath = $"{SatelliteNamespace}/{GrantId}";

    /// <summary>A signed-in caller with no grant anywhere.</summary>
    private static readonly AccessContext Outsider = new()
    {
        ObjectId = "outsider",
        Name = "Outsider",
        Roles = [],
    };

    private static MeshNode DeclaredGrant() => new(GrantId, SatelliteNamespace)
    {
        NodeType = "AccessAssignment",
        Name = "declaredadmin - Viewer",
        Content = new AccessAssignment
        {
            AccessObject = "declaredadmin",
            DisplayName = "declaredadmin",
            Roles = [new RoleAssignment { Role = "Viewer" }],
        },
        MainNode = TestPartition,
    };

    // ConfigureMeshBase, not base.ConfigureMesh: the latter chains PublicAdminAccess(), which would
    // make every identity an administrator and the denial below vacuous.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder).AddMeshNodes(DeclaredGrant());

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private static MeshQueryRequest ListTheGrants()
        => new() { Query = $"path:{SatelliteNamespace} scope:descendants" };

    private Task<IReadOnlyList<string>> Paths(MeshQueryRequest request, string because) =>
        MeshService.Query<MeshNode>(request)
            .Where(change => change.ChangeType == QueryChangeType.Initial)
            .FirstAsync()
            .Select(change => (IReadOnlyList<string>)change.Items.Select(n => n.Path).ToList())
            .Should().Within(TestTimeouts.Convergence).Emit(because,
                cancellationToken: TestContext.Current.CancellationToken);

    private void BecomeTheOutsider()
    {
        Access.SetCircuitContext(Outsider);
        (Access.Context?.ObjectId ?? Access.CircuitContext?.ObjectId)
            .Should().Be(Outsider.ObjectId,
                "every assertion about the outsider is about what THIS identity sees - if the ambient "
                + "viewer were still the test's admin the leak assertion would pass without measuring it");
    }

    /// <summary>
    /// The premise: the outsider genuinely holds no Read on the partition the declared grant is
    /// scoped to (its MainNode), and the admin the test base seeds does.
    /// </summary>
    [Fact]
    public async Task TheOutsiderReallyIsDenied_AndTheAdminIsNot()
    {
        (await Mesh.CheckPermission(TestPartition, Outsider.ObjectId, Permission.Read)
                .Should().Within(TestTimeouts.Convergence).Emit("the permission fold answers"))
            .Should().BeFalse(
                $"'{Outsider.ObjectId}' holds no grant at any scope over '{TestPartition}'");

        (await Mesh.CheckPermission(TestPartition, TestUsers.Admin.ObjectId, Permission.Read)
                .Should().Within(TestTimeouts.Convergence).Emit("the permission fold answers"))
            .Should().BeTrue(
                "the root Admin grant the test base seeds must still read - a fold that denied "
                + "everyone would make the denial above meaningless");
    }

    /// <summary>
    /// THE REGRESSION. Before the fix the static provider hands the declared grant to any viewer,
    /// so this fails with the declared path in the message.
    /// </summary>
    [Fact]
    public async Task ADeclaredAccessAssignment_IsNotListedToAViewerWhoCannotReadIt()
    {
        BecomeTheOutsider();

        var paths = await Paths(ListTheGrants(), "the listing answers for any authenticated caller");

        paths.Should().NotContain(GrantPath,
            "a declared satellite node is judged by the same Read validators, for the same viewer, "
            + "as a stored row - handing this one to a caller who cannot read its scope discloses "
            + "who holds the grant");
    }

    /// <summary>
    /// The controls on the other side: a viewer who CAN read the scope, and the System identity, are
    /// still handed the declared grant. Also the proof that <see cref="Paths"/> reads a converged
    /// snapshot - if it did not, this fails.
    /// </summary>
    [Fact]
    public async Task ADeclaredAccessAssignment_IsStillListedToAReaderAndToSystem()
    {
        var asAdmin = await Paths(ListTheGrants(), "the admin listing answers");
        asAdmin.Should().Contain(GrantPath,
            "the admin holds Read on the partition, so the declared grant stays visible to them");

        var asSystem = await Paths(
            ListTheGrants() with { UserId = WellKnownUsers.System },
            "the System listing answers");
        asSystem.Should().Contain(GrantPath,
            "System reads are unfiltered - the permission fold itself reads declared grants this way");
    }
}
