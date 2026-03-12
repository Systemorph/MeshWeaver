using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Tests that Partition nodes are correctly stored and queried in PostgreSQL,
/// including node_type_permissions for public read access.
/// </summary>
[Collection("PostgreSql")]
public class PartitionQueryTests
{
    private readonly PostgreSqlFixture _fixture;
    private readonly JsonSerializerOptions _options = new();

    public PartitionQueryTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    private static T? DeserializeContent<T>(object? content) where T : class
    {
        if (content is T typed) return typed;
        if (content is JsonElement el) return JsonSerializer.Deserialize<T>(el.GetRawText());
        return null;
    }

    private async Task SeedPartitionDataAsync()
    {
        await _fixture.CleanDataAsync();
        var adapter = _fixture.StorageAdapter;

        // Seed Partition nodes
        await adapter.WriteAsync(new MeshNode("ACME", "Admin/Partition")
        {
            Name = "ACME Corp",
            NodeType = "Partition",
            State = MeshNodeState.Active,
            Content = new PartitionDefinition
            {
                Namespace = "ACME",
                DataSource = "default",
                Schema = "acme",
                TableMappings = PartitionDefinition.StandardTableMappings,
                Description = "ACME organization partition"
            }
        }, _options, TestContext.Current.CancellationToken);

        await adapter.WriteAsync(new MeshNode("Documentation", "Admin/Partition")
        {
            Name = "MeshWeaver Documentation",
            NodeType = "Partition",
            State = MeshNodeState.Active,
            Content = new PartitionDefinition
            {
                Namespace = "Doc",
                DataSource = "static",
                Description = "Built-in documentation"
            }
        }, _options, TestContext.Current.CancellationToken);

        // Register Partition node type as public read
        await _fixture.AccessControl.SyncNodeTypePermissionsAsync(
            [new NodeTypePermission("Partition", PublicRead: true)],
            TestContext.Current.CancellationToken);
    }

    [Fact(Timeout = 30000)]
    public async Task PartitionNodes_CanBeWrittenAndRead()
    {
        await SeedPartitionDataAsync();
        var adapter = _fixture.StorageAdapter;

        var node = await adapter.ReadAsync("Admin/Partition/ACME", _options,
            TestContext.Current.CancellationToken);

        node.Should().NotBeNull();
        node!.NodeType.Should().Be("Partition");
        node.Name.Should().Be("ACME Corp");

        var def = DeserializeContent<PartitionDefinition>(node.Content);
        def.Should().NotBeNull();
        def!.Namespace.Should().Be("ACME");
        def.DataSource.Should().Be("default");
        def.Schema.Should().Be("acme");
    }

    [Fact(Timeout = 30000)]
    public async Task PublicReadPartitions_VisibleToAuthenticatedUser()
    {
        await SeedPartitionDataAsync();

        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);

        // Query as authenticated user "alice" (no explicit access grants)
        var request = MeshQueryRequest.FromQuery(
            $"namespace:Admin/Partition nodeType:Partition", userId: "alice");

        var results = new List<object>();
        await foreach (var item in query.QueryAsync(request, _options,
            TestContext.Current.CancellationToken))
        {
            results.Add(item);
        }

        results.Should().HaveCountGreaterThanOrEqualTo(2,
            "Partition nodes with public read should be visible to any authenticated user");
        results.OfType<MeshNode>().Should().Contain(n => n.Path == "Admin/Partition/ACME");
        results.OfType<MeshNode>().Should().Contain(n => n.Path == "Admin/Partition/Documentation");
    }

    [Fact(Timeout = 30000)]
    public async Task PartitionNodes_NotVisibleToAnonymous()
    {
        await SeedPartitionDataAsync();

        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);

        // Query as anonymous user
        var request = MeshQueryRequest.FromQuery(
            $"namespace:Admin/Partition nodeType:Partition", userId: WellKnownUsers.Anonymous);

        var results = new List<object>();
        await foreach (var item in query.QueryAsync(request, _options,
            TestContext.Current.CancellationToken))
        {
            results.Add(item);
        }

        results.Should().BeEmpty("Anonymous users should not see public-read partition nodes");
    }

    [Fact(Timeout = 30000)]
    public async Task PartitionDefinition_RoundTrips_Content()
    {
        await SeedPartitionDataAsync();
        var adapter = _fixture.StorageAdapter;

        var node = await adapter.ReadAsync("Admin/Partition/Documentation", _options,
            TestContext.Current.CancellationToken);

        node.Should().NotBeNull();
        var def = DeserializeContent<PartitionDefinition>(node.Content);
        def.Should().NotBeNull();
        def!.Namespace.Should().Be("Doc");
        def.DataSource.Should().Be("static");
        def.Description.Should().Be("Built-in documentation");
    }

    [Fact(Timeout = 30000)]
    public async Task PartitionDefinition_TableMappings_RoundTrip()
    {
        await SeedPartitionDataAsync();
        var adapter = _fixture.StorageAdapter;

        var node = await adapter.ReadAsync("Admin/Partition/ACME", _options,
            TestContext.Current.CancellationToken);

        node.Should().NotBeNull();
        var def = DeserializeContent<PartitionDefinition>(node.Content);
        def.Should().NotBeNull();
        def!.TableMappings.Should().NotBeNull();
        def.TableMappings.Should().ContainKey("_Activity");
        def.TableMappings!["_Activity"].Should().Be("activities");
        def.TableMappings.Should().ContainKey("_Thread");
        def.TableMappings.Should().ContainKey("_Access");
    }

    [Fact(Timeout = 30000)]
    public async Task PartitionDefinition_ResolveTable_RoutesCorrectly()
    {
        var def = new PartitionDefinition
        {
            Namespace = "User",
            TableMappings = PartitionDefinition.StandardTableMappings
        };

        // Main node → primary table
        def.ResolveTable("User/roland").Should().Be("mesh_nodes");

        // Satellite nodes → respective tables
        def.ResolveTable("User/roland/_Activity/abc").Should().Be("activities");
        def.ResolveTable("User/roland/_UserActivity/xyz").Should().Be("user_activities");
        def.ResolveTable("User/roland/_Thread/mythread").Should().Be("threads");
        def.ResolveTable("User/roland/_Thread/mythread/_ThreadMessage/msg1").Should().Be("threads");
        def.ResolveTable("User/roland/_Tracking/tc1").Should().Be("tracking");
        def.ResolveTable("User/roland/_Approval/appr1").Should().Be("approvals");
        def.ResolveTable("User/roland/_Access/aa1").Should().Be("access");
        def.ResolveTable("User/roland/_Comment/c1").Should().Be("comments");
    }
}
