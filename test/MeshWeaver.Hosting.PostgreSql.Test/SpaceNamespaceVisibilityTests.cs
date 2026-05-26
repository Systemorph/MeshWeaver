using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Blazor.Portal;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Tests that Space partition root nodes are visible in queries.
/// When an Space "PartnerRe" is created, a MeshNode at path "PartnerRe"
/// must be queryable so it appears in the namespace picker (context:create)
/// and in autocomplete results.
/// </summary>
[Collection("PostgreSql")]
public class SpaceNamespaceVisibilityTests
{
    private readonly PostgreSqlFixture _fixture;
    private readonly JsonSerializerOptions _options = new();

    public SpaceNamespaceVisibilityTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task SeedSpaceAsync()
    {
        await _fixture.CleanDataAsync();
        var adapter = _fixture.StorageAdapter;
        var ac = _fixture.AccessControl;

        // Seed Space node at root path (same as partition namespace)
        await adapter.WriteAsync(new MeshNode("PartnerRe")
        {
            Name = "PartnerRe AG",
            NodeType = "Space",
            Content = new Space { Name = "PartnerRe AG" }
        }, _options, TestContext.Current.CancellationToken);

        // Seed a child node under the Space
        await adapter.WriteAsync(new MeshNode("AiConsulting", "PartnerRe")
        {
            Name = "AI Consulting",
            NodeType = "Group"
        }, _options, TestContext.Current.CancellationToken);

        // Register Space as public-read
        await ac.SyncNodeTypePermissionsAsync([
            new NodeTypePermission("Space", PublicRead: true)
        ], TestContext.Current.CancellationToken);

        // Grant authenticated user access
        await ac.GrantAsync("PartnerRe", "alice", "Read", isAllow: true, TestContext.Current.CancellationToken);
        await ac.GrantAsync("PartnerRe", "alice", "Create", isAllow: true, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SpaceRootNode_VisibleByPath()
    {
        await SeedSpaceAsync();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);

        var request = MeshQueryRequest.FromQuery("path:PartnerRe", "alice");
        var results = new List<MeshNode>();
        await foreach (var item in query.QueryAsync(request, _options, TestContext.Current.CancellationToken))
        {
            if (item is MeshNode node) results.Add(node);
        }

        results.Should().HaveCount(1, "Space root node should be queryable by path");
        results[0].Name.Should().Be("PartnerRe AG");
        results[0].NodeType.Should().Be("Space");
    }

    [Fact]
    public async Task SpaceRootNode_VisibleByNodeType()
    {
        await SeedSpaceAsync();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);

        var request = MeshQueryRequest.FromQuery("nodeType:Space", "alice");
        var results = new List<MeshNode>();
        await foreach (var item in query.QueryAsync(request, _options, TestContext.Current.CancellationToken))
        {
            if (item is MeshNode node) results.Add(node);
        }

        results.Should().Contain(n => n.Path == "PartnerRe",
            "Space root node should appear in nodeType:Space queries");
    }

    [Fact]
    public async Task SpaceRootNode_VisibleInNamespaceQuery()
    {
        await SeedSpaceAsync();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);

        // This is how the namespace picker queries â€” root-level nodes
        var request = MeshQueryRequest.FromQuery("namespace:", "alice");
        var results = new List<MeshNode>();
        await foreach (var item in query.QueryAsync(request, _options, TestContext.Current.CancellationToken))
        {
            if (item is MeshNode node) results.Add(node);
        }

        results.Should().Contain(n => n.Path == "PartnerRe",
            "Space root node should appear in root namespace query (namespace picker)");
    }

    [Fact]
    public async Task SpaceChildren_VisibleAsDescendants()
    {
        await SeedSpaceAsync();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);

        var request = MeshQueryRequest.FromQuery("path:PartnerRe scope:descendants", "alice");
        var results = new List<MeshNode>();
        await foreach (var item in query.QueryAsync(request, _options, TestContext.Current.CancellationToken))
        {
            if (item is MeshNode node) results.Add(node);
        }

        results.Should().Contain(n => n.Path == "PartnerRe/AiConsulting",
            "Children under Space should be visible as descendants");
    }

    [Fact]
    public async Task SpaceRootNode_VisibleWithContextCreate()
    {
        await SeedSpaceAsync();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);

        // The RoutingMeshQueryProvider scopes fan-out queries as:
        //   DefaultPath=PartnerRe, Query="context:create scope:subtree is:main"
        // scope:subtree includes the root node itself (not just descendants).
        var request = MeshQueryRequest.FromQuery(
            "context:create scope:subtree is:main", "alice") with { DefaultPath = "PartnerRe" };
        var results = new List<MeshNode>();
        await foreach (var item in query.QueryAsync(request, _options, TestContext.Current.CancellationToken))
        {
            if (item is MeshNode node) results.Add(node);
        }

        results.Should().Contain(n => n.Path == "PartnerRe",
            "Space root node must appear in context:create queries â€” " +
            "otherwise the namespace picker won't show it");
    }
}
