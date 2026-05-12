using System;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive.Threading.Tasks;
using System.Reactive.Linq;
using FluentAssertions;
using FluentAssertions.Extensions;
using Memex.Portal.Shared;
using MeshWeaver.Data;
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
    {
        // Cap per-test MaxPoolSize so this test class (which spins a fresh Mesh
        // per [Fact] late in the suite) doesn't exhaust the shared Postgres
        // container's max_connections=100 after the fixture-managed tests
        // have already consumed their share. Same tactical motivation as the
        // MaxPoolSize=2 on PostgreSqlFixture.CreateSchemaAdapterAsync.
        var csb = new Npgsql.NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            MaxPoolSize = 4,
            ConnectionIdleLifetime = 10
        };
        return builder
            .UseMonolithMesh()
            .ConfigureServices(services =>
                services.AddPartitionedPostgreSqlPersistence(csb.ConnectionString))
            .AddRowLevelSecurity()
            .AddGraph()
            .AddOrganizationType();
    }

    /// <summary>
    /// Seeds the runtime Admin grant and waits for it to be visible in the
    /// workspace before completing. The post-create
    /// <see cref="MeshWeaver.Data.IWorkspace.GetMeshNodeStream"/> probe
    /// with <c>Where(n => n != null).Take(1)</c> is the deterministic
    /// "wait until visible" primitive recommended by
    /// <c>Doc/Architecture/CqrsAndContentAccess.md</c>; without it the
    /// validator's per-scope synced query races the workspace update from
    /// the pg_notify pipeline and the Admin grant is invisible on the
    /// initial subscription.
    /// </summary>
    protected override async Task SetupAccessRightsAsync()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var adminGrant = AssignmentNodeFactory.UserRole(TestUsers.Admin.ObjectId, "Admin", null);
        var workspace = Mesh.GetWorkspace();

        await meshService.CreateNode(adminGrant)
            .SelectMany(_ => workspace
                .GetMeshNodeStream(adminGrant.Path)
                .Where(n => n != null)
                .Take(1))
            .Timeout(TimeSpan.FromSeconds(10))
            .FirstAsync()
            .ToTask(TestTimeout);
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
