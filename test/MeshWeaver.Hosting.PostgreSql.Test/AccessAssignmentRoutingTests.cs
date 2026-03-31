using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using Xunit;

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
    public async Task AccessAssignment_DirectChild_RoutesToAccessTable()
    {
        var ct = TestContext.Current.CancellationToken;

        // Setup: create a partitioned schema (like PartnerRe)
        var partitionDef = new PartitionDefinition
        {
            Namespace = "TestOrg",
            Schema = "testorg_access",
            TableMappings = PartitionDefinition.StandardTableMappings
        };
        var (ds, adapter) = await _fixture.CreateSchemaAdapterAsync("testorg_access", partitionDef, ct);

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

            await adapter.WriteAsync(accessNode, _options, ct);

            // Verify: node should be in the `access` table, NOT in `mesh_nodes`
            await using var checkAccess = ds.CreateCommand(
                "SELECT count(*) FROM access WHERE id = 'rbuergi_Access' AND namespace = 'TestOrg'");
            var accessCount = (long)(await checkAccess.ExecuteScalarAsync(ct))!;

            await using var checkMeshNodes = ds.CreateCommand(
                "SELECT count(*) FROM mesh_nodes WHERE id = 'rbuergi_Access' AND namespace = 'TestOrg'");
            var meshNodesCount = (long)(await checkMeshNodes.ExecuteScalarAsync(ct))!;

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
            await ds.DisposeAsync();
        }
    }

    [Fact(Timeout = 60000)]
    public async Task AccessAssignment_ViaAccessSegment_RoutesToAccessTable()
    {
        var ct = TestContext.Current.CancellationToken;

        // Setup: partitioned schema
        var partitionDef = new PartitionDefinition
        {
            Namespace = "OrgAccSeg",
            Schema = "orgaccseg",
            TableMappings = PartitionDefinition.StandardTableMappings
        };
        var (ds, adapter) = await _fixture.CreateSchemaAdapterAsync("orgaccseg", partitionDef, ct);

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

            await adapter.WriteAsync(accessNode, _options, ct);

            // Verify: node in `access` table (path-based routing matches _Access segment)
            await using var checkAccess = ds.CreateCommand(
                "SELECT count(*) FROM access WHERE id = 'rbuergi_Access' AND namespace = 'OrgAccSeg/_Access'");
            var accessCount = (long)(await checkAccess.ExecuteScalarAsync(ct))!;

            await using var checkMeshNodes = ds.CreateCommand(
                "SELECT count(*) FROM mesh_nodes WHERE id = 'rbuergi_Access'");
            var meshNodesCount = (long)(await checkMeshNodes.ExecuteScalarAsync(ct))!;

            accessCount.Should().Be(1, "AccessAssignment under _Access segment should be in `access` table");
            meshNodesCount.Should().Be(0, "AccessAssignment should NOT be in `mesh_nodes`");
        }
        finally
        {
            await ds.DisposeAsync();
        }
    }

    [Fact(Timeout = 60000)]
    public async Task AccessAssignment_UnderAccessSegment_AlsoRoutesToAccessTable()
    {
        var ct = TestContext.Current.CancellationToken;

        // Setup
        var partitionDef = new PartitionDefinition
        {
            Namespace = "TestOrg2",
            Schema = "testorg2_access",
            TableMappings = PartitionDefinition.StandardTableMappings
        };
        var (ds, adapter) = await _fixture.CreateSchemaAdapterAsync("testorg2_access", partitionDef, ct);

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

            await adapter.WriteAsync(accessNode, _options, ct);

            // Verify: also in `access` table (path-based routing works for _Access segment)
            await using var checkAccess = ds.CreateCommand(
                "SELECT count(*) FROM access WHERE id = 'rbuergi_Access'");
            var accessCount = (long)(await checkAccess.ExecuteScalarAsync(ct))!;

            accessCount.Should().Be(1,
                "AccessAssignment under _Access segment should be in `access` table");
        }
        finally
        {
            await ds.DisposeAsync();
        }
    }
}
