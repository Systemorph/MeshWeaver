using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;
using MeshWeaver.Fixture;

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

    private void SeedTestData()
    {
        var ct = TestContext.Current.CancellationToken;
        _fixture.CleanData().Should().Within(60.Seconds()).Emit();
        var adapter = _fixture.StorageAdapter;
        var ac = _fixture.AccessControl;

        adapter.Write(new MeshNode("Story1", "ACME/Project")
        {
            Name = "Claims Processing",
            NodeType = "Story",
            Content = JsonSerializer.Deserialize<object>("""{"status":"Open","priority":"High","points":8}""", _options)
        }, _options).Should().Within(30.Seconds()).Emit();

        adapter.Write(new MeshNode("Story2", "ACME/Project")
        {
            Name = "User Authentication",
            NodeType = "Story",
            Content = JsonSerializer.Deserialize<object>("""{"status":"Closed","priority":"Low","points":3}""", _options)
        }, _options).Should().Within(30.Seconds()).Emit();

        adapter.Write(new MeshNode("Story3", "ACME/Project")
        {
            Name = "Claims Dashboard",
            NodeType = "Story",
            Content = JsonSerializer.Deserialize<object>("""{"status":"Open","priority":"Medium","points":5}""", _options)
        }, _options).Should().Within(30.Seconds()).Emit();

        adapter.Write(new MeshNode("Bug1", "ACME/Project")
        {
            Name = "Login Crash",
            NodeType = "Bug",
            Content = JsonSerializer.Deserialize<object>("""{"status":"Open","priority":"High","points":2}""", _options)
        }, _options).Should().Within(30.Seconds()).Emit();

        adapter.Write(new MeshNode("Alice", "ACME/Team")
        {
            Name = "Alice Smith",
            NodeType = "Person"
        }, _options).Should().Within(30.Seconds()).Emit();

        adapter.Write(new MeshNode("Project", "Contoso")
        {
            Name = "Contoso Project",
            NodeType = "Project"
        }, _options).Should().Within(30.Seconds()).Emit();

        // Grant Anonymous access so tests work without explicit userId
        ac.Grant("ACME", "Anonymous", "Read", isAllow: true, ct).Should().Within(30.Seconds()).Emit();
        ac.Grant("Contoso", "Anonymous", "Read", isAllow: true, ct).Should().Within(30.Seconds()).Emit();
    }

    #region Text Search

    [Fact]
    public void TextSearch_SingleTerm()
    {
        SeedTestData();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("claims path:ACME scope:descendants");

        var results = CollectResults(query, request);

        results.Should().HaveCount(2);
        results.Select(n => n.Name).Should().BeEquivalentTo(new[] { "Claims Processing", "Claims Dashboard" }, JsonSerializerOptions.Default);
    }

    [Fact]
    public void TextSearch_MultipletermsAllRequired()
    {
        SeedTestData();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("claims dashboard path:ACME scope:descendants");

        var results = CollectResults(query, request);

        results.Should().HaveCount(1);
        results[0].Name.Should().Be("Claims Dashboard");
    }

    #endregion

    #region Negation

    [Fact]
    public void Negation_ExcludesMatchingNodes()
    {
        SeedTestData();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("-nodeType:Story namespace:ACME/Project");

        var results = CollectResults(query, request);

        results.Should().HaveCount(1);
        results[0].NodeType.Should().Be("Bug");
    }

    #endregion

    #region Wildcard Patterns

    [Fact]
    public void Wildcard_Contains()
    {
        SeedTestData();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("name:*claims* path:ACME scope:descendants");

        var results = CollectResults(query, request);

        results.Should().HaveCount(2);
        results.Select(n => n.Name).Should().BeEquivalentTo(new[] { "Claims Processing", "Claims Dashboard" }, JsonSerializerOptions.Default);
    }

    [Fact]
    public void Wildcard_StartsWith()
    {
        SeedTestData();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("name:Claims* path:ACME scope:descendants");

        var results = CollectResults(query, request);

        results.Should().HaveCount(2);
    }

    [Fact]
    public void Wildcard_EndsWith()
    {
        SeedTestData();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("name:*Dashboard path:ACME scope:descendants");

        var results = CollectResults(query, request);

        results.Should().HaveCount(1);
        results[0].Name.Should().Be("Claims Dashboard");
    }

    #endregion

    #region Comparison Operators

    [Fact]
    public void ComparisonOperator_GreaterThan()
    {
        SeedTestData();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("points:>5 namespace:ACME/Project");

        var results = CollectResults(query, request);

        // Only Story1 has points=8 which is > 5
        results.Should().HaveCount(1);
        results[0].Name.Should().Be("Claims Processing");
    }

    [Fact]
    public void ComparisonOperator_LessThanOrEqual()
    {
        SeedTestData();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("points:<=3 namespace:ACME/Project");

        var results = CollectResults(query, request);

        // Story2 (3) and Bug1 (2)
        results.Should().HaveCount(2);
    }

    #endregion

    #region List Values (OR)

    [Fact]
    public void ListValues_InOperator()
    {
        SeedTestData();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("nodeType:(Story OR Bug) path:ACME scope:descendants");

        var results = CollectResults(query, request);

        results.Should().HaveCount(4);
        results.Select(n => n.NodeType).Distinct()
            .Should().BeEquivalentTo(new[] { "Story", "Bug" }, JsonSerializerOptions.Default);
    }

    [Fact]
    public void ListValues_NotIn()
    {
        SeedTestData();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("-nodeType:(Story OR Bug) path:ACME scope:descendants");

        var results = CollectResults(query, request);

        // Only Alice (Person) remains
        results.Should().HaveCount(1);
        results[0].NodeType.Should().Be("Person");
    }

    #endregion

    #region Namespace Qualifier

    [Fact]
    public void Namespace_DefaultsToChildren()
    {
        SeedTestData();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("namespace:ACME/Project");

        var results = CollectResults(query, request);

        // Immediate children of ACME/Project: Story1, Story2, Story3, Bug1
        results.Should().HaveCount(4);
    }

    [Fact]
    public void Namespace_WithDescendants()
    {
        SeedTestData();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("namespace:ACME scope:descendants");

        var results = CollectResults(query, request);

        // All nodes under ACME: 3 stories + 1 bug + 1 person = 5
        results.Should().HaveCount(5);
    }

    #endregion

    #region Select Projection

    [Fact]
    public void Select_SingleProperty()
    {
        SeedTestData();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("nodeType:Story select:name path:ACME scope:descendants sort:name");

        var results = query.QueryList(request, _options, TestContext.Current.CancellationToken)
            .Should().Within(30.Seconds()).Emit();

        results.Should().HaveCount(3);
        // Select returns dictionaries, not MeshNode
        results[0].Should().BeAssignableTo<IDictionary<string, object?>>();
        var dict = (IDictionary<string, object?>)results[0];
        dict.Should().ContainKey("name");
    }

    [Fact]
    public void Select_MultipleProperties()
    {
        SeedTestData();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("path:ACME/Project/Story1 select:name,nodeType,path");

        var results = query.QueryList(request, _options, TestContext.Current.CancellationToken)
            .Should().Within(30.Seconds()).Emit();

        results.Should().HaveCount(1);
        var dict = (IDictionary<string, object?>)results[0];
        dict.Should().ContainKey("name");
        dict.Should().ContainKey("nodeType");
        dict.Should().ContainKey("path");
    }

    #endregion

    #region Context Filtering

    [Fact]
    public void ContextFilter_ExcludesTypesViaQueryString()
    {
        SeedTestData();

        // Build a MeshConfiguration where "Bug" type is excluded from "search" context
        var typeNodes = new MeshNode[]
        {
            new("Bug") { NodeType = "NodeType", Name = "Bug", ExcludeFromContext = ["search"] },
            new("Story") { NodeType = "NodeType", Name = "Story" },
            new("Person") { NodeType = "NodeType", Name = "Person" }
        };
        var meshConfig = new MeshConfiguration(typeNodes);

        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter, meshConfiguration: meshConfig);
        var request = MeshQueryRequest.FromQuery("namespace:ACME/Project context:search");

        var results = CollectResults(query, request);

        // Bug1 should be excluded because Bug type is excluded from "search" context
        results.Should().HaveCount(3);
        results.Select(n => n.NodeType).Should().OnlyContain(t => t == "Story");
    }

    [Fact]
    public void ContextFilter_NoContextReturnsAll()
    {
        SeedTestData();

        var typeNodes = new MeshNode[]
        {
            new("Bug") { NodeType = "NodeType", Name = "Bug", ExcludeFromContext = ["search"] },
            new("Story") { NodeType = "NodeType", Name = "Story" }
        };
        var meshConfig = new MeshConfiguration(typeNodes);

        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter, meshConfiguration: meshConfig);
        var request = MeshQueryRequest.FromQuery("namespace:ACME/Project");

        var results = CollectResults(query, request);

        // Without context, all nodes are returned including Bug
        results.Should().HaveCount(4);
    }

    [Fact]
    public void ContextFilter_DifferentContextDoesNotExclude()
    {
        SeedTestData();

        var typeNodes = new MeshNode[]
        {
            new("Bug") { NodeType = "NodeType", Name = "Bug", ExcludeFromContext = ["search"] },
            new("Story") { NodeType = "NodeType", Name = "Story" }
        };
        var meshConfig = new MeshConfiguration(typeNodes);

        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter, meshConfiguration: meshConfig);
        // Use "create" context â€” Bug is only excluded from "search", not "create"
        var request = MeshQueryRequest.FromQuery("namespace:ACME/Project context:create");

        var results = CollectResults(query, request);

        results.Should().HaveCount(4);
    }

    [Fact]
    public void ContextFilter_ViaRequestContext()
    {
        SeedTestData();

        var typeNodes = new MeshNode[]
        {
            new("Person") { NodeType = "NodeType", Name = "Person", ExcludeFromContext = ["search"] },
            new("Story") { NodeType = "NodeType", Name = "Story" }
        };
        var meshConfig = new MeshConfiguration(typeNodes);

        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter, meshConfiguration: meshConfig);
        // Set context via MeshQueryRequest.Context (takes precedence over query string)
        var request = new MeshQueryRequest
        {
            Query = "path:ACME scope:descendants",
            Context = "search"
        };

        var results = CollectResults(query, request);

        // Person (Alice) should be excluded from "search" context
        results.Should().NotContain(n => n.NodeType == "Person");
        results.Should().HaveCountGreaterThan(0);
    }

    [Fact]
    public void ContextFilter_MultipleTypesExcluded()
    {
        SeedTestData();

        var typeNodes = new MeshNode[]
        {
            new("Bug") { NodeType = "NodeType", Name = "Bug", ExcludeFromContext = ["create"] },
            new("Person") { NodeType = "NodeType", Name = "Person", ExcludeFromContext = ["create"] },
            new("Story") { NodeType = "NodeType", Name = "Story" }
        };
        var meshConfig = new MeshConfiguration(typeNodes);

        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter, meshConfiguration: meshConfig);
        var request = MeshQueryRequest.FromQuery("path:ACME scope:descendants context:create");

        var results = CollectResults(query, request);

        // Only Story nodes should remain
        results.Should().HaveCount(3);
        results.Should().OnlyContain(n => n.NodeType == "Story");
    }

    #endregion

    #region Content Field Queries

    [Fact]
    public void ContentField_EqualityFilter()
    {
        SeedTestData();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("status:Open namespace:ACME/Project");

        var results = CollectResults(query, request);

        // Story1 (Open), Story3 (Open), Bug1 (Open)
        results.Should().HaveCount(3);
    }

    [Fact]
    public void ContentField_CombinedWithNodeTypeFilter()
    {
        SeedTestData();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("nodeType:Story status:Open path:ACME scope:descendants");

        var results = CollectResults(query, request);

        // Only Story1 and Story3 (Open Stories)
        results.Should().HaveCount(2);
    }

    #endregion

    #region Combined Features

    [Fact]
    public void CombinedQuery_FilterSortLimit()
    {
        SeedTestData();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = new MeshQueryRequest
        {
            Query = "nodeType:Story sort:name path:ACME scope:descendants",
            Limit = 2
        };

        var results = CollectResults(query, request);

        // 3 stories sorted: Claims Dashboard, Claims Processing, User Authentication
        // Limit 2 â†’ first two.
        results.Should().HaveCount(2);
        results[0].Name.Should().Be("Claims Dashboard");
        results[1].Name.Should().Be("Claims Processing");
    }

    [Fact]
    public void CombinedQuery_WildcardWithSort()
    {
        SeedTestData();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("name:*claims* sort:name-desc path:ACME scope:descendants");

        var results = CollectResults(query, request);

        results.Should().HaveCount(2);
        results[0].Name.Should().Be("Claims Processing");
        results[1].Name.Should().Be("Claims Dashboard");
    }

    #endregion

    #region Pipe Alternation (`field:A|B|C`) â€” pushed down as `IN (...)`

    [Fact]
    public void PipeAlternation_NodeType_ReturnsAllListedTypes()
    {
        SeedTestData();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        // Equivalent to nodeType:(Story OR Bug) â€” concise grep-style form.
        var request = MeshQueryRequest.FromQuery("nodeType:Story|Bug path:ACME scope:descendants");

        var results = CollectResults(query, request);

        results.Should().HaveCount(4);
        results.Select(n => n.NodeType).Distinct()
            .Should().BeEquivalentTo(new[] { "Story", "Bug" }, JsonSerializerOptions.Default);
    }

    [Fact]
    public void PipeAlternation_NegatedNodeType_ExcludesAllListedTypes()
    {
        SeedTestData();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        var request = MeshQueryRequest.FromQuery("-nodeType:Story|Bug path:ACME scope:descendants");

        var results = CollectResults(query, request);

        // Only Alice (Person) remains
        results.Should().HaveCount(1);
        results[0].NodeType.Should().Be("Person");
    }

    [Fact]
    public void PipeAlternation_OnPath_PushesDownIN()
    {
        SeedTestData();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        // Multi-path: matches the listed paths exactly. Single Postgres roundtrip
        // emits `WHERE path IN (...)` rather than N separate queries.
        var request = MeshQueryRequest.FromQuery(
            "path:ACME/Project/Story1|ACME/Project/Bug1|ACME/Project/Missing");

        var results = CollectResults(query, request);

        // Story1 and Bug1 exist; Missing doesn't â€” IN(...) returns the existing two.
        results.Should().HaveCount(2);
        results.Select(n => n.Path).Should().BeEquivalentTo(
            new[] { "ACME/Project/Story1", "ACME/Project/Bug1" }, JsonSerializerOptions.Default);
    }

    #endregion

    #region SQL-function sort (`sort:length(path)-desc`)

    [Fact]
    public void Sort_LengthOfPath_LongestFirst()
    {
        SeedTestData();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        // Routing-layer canonical form â€” "longest matching path wins" in one
        // round-trip. Pin this contract: backends MUST emit ORDER BY length(...)
        // and respect descending direction so MeshCatalog.FindBestPersistenceMatch
        // can rely on a single query for prefix resolution.
        var request = MeshQueryRequest.FromQuery(
            "path:ACME/Project|ACME|ACME/Project/Story1 sort:length(path)-desc");

        var results = CollectResults(query, request);

        // All three exact paths should hit (the 'ACME' top-level node won't exist
        // since SeedTestDataAsync seeds children only â€” verify ordering of the rest).
        results.Should().NotBeEmpty();
        // Among the existing results, longest path must come first.
        for (var i = 1; i < results.Count; i++)
            results[i - 1].Path.Length.Should().BeGreaterThanOrEqualTo(results[i].Path.Length,
                "sort:length(path)-desc must order results by descending path length");
    }

    [Fact]
    public void Sort_LengthOfPath_LimitOne_ReturnsLongestMatchingPrefix()
    {
        SeedTestData();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        // The single canonical form for routing-layer prefix lookup:
        // path:a|b|c sort:length(path)-desc limit:1 â†’ one row back, the deepest.
        var request = new MeshQueryRequest
        {
            Query = "path:ACME/Project/Story1|ACME/Project|ACME sort:length(path)-desc",
            Limit = 1
        };

        var results = CollectResults(query, request);

        results.Should().HaveCount(1);
        results[0].Path.Should().Be("ACME/Project/Story1");
    }

    [Fact]
    public void Sort_LowerOfName_CaseInsensitiveAscending()
    {
        SeedTestData();
        var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
        // Generic SQL-function selector â€” verifies the function-call syntax isn't
        // hard-coded to length() and works for the other allow-listed functions.
        var request = MeshQueryRequest.FromQuery(
            "nodeType:Story sort:lower(name) path:ACME scope:descendants");

        var results = CollectResults(query, request);

        results.Should().HaveCount(3);
        // Sort by lowered name ascending: "claims dashboard" < "claims processing" < "user authentication"
        results[0].Name.Should().Be("Claims Dashboard");
        results[1].Name.Should().Be("Claims Processing");
        results[2].Name.Should().Be("User Authentication");
    }

    #endregion

    private List<MeshNode> CollectResults(PostgreSqlMeshQuery query, MeshQueryRequest request)
        => query.QueryList(request, _options, TestContext.Current.CancellationToken)
            .Should().Within(30.Seconds()).Emit()
            .OfType<MeshNode>().ToList();
}
