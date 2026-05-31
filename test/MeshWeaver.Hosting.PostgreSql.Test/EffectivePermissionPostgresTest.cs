using System;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive.Threading.Tasks;
using System.Reactive.Linq;
using MeshWeaver.Blazor.Portal;
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
/// 3) Ask SecurityService.HasPermission → should NOT return None
/// </summary>
[Collection("PostgreSql")]
public class EffectivePermissionPostgresTest(PostgreSqlFixture fixture, ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
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
            .AddSpaceType();
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
    protected override Task SetupAccessRightsAsync()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var adminGrant = AssignmentNodeFactory.UserRole(TestUsers.Admin.ObjectId, "Admin", null);
        var workspace = Mesh.GetWorkspace();

        // CreateNode throws if the node already exists. The Postgres fixture is
        // shared across all tests in the [Collection("PostgreSql")] so a prior
        // test's setup may have persisted the same `_Access/Roland_Access` row.
        // Probe with the queryable index (CqrsAndContentAccess.md: query is the
        // right primitive for existence checks; GetMeshNodeStream throws
        // DeliveryFailureException on a 404 in distributed setups). The
        // index lag is unimportant here — a missed hit just means we attempt
        // CreateNode and swallow the "already exists" race. The "already exists"
        // OnError is folded back into a benign emission via Catch so the
        // blocking .Emit() doesn't surface it (this override stays Task-shaped
        // but its body is fully reactive — no await, §2a).
        meshService.CreateNode(adminGrant)
            .SelectMany(_ => workspace
                .GetMeshNodeStream(adminGrant.Path)
                .Where(n => n != null)
                .Take(1))
            .Timeout(TimeSpan.FromSeconds(10))
            .Catch<MeshNode, InvalidOperationException>(ex =>
                ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase)
                    ? Observable.Return<MeshNode>(null!)
                    : Observable.Throw<MeshNode>(ex))
            .Should().Within(90.Seconds()).Emit();
        return Task.CompletedTask;
    }

    [Fact(Timeout = 60000)]
    public void CreateOrganization_HasPermission_ReturnsAdmin()
    {
        // 1) OrganizationNodeType is installed via ConfigureMesh above
        const string orgId = "Systemorph";

        // 2) Register the Admin/Partition/Systemorph MeshNode FIRST. The
        //    Postgres partition provider routes by partition-table state
        //    (see PartitionRoutingTests) — Matches returns false for
        //    unknown first segments. OrganizationNodeType.GetAdditionalNodes
        //    yields the partition AFTER the org create, but the main
        //    `Systemorph` write itself is what fails-to-route without a
        //    pre-existing partition. In prod, the Organization-create flow
        //    needs to provision the partition before the main node — this
        //    test pre-arranges the partition to exercise just the
        //    Organization handler's grant logic.
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var pgProvider = Mesh.ServiceProvider.GetRequiredService<PostgreSqlPartitionStorageProvider>();
        var partitionDef = new PartitionDefinition
        {
            Namespace = orgId,
            DataSource = "default",
            Schema = orgId.ToLowerInvariant(),
            Table = "mesh_nodes",
            TableMappings = PartitionDefinition.StandardTableMappings,
            Versioned = true,
        };
        var partitionNode = new MeshNode(orgId, "Admin/Partition")
        {
            NodeType = "Partition",
            Name = orgId,
            State = MeshNodeState.Active,
            Content = partitionDef
        };
        // Register directly with the provider so Matches returns true
        // immediately — the SubscribeToWorkspace pipeline's CREATE SCHEMA
        // step is async and races the org create otherwise. Also persist
        // the Admin/Partition MeshNode so subsequent reads via the workspace
        // see the partition's catalog entry.
        pgProvider.EnsureSchemaForPartitionAsync(partitionDef, TestContext.Current.CancellationToken)
            .ToObservable().Should().Within(60.Seconds()).Emit();
        pgProvider.RegisterPartition(partitionDef);
        meshService.CreateNode(partitionNode)
            .Should().Within(90.Seconds()).Emit();

        // 3) Create Organization "Systemorph"
        var orgNode = MeshNode.FromPath(orgId) with
        {
            Name = orgId,
            NodeType = SpaceNodeType.NodeType,
            Content = new Space { Name = orgId }
        };
        NodeFactory.CreateNode(orgNode).Should().Within(90.Seconds()).Emit();

        // 4) Ask SecurityService.HasPermission for this node — wait for the
        // synced AccessAssignment query to surface the post-create Admin grant.
        // The post-creation handler granted Admin to Roland via persisted
        // AccessAssignment.
        // Without fix: returns Permission.None (GetChildrenAsync filters satellite nodes)
        // With fix: returns Permission.All (GetAllChildrenAsync includes satellites)
        var permissions = Mesh.GetEffectivePermissions("Systemorph", TestUsers.Admin.ObjectId)
            .Should().Within(90.Seconds()).Match(p => p == Permission.All);
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
    [Fact(Timeout = 60000)]
    public void RuntimeCreateNode_AccessAssignment_PgBacked_GrantsPermission()
    {
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var savedContext = accessService.CircuitContext;

        const string userId = "pg-runtime-assignee";
        const string scope = "PgRuntimeOrg";

        try
        {
            var before = Mesh.GetEffectivePermissions(scope, userId)
                .Should().Within(90.Seconds()).Emit();
            before.Should().Be(Permission.None);

            // Admin authorises the assignment-create. Use TestUsers.Admin
            // directly so its Roles=["Admin"] claim is preserved — claim-based
            // role resolution takes the fast path in SecurityService.ComputeRoleState
            // (no synced-query round-trip for the AUTHORISING user). The
            // RUNTIME AccessAssignment being created here is what the test
            // actually exercises further down via the `after` permission
            // check on `pg-runtime-assignee`.
            accessService.SetCircuitContext(TestUsers.Admin);

            var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

            // Same routing prep as CreateOrganization_HasPermission_ReturnsAdmin:
            // register the scope's partition directly with the provider so
            // Matches({scope}/…) returns true before the AccessAssignment write,
            // then persist the Admin/Partition catalog entry. The PG provider
            // is strict — Matches reflects partition-table state, not a
            // wildcard (pinned by PartitionRoutingTests).
            var pgProvider = Mesh.ServiceProvider.GetRequiredService<PostgreSqlPartitionStorageProvider>();
            var partitionDef = new PartitionDefinition
            {
                Namespace = scope,
                DataSource = "default",
                Schema = scope.ToLowerInvariant(),
                Table = "mesh_nodes",
                TableMappings = PartitionDefinition.StandardTableMappings,
                Versioned = true,
            };
            pgProvider.EnsureSchemaForPartitionAsync(partitionDef, TestContext.Current.CancellationToken)
                .ToObservable().Should().Within(60.Seconds()).Emit();
            pgProvider.RegisterPartition(partitionDef);
            var partitionNode = new MeshNode(scope, "Admin/Partition")
            {
                NodeType = "Partition",
                Name = scope,
                State = MeshNodeState.Active,
                Content = partitionDef
            };
            meshService.CreateNode(partitionNode).Should().Within(90.Seconds()).Emit();

            var assignment = AssignmentNodeFactory.UserRole(userId, "Admin", scope);
            meshService.CreateNode(assignment).Should().Within(90.Seconds()).Emit();

            var after = Mesh.GetEffectivePermissions(scope, userId)
                .Should().Within(90.Seconds()).Match(p => p.HasFlag(Permission.Read));
            after.Should().Be(Permission.All);
        }
        finally
        {
            accessService.SetCircuitContext(savedContext);
        }
    }

}
