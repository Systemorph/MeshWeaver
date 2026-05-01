using System;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive.Threading.Tasks;
using System.Reactive.Linq;
using FluentAssertions;
using FluentAssertions.Extensions;
using Memex.Portal.Shared;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Tests GetEffectivePermissionsAsync with PostgreSQL persistence.
/// Protocol:
/// 1) Install OrganizationNodeType
/// 2) Create Organization "Systemorph"
/// 3) Ask ISecurityService.HasPermission → should NOT return None
/// </summary>
[Collection("PostgreSql")]
public class EffectivePermissionPostgresTest(PostgreSqlFixture fixture, ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(90.Seconds()).Token;

    /// <summary>
    /// Wire up PostgreSQL partitioned persistence instead of in-memory.
    /// No PublicAdminAccess — permissions must come from persisted AccessAssignment nodes.
    /// </summary>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => builder
            .UseMonolithMesh()
            .ConfigureServices(services =>
                services.AddPartitionedPostgreSqlPersistence(fixture.ConnectionString))
            .AddRowLevelSecurity()
            .AddGraph()
            .AddOrganizationType();

    protected override async Task SetupAccessRightsAsync()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await meshService.CreateNode(AssignmentNodeFactory.UserRole(TestUsers.Admin.ObjectId, "Admin", null)).FirstAsync().ToTask(TestTimeout);
    }

    [Fact(Timeout = 120000)]
    public async Task CreateOrganization_HasPermission_ReturnsAdmin()
    {
        // 1) OrganizationNodeType is installed via ConfigureMesh above

        // 2) Create Organization "Systemorph"
        var orgNode = MeshNode.FromPath("Systemorph") with
        {
            Name = "Systemorph",
            NodeType = OrganizationNodeType.NodeType,
            Content = new Organization { Name = "Systemorph" }
        };
        await NodeFactory.CreateNode(orgNode);

        // 3) Ask ISecurityService.HasPermission for this node
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var permissions = await Mesh.GetPermissionAsync(
            "Systemorph", TestUsers.Admin.ObjectId, TestTimeout);

        // The post-creation handler granted Admin to Roland via persisted AccessAssignment.
        // Without fix: returns Permission.None (GetChildrenAsync filters satellite nodes)
        // With fix: returns Permission.All (GetAllChildrenAsync includes satellites)
        permissions.Should().NotBe(Permission.None,
            "Creator should have permissions from persisted AccessAssignment on the Organization");
        permissions.Should().Be(Permission.All,
            "Admin role grants all permissions");
    }

    /// <summary>
    /// Postgres-backed regression for the 2026-05-01 cleanup-session bug:
    /// runtime AccessAssignment created via the proper write path
    /// (<see cref="IMeshService.CreateNode"/>) must propagate to permission
    /// checks within the synced-query settle window. This is the PG analogue of
    /// the in-memory <c>RuntimeCreateNode_AccessAssignment_GrantsPermission</c>
    /// — the in-memory path passes; this test confirms the same end-to-end
    /// against Postgres-backed persistence with pg_notify-driven change feed.
    ///
    /// If it fails: the synced query's Postgres path has a bug specific to
    /// distributed mode that the in-memory test cannot catch.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task RuntimeCreateNode_AccessAssignment_PgBacked_GrantsPermission()
    {
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var savedContext = accessService.CircuitContext;

        const string userId = "pg-runtime-assignee";
        const string scope = "PgRuntimeOrg";

        try
        {
            var before = await Mesh.GetPermissionAsync(scope, userId, TestTimeout);
            before.Should().Be(Permission.None);

            // Admin (statically seeded as global Admin via SetupAccessRightsAsync)
            // creates the assignment. AccessContext is captured at call time by
            // the underlying CreateNodeRequest pipeline.
            accessService.SetCircuitContext(new AccessContext
            {
                ObjectId = TestUsers.Admin.ObjectId,
                Name = TestUsers.Admin.Name
            });

            var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
            var assignment = AssignmentNodeFactory.UserRole(userId, "Admin", scope);
            await meshService.CreateNode(assignment).FirstAsync().ToTask(TestTimeout);

            var after = await Mesh.GetPermissionAsync(scope, userId,
                until: p => p.HasFlag(Permission.Read), TestTimeout);
            after.Should().Be(Permission.All);
        }
        finally
        {
            accessService.SetCircuitContext(savedContext);
        }
    }

}
