using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Hosting.PostgreSql.Test;

[Collection("PostgreSql")]
public class QueryTests
{
    private readonly PostgreSqlFixture _fixture;
    private readonly JsonSerializerOptions _options = new();

    public QueryTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    private void SeedTestData()
    {
        var ct = TestContext.Current.CancellationToken;
        _fixture.CleanData().Should().Within(60.Seconds()).Emit();
        var adapter = _fixture.StorageAdapter;

        adapter.Write(new MeshNode("Story1", "ACME/Project")
        {
            Name = "Story One",
            NodeType = "Story",
            Content = JsonSerializer.Deserialize<object>("""{"status":"Open","priority":"High"}""", _options)
        }, _options).Should().Within(30.Seconds()).Emit();

        adapter.Write(new MeshNode("Story2", "ACME/Project")
        {
            Name = "Story Two",
            NodeType = "Story",
            Content = JsonSerializer.Deserialize<object>("""{"status":"Closed","priority":"Low"}""", _options)
        }, _options).Should().Within(30.Seconds()).Emit();

        adapter.Write(new MeshNode("Alice", "ACME/Team")
        {
            Name = "Alice",
            NodeType = "Person"
        }, _options).Should().Within(30.Seconds()).Emit();

        adapter.Write(new MeshNode("Project", "Contoso")
        {
            Name = "Contoso Project",
            NodeType = "Project"
        }, _options).Should().Within(30.Seconds()).Emit();

        // Grant Anonymous Read access so query tests work without explicit userId
        var ac = _fixture.AccessControl;
        ac.Grant("ACME", "Anonymous", "Read", isAllow: true, ct).Should().Within(30.Seconds()).Emit();
        ac.Grant("Contoso", "Anonymous", "Read", isAllow: true, ct).Should().Within(30.Seconds()).Emit();
    }

    [Fact]
    public void QueryByNodeType()
    {
        SeedTestData();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("nodeType:Story");

        var results = query.QueryList(request, _options, TestContext.Current.CancellationToken)
            .Should().Within(30.Seconds()).Emit();

        results.Should().HaveCount(2);
        results.Cast<MeshNode>().Should().AllSatisfy(n => n.NodeType.Should().Be("Story"));
    }

    [Fact]
    public void QueryWithPathScope_Children()
    {
        SeedTestData();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("namespace:ACME/Project");

        var results = query.QueryList(request, _options, TestContext.Current.CancellationToken)
            .Should().Within(30.Seconds()).Emit();

        results.Should().HaveCount(2);
        results.Cast<MeshNode>().Select(n => n.Id).Should().BeEquivalentTo(new[] { "Story1", "Story2" }, JsonSerializerOptions.Default);
    }

    [Fact]
    public void QueryWithPathScope_Descendants()
    {
        SeedTestData();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("path:ACME scope:descendants");

        var results = query.QueryList(request, _options, TestContext.Current.CancellationToken)
            .Should().Within(30.Seconds()).Emit();

        // ACME/Project/Story1, ACME/Project/Story2, ACME/Team/Alice
        results.Should().HaveCount(3);
    }

    [Fact]
    public void QueryWithPathScope_Exact()
    {
        SeedTestData();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("path:ACME/Project/Story1");

        var results = query.QueryList(request, _options, TestContext.Current.CancellationToken)
            .Should().Within(30.Seconds()).Emit();

        results.Should().HaveCount(1);
        ((MeshNode)results[0]).Id.Should().Be("Story1");
    }

    [Fact]
    public void QueryWithPathScope_Subtree()
    {
        SeedTestData();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);

        // First add a node at ACME itself
        _fixture.StorageAdapter.Write(new MeshNode("ACME")
        {
            Name = "ACME Corp",
            NodeType = "Space"
        }, _options).Should().Within(30.Seconds()).Emit();

        var request = MeshQueryRequest.FromQuery("path:ACME scope:subtree");

        var results = query.QueryList(request, _options, TestContext.Current.CancellationToken)
            .Should().Within(30.Seconds()).Emit();

        // ACME + ACME/Project/Story1, ACME/Project/Story2, ACME/Team/Alice
        results.Should().HaveCount(4);
    }

    [Fact]
    public void QueryWithPathScope_Ancestors()
    {
        SeedTestData();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);

        // Add ancestor nodes for ACME/Project/Story1
        _fixture.StorageAdapter.Write(new MeshNode("ACME")
        {
            Name = "ACME Corp",
            NodeType = "Space"
        }, _options).Should().Within(30.Seconds()).Emit();
        _fixture.StorageAdapter.Write(new MeshNode("Project", "ACME")
        {
            Name = "ACME Project",
            NodeType = "Project"
        }, _options).Should().Within(30.Seconds()).Emit();

        var request = MeshQueryRequest.FromQuery("path:ACME/Project/Story1 scope:ancestors");

        var results = query.QueryList(request, _options, TestContext.Current.CancellationToken)
            .Should().Within(30.Seconds()).Emit();

        // Ancestors: ACME, ACME/Project (NOT Story1 itself)
        results.Should().HaveCount(2);
        results.Cast<MeshNode>().Select(n => n.Path).Should()
            .BeEquivalentTo(new[] { "ACME", "ACME/Project" }, JsonSerializerOptions.Default);
    }

    [Fact]
    public void QueryWithLimit()
    {
        SeedTestData();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = new MeshQueryRequest { Query = "nodeType:Story", Limit = 1 };

        var results = query.QueryList(request, _options, TestContext.Current.CancellationToken)
            .Should().Within(30.Seconds()).Emit();

        results.Should().HaveCount(1);
    }

    [Fact]
    public void QueryWithSkip()
    {
        SeedTestData();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = new MeshQueryRequest
        {
            Query = "nodeType:Story sort:name",
            Skip = 1,
            Limit = 10
        };

        var results = query.QueryList(request, _options, TestContext.Current.CancellationToken)
            .Should().Within(30.Seconds()).Emit();

        results.Should().HaveCount(1);
        ((MeshNode)results[0]).Name.Should().Be("Story Two");
    }

    [Fact]
    public void QueryWithSort()
    {
        SeedTestData();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("nodeType:Story sort:name");

        var results = query.QueryList(request, _options, TestContext.Current.CancellationToken)
            .Should().Within(30.Seconds()).Emit();

        results.Should().HaveCount(2);
        ((MeshNode)results[0]).Name.Should().Be("Story One");
        ((MeshNode)results[1]).Name.Should().Be("Story Two");
    }

    [Fact]
    public void QueryWithSortDescending()
    {
        SeedTestData();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("nodeType:Story sort:name-desc");

        var results = query.QueryList(request, _options, TestContext.Current.CancellationToken)
            .Should().Within(30.Seconds()).Emit();

        results.Should().HaveCount(2);
        ((MeshNode)results[0]).Name.Should().Be("Story Two");
        ((MeshNode)results[1]).Name.Should().Be("Story One");
    }

    [Fact]
    public void QueryWithDefaultPathFallsBackToChildren()
    {
        SeedTestData();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = new MeshQueryRequest
        {
            Query = "nodeType:Story",
            DefaultPath = "ACME/Project"
        };

        var results = query.QueryList(request, _options, TestContext.Current.CancellationToken)
            .Should().Within(30.Seconds()).Emit();

        results.Should().HaveCount(2);
    }
}
