using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

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

    private async Task SeedTestDataAsync()
    {
        await _fixture.CleanDataAsync();
        var adapter = _fixture.StorageAdapter;

        await adapter.WriteAsync(new MeshNode("Story1", "ACME/Software/Project")
        {
            Name = "Story One",
            NodeType = "Story",
            Content = JsonSerializer.Deserialize<object>("""{"status":"Open","priority":"High"}""", _options)
        }, _options);

        await adapter.WriteAsync(new MeshNode("Story2", "ACME/Software/Project")
        {
            Name = "Story Two",
            NodeType = "Story",
            Content = JsonSerializer.Deserialize<object>("""{"status":"Closed","priority":"Low"}""", _options)
        }, _options);

        await adapter.WriteAsync(new MeshNode("Alice", "ACME/Software/Team")
        {
            Name = "Alice",
            NodeType = "Person"
        }, _options);

        await adapter.WriteAsync(new MeshNode("Project", "Contoso")
        {
            Name = "Contoso Project",
            NodeType = "Project"
        }, _options);

        // Grant Public Read access so query tests work without explicit userId
        var ac = _fixture.AccessControl;
        await ac.GrantAsync("ACME", "Public", "Read", isAllow: true);
        await ac.GrantAsync("Contoso", "Public", "Read", isAllow: true);
    }

    [Fact]
    public async Task QueryByNodeType()
    {
        await SeedTestDataAsync();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("nodeType:Story");

        var results = new List<object>();
        await foreach (var item in query.QueryAsync(request, _options))
            results.Add(item);

        results.Should().HaveCount(2);
        results.Cast<MeshNode>().Should().AllSatisfy(n => n.NodeType.Should().Be("Story"));
    }

    [Fact]
    public async Task QueryWithPathScope_Children()
    {
        await SeedTestDataAsync();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("path:ACME/Software/Project scope:children");

        var results = new List<object>();
        await foreach (var item in query.QueryAsync(request, _options))
            results.Add(item);

        results.Should().HaveCount(2);
        results.Cast<MeshNode>().Select(n => n.Id).Should().BeEquivalentTo("Story1", "Story2");
    }

    [Fact]
    public async Task QueryWithPathScope_Descendants()
    {
        await SeedTestDataAsync();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("path:ACME scope:descendants");

        var results = new List<object>();
        await foreach (var item in query.QueryAsync(request, _options))
            results.Add(item);

        // ACME/Project/Story1, ACME/Project/Story2, ACME/Team/Alice
        results.Should().HaveCount(3);
    }

    [Fact]
    public async Task QueryWithPathScope_Exact()
    {
        await SeedTestDataAsync();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("path:ACME/Software/Project/Story1 scope:exact");

        var results = new List<object>();
        await foreach (var item in query.QueryAsync(request, _options))
            results.Add(item);

        results.Should().HaveCount(1);
        ((MeshNode)results[0]).Id.Should().Be("Story1");
    }

    [Fact]
    public async Task QueryWithPathScope_Subtree()
    {
        await SeedTestDataAsync();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);

        // First add a node at ACME itself
        await _fixture.StorageAdapter.WriteAsync(new MeshNode("ACME")
        {
            Name = "ACME Corp",
            NodeType = "Organization"
        }, _options);

        var request = MeshQueryRequest.FromQuery("path:ACME scope:subtree");

        var results = new List<object>();
        await foreach (var item in query.QueryAsync(request, _options))
            results.Add(item);

        // ACME + ACME/Project/Story1, ACME/Project/Story2, ACME/Team/Alice
        results.Should().HaveCount(4);
    }

    [Fact]
    public async Task QueryWithPathScope_Ancestors()
    {
        await SeedTestDataAsync();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);

        // Add ancestor nodes for ACME/Software/Project/Story1
        await _fixture.StorageAdapter.WriteAsync(new MeshNode("ACME")
        {
            Name = "ACME Corp",
            NodeType = "Organization"
        }, _options);
        await _fixture.StorageAdapter.WriteAsync(new MeshNode("Software", "ACME")
        {
            Name = "ACME Software",
            NodeType = "Division"
        }, _options);
        await _fixture.StorageAdapter.WriteAsync(new MeshNode("Project", "ACME/Software")
        {
            Name = "ACME Software Project",
            NodeType = "Project"
        }, _options);

        var request = MeshQueryRequest.FromQuery("path:ACME/Software/Project/Story1 scope:ancestors");

        var results = new List<object>();
        await foreach (var item in query.QueryAsync(request, _options))
            results.Add(item);

        // Ancestors: ACME, ACME/Software, ACME/Software/Project (NOT Story1 itself)
        results.Should().HaveCount(3);
        results.Cast<MeshNode>().Select(n => n.Path).Should()
            .BeEquivalentTo("ACME", "ACME/Software", "ACME/Software/Project");
    }

    [Fact]
    public async Task QueryWithLimit()
    {
        await SeedTestDataAsync();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = new MeshQueryRequest { Query = "nodeType:Story", Limit = 1 };

        var results = new List<object>();
        await foreach (var item in query.QueryAsync(request, _options))
            results.Add(item);

        results.Should().HaveCount(1);
    }

    [Fact]
    public async Task QueryWithSkip()
    {
        await SeedTestDataAsync();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = new MeshQueryRequest
        {
            Query = "nodeType:Story sort:name",
            Skip = 1,
            Limit = 10
        };

        var results = new List<object>();
        await foreach (var item in query.QueryAsync(request, _options))
            results.Add(item);

        results.Should().HaveCount(1);
        ((MeshNode)results[0]).Name.Should().Be("Story Two");
    }

    [Fact]
    public async Task QueryWithSort()
    {
        await SeedTestDataAsync();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("nodeType:Story sort:name");

        var results = new List<object>();
        await foreach (var item in query.QueryAsync(request, _options))
            results.Add(item);

        results.Should().HaveCount(2);
        ((MeshNode)results[0]).Name.Should().Be("Story One");
        ((MeshNode)results[1]).Name.Should().Be("Story Two");
    }

    [Fact]
    public async Task QueryWithSortDescending()
    {
        await SeedTestDataAsync();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("nodeType:Story sort:name-desc");

        var results = new List<object>();
        await foreach (var item in query.QueryAsync(request, _options))
            results.Add(item);

        results.Should().HaveCount(2);
        ((MeshNode)results[0]).Name.Should().Be("Story Two");
        ((MeshNode)results[1]).Name.Should().Be("Story One");
    }

    [Fact]
    public async Task QueryWithDefaultPathFallsBackToChildren()
    {
        await SeedTestDataAsync();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = new MeshQueryRequest
        {
            Query = "nodeType:Story",
            DefaultPath = "ACME/Software/Project"
        };

        var results = new List<object>();
        await foreach (var item in query.QueryAsync(request, _options))
            results.Add(item);

        results.Should().HaveCount(2);
    }
}
