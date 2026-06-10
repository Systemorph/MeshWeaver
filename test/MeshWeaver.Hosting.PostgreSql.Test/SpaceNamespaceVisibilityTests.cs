using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Graph;
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

    private List<MeshNode> Query(PostgreSqlMeshQuery query, MeshQueryRequest request)
        => query.QueryList(request, _options, TestContext.Current.CancellationToken)
            .Should().Within(30.Seconds()).Emit()
            .OfType<MeshNode>().ToList();

    private void SeedSpace()
    {
        var ct = TestContext.Current.CancellationToken;
        _fixture.CleanData().Should().Within(60.Seconds()).Emit();
        var adapter = _fixture.StorageAdapter;
        var ac = _fixture.AccessControl;

        // Seed Space node at root path (same as partition namespace)
        adapter.Write(new MeshNode("PartnerRe")
        {
            Name = "PartnerRe AG",
            NodeType = "Space",
            Content = new Space { Name = "PartnerRe AG" }
        }, _options).Should().Within(30.Seconds()).Emit();

        // Seed a child node under the Space
        adapter.Write(new MeshNode("AiConsulting", "PartnerRe")
        {
            Name = "AI Consulting",
            NodeType = "Group"
        }, _options).Should().Within(30.Seconds()).Emit();

        // Register Space as public-read
        ac.SyncNodeTypePermissionsAsync([
            new NodeTypePermission("Space", PublicRead: true)
        ], ct).Run().Should().Within(30.Seconds()).Emit();

        // Grant authenticated user access
        ac.Grant("PartnerRe", "alice", "Read", isAllow: true, ct).Should().Within(30.Seconds()).Emit();
        ac.Grant("PartnerRe", "alice", "Create", isAllow: true, ct).Should().Within(30.Seconds()).Emit();
    }

    [Fact]
    public void SpaceRootNode_VisibleByPath()
    {
        SeedSpace();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);

        var request = MeshQueryRequest.FromQuery("path:PartnerRe", "alice");
        var results = Query(query, request);

        results.Should().HaveCount(1, "Space root node should be queryable by path");
        results[0].Name.Should().Be("PartnerRe AG");
        results[0].NodeType.Should().Be("Space");
    }

    [Fact]
    public void SpaceRootNode_VisibleByNodeType()
    {
        SeedSpace();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);

        var request = MeshQueryRequest.FromQuery("nodeType:Space", "alice");
        var results = Query(query, request);

        results.Should().Contain(n => n.Path == "PartnerRe",
            "Space root node should appear in nodeType:Space queries");
    }

    [Fact]
    public void SpaceRootNode_VisibleInNamespaceQuery()
    {
        SeedSpace();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);

        // This is how the namespace picker queries â€” root-level nodes
        var request = MeshQueryRequest.FromQuery("namespace:", "alice");
        var results = Query(query, request);

        results.Should().Contain(n => n.Path == "PartnerRe",
            "Space root node should appear in root namespace query (namespace picker)");
    }

    [Fact]
    public void SpaceChildren_VisibleAsDescendants()
    {
        SeedSpace();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);

        var request = MeshQueryRequest.FromQuery("path:PartnerRe scope:descendants", "alice");
        var results = Query(query, request);

        results.Should().Contain(n => n.Path == "PartnerRe/AiConsulting",
            "Children under Space should be visible as descendants");
    }

    [Fact]
    public void SpaceRootNode_VisibleWithContextCreate()
    {
        SeedSpace();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);

        // The RoutingMeshQueryProvider scopes fan-out queries as:
        //   DefaultPath=PartnerRe, Query="context:create scope:subtree is:main"
        // scope:subtree includes the root node itself (not just descendants).
        var request = MeshQueryRequest.FromQuery(
            "context:create scope:subtree is:main", "alice") with { DefaultPath = "PartnerRe" };
        var results = Query(query, request);

        results.Should().Contain(n => n.Path == "PartnerRe",
            "Space root node must appear in context:create queries â€” " +
            "otherwise the namespace picker won't show it");
    }
}
