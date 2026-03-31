using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Tests that all documented query syntax features work correctly in PostgreSQL.
/// Covers: text search, negation, wildcards, comparison operators, list values,
/// namespace qualifier, select projection, context filtering.
/// </summary>
[Collection("PostgreSql")]
public class QuerySyntaxTests
{
    private readonly PostgreSqlFixture _fixture;
    private readonly JsonSerializerOptions _options = new();

    public QuerySyntaxTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task SeedTestDataAsync()
    {
        await _fixture.CleanDataAsync();
        var adapter = _fixture.StorageAdapter;
        var ac = _fixture.AccessControl;

        await adapter.WriteAsync(new MeshNode("Story1", "ACME/Project")
        {
            Name = "Claims Processing",
            NodeType = "Story",
            Content = JsonSerializer.Deserialize<object>("""{"status":"Open","priority":"High","points":8}""", _options)
        }, _options, TestContext.Current.CancellationToken);

        await adapter.WriteAsync(new MeshNode("Story2", "ACME/Project")
        {
            Name = "User Authentication",
            NodeType = "Story",
            Content = JsonSerializer.Deserialize<object>("""{"status":"Closed","priority":"Low","points":3}""", _options)
        }, _options, TestContext.Current.CancellationToken);

        await adapter.WriteAsync(new MeshNode("Story3", "ACME/Project")
        {
            Name = "Claims Dashboard",
            NodeType = "Story",
            Content = JsonSerializer.Deserialize<object>("""{"status":"Open","priority":"Medium","points":5}""", _options)
        }, _options, TestContext.Current.CancellationToken);

        await adapter.WriteAsync(new MeshNode("Bug1", "ACME/Project")
        {
            Name = "Login Crash",
            NodeType = "Bug",
            Content = JsonSerializer.Deserialize<object>("""{"status":"Open","priority":"High","points":2}""", _options)
        }, _options, TestContext.Current.CancellationToken);

        await adapter.WriteAsync(new MeshNode("Alice", "ACME/Team")
        {
            Name = "Alice Smith",
            NodeType = "Person"
        }, _options, TestContext.Current.CancellationToken);

        await adapter.WriteAsync(new MeshNode("Project", "Contoso")
        {
            Name = "Contoso Project",
            NodeType = "Project"
        }, _options, TestContext.Current.CancellationToken);

        // Grant Anonymous access so tests work without explicit userId
        await ac.GrantAsync("ACME", "Anonymous", "Read", isAllow: true, TestContext.Current.CancellationToken);
        await ac.GrantAsync("Contoso", "Anonymous", "Read", isAllow: true, TestContext.Current.CancellationToken);
    }

    #region Text Search

    [Fact]
    public async Task TextSearch_SingleTerm()
    {
        await SeedTestDataAsync();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("claims path:ACME scope:descendants");

        var results = await CollectResults(query, request);

        results.Should().HaveCount(2);
        results.Select(n => n.Name).Should().BeEquivalentTo("Claims Processing", "Claims Dashboard");
    }

    [Fact]
    public async Task TextSearch_MultipletermsAllRequired()
    {
        await SeedTestDataAsync();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("claims dashboard path:ACME scope:descendants");

        var results = await CollectResults(query, request);

        results.Should().HaveCount(1);
        results[0].Name.Should().Be("Claims Dashboard");
    }

    #endregion

    #region Negation

    [Fact]
    public async Task Negation_ExcludesMatchingNodes()
    {
        await SeedTestDataAsync();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("-nodeType:Story namespace:ACME/Project");

        var results = await CollectResults(query, request);

        results.Should().HaveCount(1);
        results[0].NodeType.Should().Be("Bug");
    }

    #endregion

    #region Wildcard Patterns

    [Fact]
    public async Task Wildcard_Contains()
    {
        await SeedTestDataAsync();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("name:*claims* path:ACME scope:descendants");

        var results = await CollectResults(query, request);

        results.Should().HaveCount(2);
        results.Select(n => n.Name).Should().BeEquivalentTo("Claims Processing", "Claims Dashboard");
    }

    [Fact]
    public async Task Wildcard_StartsWith()
    {
        await SeedTestDataAsync();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("name:Claims* path:ACME scope:descendants");

        var results = await CollectResults(query, request);

        results.Should().HaveCount(2);
    }

    [Fact]
    public async Task Wildcard_EndsWith()
    {
        await SeedTestDataAsync();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("name:*Dashboard path:ACME scope:descendants");

        var results = await CollectResults(query, request);

        results.Should().HaveCount(1);
        results[0].Name.Should().Be("Claims Dashboard");
    }

    #endregion

    #region Comparison Operators

    [Fact]
    public async Task ComparisonOperator_GreaterThan()
    {
        await SeedTestDataAsync();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("points:>5 namespace:ACME/Project");

        var results = await CollectResults(query, request);

        // Only Story1 has points=8 which is > 5
        results.Should().HaveCount(1);
        results[0].Name.Should().Be("Claims Processing");
    }

    [Fact]
    public async Task ComparisonOperator_LessThanOrEqual()
    {
        await SeedTestDataAsync();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("points:<=3 namespace:ACME/Project");

        var results = await CollectResults(query, request);

        // Story2 (3) and Bug1 (2)
        results.Should().HaveCount(2);
    }

    #endregion

    #region List Values (OR)

    [Fact]
    public async Task ListValues_InOperator()
    {
        await SeedTestDataAsync();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("nodeType:(Story OR Bug) path:ACME scope:descendants");

        var results = await CollectResults(query, request);

        results.Should().HaveCount(4);
        results.Select(n => n.NodeType).Distinct()
            .Should().BeEquivalentTo("Story", "Bug");
    }

    [Fact]
    public async Task ListValues_NotIn()
    {
        await SeedTestDataAsync();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("-nodeType:(Story OR Bug) path:ACME scope:descendants");

        var results = await CollectResults(query, request);

        // Only Alice (Person) remains
        results.Should().HaveCount(1);
        results[0].NodeType.Should().Be("Person");
    }

    #endregion

    #region Namespace Qualifier

    [Fact]
    public async Task Namespace_DefaultsToChildren()
    {
        await SeedTestDataAsync();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("namespace:ACME/Project");

        var results = await CollectResults(query, request);

        // Immediate children of ACME/Project: Story1, Story2, Story3, Bug1
        results.Should().HaveCount(4);
    }

    [Fact]
    public async Task Namespace_WithDescendants()
    {
        await SeedTestDataAsync();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("namespace:ACME scope:descendants");

        var results = await CollectResults(query, request);

        // All nodes under ACME: 3 stories + 1 bug + 1 person = 5
        results.Should().HaveCount(5);
    }

    #endregion

    #region Select Projection

    [Fact]
    public async Task Select_SingleProperty()
    {
        await SeedTestDataAsync();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("nodeType:Story select:name path:ACME scope:descendants sort:name");

        var results = new List<object>();
        await foreach (var item in query.QueryAsync(request, _options, TestContext.Current.CancellationToken))
            results.Add(item);

        results.Should().HaveCount(3);
        // Select returns dictionaries, not MeshNode
        results[0].Should().BeAssignableTo<IDictionary<string, object?>>();
        var dict = (IDictionary<string, object?>)results[0];
        dict.Should().ContainKey("name");
    }

    [Fact]
    public async Task Select_MultipleProperties()
    {
        await SeedTestDataAsync();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("path:ACME/Project/Story1 select:name,nodeType,path");

        var results = new List<object>();
        await foreach (var item in query.QueryAsync(request, _options, TestContext.Current.CancellationToken))
            results.Add(item);

        results.Should().HaveCount(1);
        var dict = (IDictionary<string, object?>)results[0];
        dict.Should().ContainKey("name");
        dict.Should().ContainKey("nodeType");
        dict.Should().ContainKey("path");
    }

    #endregion

    #region Context Filtering

    [Fact]
    public async Task ContextFilter_ExcludesTypesViaQueryString()
    {
        await SeedTestDataAsync();

        // Build a MeshConfiguration where "Bug" type is excluded from "search" context
        var typeNodes = new Dictionary<string, MeshNode>
        {
            ["Bug"] = new MeshNode("Bug") { NodeType = "NodeType", Name = "Bug", ExcludeFromContext = ["search"] },
            ["Story"] = new MeshNode("Story") { NodeType = "NodeType", Name = "Story" },
            ["Person"] = new MeshNode("Person") { NodeType = "NodeType", Name = "Person" }
        };
        var meshConfig = new MeshConfiguration(typeNodes);

        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter, meshConfiguration: meshConfig);
        var request = MeshQueryRequest.FromQuery("namespace:ACME/Project context:search");

        var results = await CollectResults(query, request);

        // Bug1 should be excluded because Bug type is excluded from "search" context
        results.Should().HaveCount(3);
        results.Select(n => n.NodeType).Should().OnlyContain(t => t == "Story");
    }

    [Fact]
    public async Task ContextFilter_NoContextReturnsAll()
    {
        await SeedTestDataAsync();

        var typeNodes = new Dictionary<string, MeshNode>
        {
            ["Bug"] = new MeshNode("Bug") { NodeType = "NodeType", Name = "Bug", ExcludeFromContext = ["search"] },
            ["Story"] = new MeshNode("Story") { NodeType = "NodeType", Name = "Story" }
        };
        var meshConfig = new MeshConfiguration(typeNodes);

        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter, meshConfiguration: meshConfig);
        var request = MeshQueryRequest.FromQuery("namespace:ACME/Project");

        var results = await CollectResults(query, request);

        // Without context, all nodes are returned including Bug
        results.Should().HaveCount(4);
    }

    [Fact]
    public async Task ContextFilter_DifferentContextDoesNotExclude()
    {
        await SeedTestDataAsync();

        var typeNodes = new Dictionary<string, MeshNode>
        {
            ["Bug"] = new MeshNode("Bug") { NodeType = "NodeType", Name = "Bug", ExcludeFromContext = ["search"] },
            ["Story"] = new MeshNode("Story") { NodeType = "NodeType", Name = "Story" }
        };
        var meshConfig = new MeshConfiguration(typeNodes);

        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter, meshConfiguration: meshConfig);
        // Use "create" context — Bug is only excluded from "search", not "create"
        var request = MeshQueryRequest.FromQuery("namespace:ACME/Project context:create");

        var results = await CollectResults(query, request);

        results.Should().HaveCount(4);
    }

    [Fact]
    public async Task ContextFilter_ViaRequestContext()
    {
        await SeedTestDataAsync();

        var typeNodes = new Dictionary<string, MeshNode>
        {
            ["Person"] = new MeshNode("Person") { NodeType = "NodeType", Name = "Person", ExcludeFromContext = ["search"] },
            ["Story"] = new MeshNode("Story") { NodeType = "NodeType", Name = "Story" }
        };
        var meshConfig = new MeshConfiguration(typeNodes);

        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter, meshConfiguration: meshConfig);
        // Set context via MeshQueryRequest.Context (takes precedence over query string)
        var request = new MeshQueryRequest
        {
            Query = "path:ACME scope:descendants",
            Context = "search"
        };

        var results = await CollectResults(query, request);

        // Person (Alice) should be excluded from "search" context
        results.Should().NotContain(n => n.NodeType == "Person");
        results.Should().HaveCountGreaterThan(0);
    }

    [Fact]
    public async Task ContextFilter_MultipleTypesExcluded()
    {
        await SeedTestDataAsync();

        var typeNodes = new Dictionary<string, MeshNode>
        {
            ["Bug"] = new MeshNode("Bug") { NodeType = "NodeType", Name = "Bug", ExcludeFromContext = ["create"] },
            ["Person"] = new MeshNode("Person") { NodeType = "NodeType", Name = "Person", ExcludeFromContext = ["create"] },
            ["Story"] = new MeshNode("Story") { NodeType = "NodeType", Name = "Story" }
        };
        var meshConfig = new MeshConfiguration(typeNodes);

        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter, meshConfiguration: meshConfig);
        var request = MeshQueryRequest.FromQuery("path:ACME scope:descendants context:create");

        var results = await CollectResults(query, request);

        // Only Story nodes should remain
        results.Should().HaveCount(3);
        results.Should().OnlyContain(n => n.NodeType == "Story");
    }

    #endregion

    #region Content Field Queries

    [Fact]
    public async Task ContentField_EqualityFilter()
    {
        await SeedTestDataAsync();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("status:Open namespace:ACME/Project");

        var results = await CollectResults(query, request);

        // Story1 (Open), Story3 (Open), Bug1 (Open)
        results.Should().HaveCount(3);
    }

    [Fact]
    public async Task ContentField_CombinedWithNodeTypeFilter()
    {
        await SeedTestDataAsync();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("nodeType:Story status:Open path:ACME scope:descendants");

        var results = await CollectResults(query, request);

        // Only Story1 and Story3 (Open Stories)
        results.Should().HaveCount(2);
    }

    #endregion

    #region Combined Features

    [Fact]
    public async Task CombinedQuery_FilterSortLimit()
    {
        await SeedTestDataAsync();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = new MeshQueryRequest
        {
            Query = "nodeType:Story sort:name path:ACME scope:descendants",
            Limit = 2
        };

        var results = await CollectResults(query, request);

        // 3 stories sorted: Claims Dashboard, Claims Processing, User Authentication
        // Limit 2 → first two.
        results.Should().HaveCount(2);
        results[0].Name.Should().Be("Claims Dashboard");
        results[1].Name.Should().Be("Claims Processing");
    }

    [Fact]
    public async Task CombinedQuery_WildcardWithSort()
    {
        await SeedTestDataAsync();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("name:*claims* sort:name-desc path:ACME scope:descendants");

        var results = await CollectResults(query, request);

        results.Should().HaveCount(2);
        results[0].Name.Should().Be("Claims Processing");
        results[1].Name.Should().Be("Claims Dashboard");
    }

    #endregion

    private async Task<List<MeshNode>> CollectResults(PostgreSqlMeshQuery query, MeshQueryRequest request)
    {
        var results = new List<MeshNode>();
        await foreach (var item in query.QueryAsync(request, _options, TestContext.Current.CancellationToken))
        {
            if (item is MeshNode node)
                results.Add(node);
        }
        return results;
    }
}
