using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Tests that AccessAssignment nodes are routed to the `access` satellite table
/// even when their path doesn't contain a "_Access" segment.
///
/// Reproduces the production bug where `PartnerRe/rbuergi_Access` was written
/// to `mesh_nodes` instead of `access`, so the trigger never fired and
/// `user_effective_permissions` stayed empty.
/// </summary>
[Collection("PostgreSql")]
public class AccessAssignmentRoutingTests
{
    private readonly PostgreSqlFixture _fixture;
    private readonly JsonSerializerOptions _options = new();

    public AccessAssignmentRoutingTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact(Timeout = 60000)]
    public void AccessAssignment_DirectChild_RoutesToAccessTable()
    {
        var ct = TestContext.Current.CancellationToken;

        // Setup: create a partitioned schema (like PartnerRe)
        var partitionDef = new PartitionDefinition
        {
            Namespace = "TestOrg",
            Schema = "testorg_access",
            TableMappings = PartitionDefinition.StandardTableMappings
        };
        var (ds, adapter) = _fixture.CreateSchemaAdapter("testorg_access", partitionDef, ct)
            .Should().Within(60.Seconds()).Emit();

        try
        {
            // Write an AccessAssignment as a direct child (path: TestOrg/rbuergi_Access)
            // This is the pattern used in production — NOT under a _Access segment
            var accessNode = new MeshNode("rbuergi_Access", "TestOrg")
            {
                Name = "rbuergi Access",
                NodeType = "AccessAssignment",
                MainNode = "TestOrg",
                State = MeshNodeState.Active,
                Content = new AccessAssignment
                {
                    AccessObject = "rbuergi",
                    DisplayName = "rbuergi",
                    Roles =
                    [
                        new RoleAssignment { Role = "Admin" }
                    ]
                }
            };

            adapter.Write(accessNode, _options).Should().Within(60.Seconds()).Emit();

            // Verify: node should be in the `access` table, NOT in `mesh_nodes`
            var accessCount = ds.ScalarLong(
                "SELECT count(*) FROM access WHERE id = 'rbuergi_Access' AND namespace = 'TestOrg'", ct)
                .Should().Within(30.Seconds()).Emit();
            var meshNodesCount = ds.ScalarLong(
                "SELECT count(*) FROM mesh_nodes WHERE id = 'rbuergi_Access' AND namespace = 'TestOrg'", ct)
                .Should().Within(30.Seconds()).Emit();

            accessCount.Should().Be(1,
                "AccessAssignment should be in the `access` satellite table");
            meshNodesCount.Should().Be(0,
                "AccessAssignment should NOT be in `mesh_nodes`");

            // Note: user_effective_permissions is populated by a trigger function
            // that may reference other tables (group_members, access_control).
            // The trigger fires correctly in production schemas; this test verifies
            // the routing (mesh_nodes vs access table) which is the root cause fix.
        }
        finally
        {
            ds.DisposeReactive().Should().Within(30.Seconds()).Emit();
        }
    }

    [Fact(Timeout = 60000)]
    public void AccessAssignment_ViaAccessSegment_RoutesToAccessTable()
    {
        var ct = TestContext.Current.CancellationToken;

        // Setup: partitioned schema
        var partitionDef = new PartitionDefinition
        {
            Namespace = "OrgAccSeg",
            Schema = "orgaccseg",
            TableMappings = PartitionDefinition.StandardTableMappings
        };
        var (ds, adapter) = _fixture.CreateSchemaAdapter("orgaccseg", partitionDef, ct)
            .Should().Within(60.Seconds()).Emit();

        try
        {
            // Write AccessAssignment under _Access segment (correct path pattern)
            // This is how AddUserRoleAsync should create them:
            // namespace = "OrgAccSeg/_Access", id = "rbuergi_Access"
            var accessNode = new MeshNode("rbuergi_Access", "OrgAccSeg/_Access")
            {
                Name = "rbuergi Access",
                NodeType = "AccessAssignment",
                MainNode = "OrgAccSeg",
                State = MeshNodeState.Active,
                Content = new AccessAssignment
                {
                    AccessObject = "rbuergi",
                    DisplayName = "rbuergi",
                    Roles = [new RoleAssignment { Role = "Admin" }]
                }
            };

            adapter.Write(accessNode, _options).Should().Within(60.Seconds()).Emit();

            // Verify: node in `access` table (path-based routing matches _Access segment)
            var accessCount = ds.ScalarLong(
                "SELECT count(*) FROM access WHERE id = 'rbuergi_Access' AND namespace = 'OrgAccSeg/_Access'", ct)
                .Should().Within(30.Seconds()).Emit();
            var meshNodesCount = ds.ScalarLong(
                "SELECT count(*) FROM mesh_nodes WHERE id = 'rbuergi_Access'", ct)
                .Should().Within(30.Seconds()).Emit();

            accessCount.Should().Be(1, "AccessAssignment under _Access segment should be in `access` table");
            meshNodesCount.Should().Be(0, "AccessAssignment should NOT be in `mesh_nodes`");
        }
        finally
        {
            ds.DisposeReactive().Should().Within(30.Seconds()).Emit();
        }
    }

    [Fact(Timeout = 60000)]
    public void AccessAssignment_UnderAccessSegment_AlsoRoutesToAccessTable()
    {
        var ct = TestContext.Current.CancellationToken;

        // Setup
        var partitionDef = new PartitionDefinition
        {
            Namespace = "TestOrg2",
            Schema = "testorg2_access",
            TableMappings = PartitionDefinition.StandardTableMappings
        };
        var (ds, adapter) = _fixture.CreateSchemaAdapter("testorg2_access", partitionDef, ct)
            .Should().Within(60.Seconds()).Emit();

        try
        {
            // Write an AccessAssignment under a _Access segment (the "correct" path pattern)
            var accessNode = new MeshNode("rbuergi_Access", "TestOrg2/_Access")
            {
                Name = "rbuergi Access",
                NodeType = "AccessAssignment",
                MainNode = "TestOrg2",
                State = MeshNodeState.Active,
                Content = new AccessAssignment
                {
                    AccessObject = "rbuergi",
                    DisplayName = "rbuergi",
                    Roles =
                    [
                        new RoleAssignment { Role = "Editor" }
                    ]
                }
            };

            adapter.Write(accessNode, _options).Should().Within(60.Seconds()).Emit();

            // Verify: also in `access` table (path-based routing works for _Access segment)
            var accessCount = ds.ScalarLong(
                "SELECT count(*) FROM access WHERE id = 'rbuergi_Access'", ct)
                .Should().Within(30.Seconds()).Emit();

            accessCount.Should().Be(1,
                "AccessAssignment under _Access segment should be in `access` table");
        }
        finally
        {
            ds.DisposeReactive().Should().Within(30.Seconds()).Emit();
        }
    }
}
