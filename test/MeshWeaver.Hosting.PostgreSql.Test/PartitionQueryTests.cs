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
                BasePaths = new HashSet<string> { "ACME" },
                StorageType = "PostgreSql",
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
                BasePaths = new HashSet<string> { "Doc" },
                StorageType = "Static",
                Description = "Built-in documentation"
            }
        }, _options, TestContext.Current.CancellationToken);

        // Register Partition node type as public read
        await _fixture.AccessControl.SyncNodeTypePermissionsAsync(
            [new NodeTypePermission("Partition", PublicRead: true)],
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PartitionNodes_CanBeWrittenAndRead()
    {
        await SeedPartitionDataAsync();
        var adapter = _fixture.StorageAdapter;

        var node = await adapter.ReadAsync("Admin/Partition/ACME", _options,
            TestContext.Current.CancellationToken);

        node.Should().NotBeNull();
        node!.NodeType.Should().Be("Partition");
        node.Name.Should().Be("ACME Corp");
        node.Content.Should().BeOfType<PartitionDefinition>();

        var def = (PartitionDefinition)node.Content!;
        def.BasePaths.Should().Contain("ACME");
        def.StorageType.Should().Be("PostgreSql");
    }

    [Fact]
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

    [Fact]
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

    [Fact]
    public async Task PartitionDefinition_RoundTrips_Content()
    {
        await SeedPartitionDataAsync();
        var adapter = _fixture.StorageAdapter;

        var node = await adapter.ReadAsync("Admin/Partition/Documentation", _options,
            TestContext.Current.CancellationToken);

        node.Should().NotBeNull();
        var def = node!.Content as PartitionDefinition;
        def.Should().NotBeNull();
        def!.BasePaths.Should().Contain("Doc");
        def.StorageType.Should().Be("Static");
        def.Description.Should().Be("Built-in documentation");
    }
}
