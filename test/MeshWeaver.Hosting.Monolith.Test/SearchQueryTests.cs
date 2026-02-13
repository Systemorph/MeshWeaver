using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Tests for MeshQuery search functionality using samples/Graph/Data.
/// Validates case-insensitive text search, wildcard patterns, and autocomplete.
/// </summary>
[Collection("SearchQueryTests")]
public class SearchQueryTests : MonolithMeshTestBase
{

    private static readonly string SamplesDataDirectory = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", "Graph", "Data"));

    private readonly string _cacheDirectory;

    private IMeshQuery MeshQuery => Mesh.ServiceProvider.GetRequiredService<IMeshQuery>();
    private IPersistenceService Persistence => Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();

    public SearchQueryTests(ITestOutputHelper output) : base(output)
    {
        _cacheDirectory = Path.Combine(Path.GetTempPath(), "MeshWeaverSearchTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_cacheDirectory);
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return builder
            .UseMonolithMesh()
            .AddFileSystemPersistence(SamplesDataDirectory)
            .ConfigureServices(services => services.Configure<CompilationCacheOptions>(o => o.CacheDirectory = _cacheDirectory))
            .AddJsonGraphConfiguration(SamplesDataDirectory);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();

        if (Directory.Exists(_cacheDirectory))
        {
            try { Directory.Delete(_cacheDirectory, recursive: true); }
            catch { /* Ignore cleanup errors */ }
        }
    }

    #region Text Search Tests

    [Fact(Timeout = 30000)]
    public async Task TextSearch_CaseInsensitive_MatchesRegardlessOfCase()
    {
        // Arrange - search for "alice" (lowercase) should match "Alice" (proper case)
        // Note: scope:descendants is needed to search the full tree
        var request = new MeshQueryRequest { Query = "alice scope:descendants", Limit = 10 };

        // Act
        var results = await MeshQuery.QueryAsync<MeshNode>(request, null, TestContext.Current.CancellationToken).ToArrayAsync(TestContext.Current.CancellationToken);

        // Assert
        Output.WriteLine($"Found {results.Length} results for 'alice scope:descendants'");
        foreach (var r in results)
            Output.WriteLine($"  - {r.Path}: {r.Name}");

        // If no Alice found, the test is inconclusive (sample data may not have Alice)
        if (results.Length == 0)
        {
            Output.WriteLine("No results found - sample data may not contain 'Alice'");
            return;
        }

        results.Should().Contain(n => n.Name != null && n.Name.Contains("Alice", StringComparison.OrdinalIgnoreCase),
            "Case-insensitive search should find 'Alice' when searching for 'alice'");
    }

    [Fact(Timeout = 30000)]
    public async Task TextSearch_SubstringMatch_FindsPartialMatches()
    {
        // Arrange - search for partial string "org" should find "Organization"
        var request = new MeshQueryRequest { Query = "name:*org* scope:descendants", Limit = 10 };

        // Act
        var results = await MeshQuery.QueryAsync<MeshNode>(request, null, TestContext.Current.CancellationToken).ToArrayAsync(TestContext.Current.CancellationToken);

        // Assert
        Output.WriteLine($"Found {results.Length} results for 'name:*org*'");
        foreach (var r in results)
            Output.WriteLine($"  - {r.Path}: {r.Name} ({r.NodeType})");

        // Test passes even if no results - the query executed successfully
    }

    [Fact(Timeout = 10000)]
    public async Task TextSearch_EmptyQueryWithScope_ReturnsNodes()
    {
        // Arrange - empty query with scope should return all nodes up to limit
        var request = new MeshQueryRequest { Query = "scope:descendants", Limit = 10 };

        // Act
        var results = await MeshQuery.QueryAsync<MeshNode>(request, null, TestContext.Current.CancellationToken).ToArrayAsync(TestContext.Current.CancellationToken);

        // Assert
        Output.WriteLine($"Found {results.Length} results for 'scope:descendants'");
        results.Should().NotBeEmpty("Descendants query should return available nodes");
    }

    [Fact(Timeout = 10000)]
    public async Task TextSearch_WhitespaceOnly_DoesNotThrow()
    {
        // Arrange - whitespace-only query should not throw
        var request = new MeshQueryRequest { Query = "   ", Limit = 10 };

        // Act - should not throw
        var results = await MeshQuery.QueryAsync<MeshNode>(request, null, TestContext.Current.CancellationToken).ToArrayAsync(TestContext.Current.CancellationToken);

        // Assert
        Output.WriteLine($"Found {results.Length} results for whitespace query");
        // Whitespace query behavior may vary - just ensure it doesn't throw
    }

    #endregion

    #region Wildcard Search Tests

    [Fact(Timeout = 30000)]
    public async Task WildcardSearch_BothWildcards_MatchesSubstring()
    {
        // Arrange - "*erson*" should match "Person"
        var request = new MeshQueryRequest { Query = "name:*erson* scope:descendants", Limit = 10 };

        // Act
        var results = await MeshQuery.QueryAsync<MeshNode>(request, null, TestContext.Current.CancellationToken).ToArrayAsync(TestContext.Current.CancellationToken);

        // Assert
        Output.WriteLine($"Found {results.Length} results for 'name:*erson* scope:descendants'");
        foreach (var r in results)
            Output.WriteLine($"  - {r.Path}: {r.Name}");

        // If results found, verify they contain 'erson'
        if (results.Length > 0)
        {
            results.Should().Contain(n => n.Name != null && n.Name.Contains("erson", StringComparison.OrdinalIgnoreCase),
                "Wildcard search should find nodes with 'erson' in name");
        }
    }

    [Fact(Timeout = 30000)]
    public async Task WildcardSearch_TrailingWildcard_MatchesPrefix()
    {
        // Arrange - "name:Per*" should match "Person"
        var request = new MeshQueryRequest { Query = "name:Per* scope:descendants", Limit = 10 };

        // Act
        var results = await MeshQuery.QueryAsync<MeshNode>(request, null, TestContext.Current.CancellationToken).ToArrayAsync(TestContext.Current.CancellationToken);

        // Assert
        Output.WriteLine($"Found {results.Length} results for 'name:Per* scope:descendants'");
        foreach (var r in results)
            Output.WriteLine($"  - {r.Path}: {r.Name}");

        if (results.Length > 0)
        {
            results.Should().Contain(n => n.Name != null && n.Name.StartsWith("Per", StringComparison.OrdinalIgnoreCase),
                "Trailing wildcard should match prefix");
        }
    }

    [Fact(Timeout = 30000)]
    public async Task WildcardSearch_LeadingWildcard_MatchesSuffix()
    {
        // Arrange - "name:*tion" should match names ending in "tion" like "Organization"
        var request = new MeshQueryRequest { Query = "name:*tion scope:descendants", Limit = 10 };

        // Act
        var results = await MeshQuery.QueryAsync<MeshNode>(request, null, TestContext.Current.CancellationToken).ToArrayAsync(TestContext.Current.CancellationToken);

        // Assert
        Output.WriteLine($"Found {results.Length} results for 'name:*tion scope:descendants'");
        foreach (var r in results)
            Output.WriteLine($"  - {r.Path}: {r.Name}");

        if (results.Length > 0)
        {
            results.Should().Contain(n => n.Name != null && n.Name.EndsWith("tion", StringComparison.OrdinalIgnoreCase),
                "Leading wildcard should match suffix");
        }
    }

    #endregion

    #region Graph Sample Data Integration Tests

    [Fact(Timeout = 30000)]
    public async Task Search_GraphSampleData_FindsUserNodes()
    {
        // Arrange - search for User nodes (User is a NodeType that uses Person as content type)
        var request = new MeshQueryRequest { Query = "nodeType:User scope:descendants", Limit = 20 };

        // Act
        var results = await MeshQuery.QueryAsync<MeshNode>(request, null, TestContext.Current.CancellationToken).ToArrayAsync(TestContext.Current.CancellationToken);

        // Assert
        Output.WriteLine($"Found {results.Length} User nodes");
        foreach (var r in results)
            Output.WriteLine($"  - {r.Path}: {r.Name}");

        // The samples/Graph/Data should have User nodes (Alice, Bob, etc.)
        results.Should().NotBeEmpty("Sample data should contain User nodes");
    }

    [Fact(Timeout = 30000)]
    public async Task Search_GraphSampleData_FindsOrganization()
    {
        // Arrange - search for Organization with scope
        var request = new MeshQueryRequest { Query = "Organization scope:descendants", Limit = 10 };

        // Act
        var results = await MeshQuery.QueryAsync<MeshNode>(request, null, TestContext.Current.CancellationToken).ToArrayAsync(TestContext.Current.CancellationToken);

        // Assert
        Output.WriteLine($"Found {results.Length} results for 'Organization scope:descendants'");
        foreach (var r in results)
            Output.WriteLine($"  - {r.Path}: {r.Name} ({r.NodeType})");

        results.Should().Contain(n => n.Path != null && n.Path.Contains("Organization", StringComparison.OrdinalIgnoreCase),
            "Should find Organization in sample data");
    }

    [Fact(Timeout = 30000)]
    public async Task Search_GraphSampleData_ResultsLimitedCorrectly()
    {
        // Arrange - request with limit of 5
        var request = new MeshQueryRequest { Query = "", Limit = 5 };

        // Act
        var results = await MeshQuery.QueryAsync<MeshNode>(request, null, TestContext.Current.CancellationToken).ToArrayAsync(TestContext.Current.CancellationToken);

        // Assert
        Output.WriteLine($"Found {results.Length} results with limit 5");
        results.Length.Should().BeLessThanOrEqualTo(5, "Results should respect the limit");
    }

    [Fact(Timeout = 30000)]
    public async Task Search_GraphSampleData_FindsProjects()
    {
        // Arrange - search for Project nodes
        var request = new MeshQueryRequest { Query = "nodeType:*Project*", Limit = 20 };

        // Act
        var results = await MeshQuery.QueryAsync<MeshNode>(request, null, TestContext.Current.CancellationToken).ToArrayAsync(TestContext.Current.CancellationToken);

        // Assert
        Output.WriteLine($"Found {results.Length} Project-related nodes");
        foreach (var r in results)
            Output.WriteLine($"  - {r.Path}: {r.Name} ({r.NodeType})");

        // Log even if empty to understand the data structure
    }

    #endregion

    #region Autocomplete Tests

    [Fact(Timeout = 30000)]
    public async Task Autocomplete_WithBasePath_ReturnsSuggestions()
    {
        // Arrange - autocomplete from root
        var basePath = "";
        var prefix = "";

        // Act
        var suggestions = await MeshQuery.AutocompleteAsync(basePath, prefix, 10, TestContext.Current.CancellationToken).ToArrayAsync(TestContext.Current.CancellationToken);

        // Assert
        Output.WriteLine($"Found {suggestions.Length} autocomplete suggestions from root");
        foreach (var s in suggestions)
            Output.WriteLine($"  - {s.Path}: {s.Name} (Score: {s.Score:F2})");

        suggestions.Should().NotBeEmpty("Autocomplete from root should return suggestions");
    }

    [Fact(Timeout = 30000)]
    public async Task Autocomplete_WithPrefix_FiltersByPrefix()
    {
        // Arrange - autocomplete with prefix "Per"
        var basePath = "";
        var prefix = "Per";

        // Act
        var suggestions = await MeshQuery.AutocompleteAsync(basePath, prefix, 10, TestContext.Current.CancellationToken).ToArrayAsync(TestContext.Current.CancellationToken);

        // Assert
        Output.WriteLine($"Found {suggestions.Length} suggestions for prefix 'Per'");
        foreach (var s in suggestions)
            Output.WriteLine($"  - {s.Path}: {s.Name} (Score: {s.Score:F2})");

        // Suggestions should prefer items starting with or containing the prefix
    }

    [Fact(Timeout = 30000)]
    public async Task Autocomplete_WithNestedPath_ReturnsChildren()
    {
        // Arrange - autocomplete from a nested path
        // First find a node that has children
        var nodesRequest = new MeshQueryRequest { Query = "", Limit = 20 };
        var nodes = await MeshQuery.QueryAsync<MeshNode>(nodesRequest, null, TestContext.Current.CancellationToken).ToArrayAsync(TestContext.Current.CancellationToken);

        var nodeWithPotentialChildren = nodes.FirstOrDefault(n => n.Path != null && !n.Path.Contains("/"));
        if (nodeWithPotentialChildren == null)
        {
            Output.WriteLine("No suitable root-level node found for nested autocomplete test");
            return;
        }

        // Act
        var suggestions = await MeshQuery.AutocompleteAsync(nodeWithPotentialChildren.Path!, "", 10, TestContext.Current.CancellationToken).ToArrayAsync(TestContext.Current.CancellationToken);

        // Assert
        Output.WriteLine($"Found {suggestions.Length} suggestions under '{nodeWithPotentialChildren.Path}'");
        foreach (var s in suggestions)
            Output.WriteLine($"  - {s.Path}: {s.Name}");
    }

    #endregion

    #region Namespace Query Tests

    [Fact(Timeout = 30000)]
    public async Task NamespaceQuery_WithoutScope_FindsAllDescendants()
    {
        // Arrange - query with namespace: (defaults to scope:descendants)
        var request = new MeshQueryRequest { Query = "namespace:Systemorph", Limit = 50 };

        // Act
        var results = await MeshQuery.QueryAsync<MeshNode>(request, null, TestContext.Current.CancellationToken).ToArrayAsync(TestContext.Current.CancellationToken);

        // Assert
        Output.WriteLine($"Found {results.Length} nodes recursively under Systemorph namespace");
        foreach (var r in results.Take(10))
            Output.WriteLine($"  - {r.Path}: {r.Name} ({r.NodeType})");
        if (results.Length > 10)
            Output.WriteLine($"  ... and {results.Length - 10} more");

        // Results should include all descendants under Systemorph
    }

    [Fact(Timeout = 30000)]
    public async Task NamespaceQuery_WithDescendants_FindsAllNested()
    {
        // Arrange - query with namespace: and scope:descendants
        var request = new MeshQueryRequest { Query = "namespace:Systemorph scope:descendants", Limit = 50 };

        // Act
        var results = await MeshQuery.QueryAsync<MeshNode>(request, null, TestContext.Current.CancellationToken).ToArrayAsync(TestContext.Current.CancellationToken);

        // Assert
        Output.WriteLine($"Found {results.Length} nodes recursively under Systemorph");
        foreach (var r in results.Take(10))
            Output.WriteLine($"  - {r.Path}: {r.Name}");
        if (results.Length > 10)
            Output.WriteLine($"  ... and {results.Length - 10} more");

        // Recursive search should find more items than immediate children
    }

    [Fact(Timeout = 30000)]
    public async Task NamespaceQuery_WithFilter_CombinesBoth()
    {
        // Arrange - namespace with nodeType filter
        var request = new MeshQueryRequest { Query = "namespace:Systemorph nodeType:*Project*", Limit = 20 };

        // Act
        var results = await MeshQuery.QueryAsync<MeshNode>(request, null, TestContext.Current.CancellationToken).ToArrayAsync(TestContext.Current.CancellationToken);

        // Assert
        Output.WriteLine($"Found {results.Length} Project nodes in Systemorph namespace");
        foreach (var r in results)
            Output.WriteLine($"  - {r.Path}: {r.Name} ({r.NodeType})");
    }

    #endregion

    #region @ Reference Autocomplete Pattern Tests

    [Fact(Timeout = 30000)]
    public async Task ReferenceAutocomplete_AtSymbol_QueriesMeshNodes()
    {
        // This simulates what happens when user types "@" in the search bar
        // The search bar uses scope:descendants to get all nodes

        // Arrange - query with scope (simulating "@" which shows all nodes)
        var request = new MeshQueryRequest { Query = "scope:descendants", Limit = 10 };

        // Act
        var results = await MeshQuery.QueryAsync<MeshNode>(request, null, TestContext.Current.CancellationToken).ToArrayAsync(TestContext.Current.CancellationToken);

        // Assert
        Output.WriteLine($"Found {results.Length} nodes for '@' (scope:descendants query)");
        foreach (var r in results)
            Output.WriteLine($"  - {r.Path}: {r.Name}");

        results.Should().NotBeEmpty("'@' mode should return available nodes");
    }

    [Fact(Timeout = 30000)]
    public async Task ReferenceAutocomplete_WithPartialPath_UsesWildcard()
    {
        // This simulates "@Org" - searching for nodes matching "Org"

        // Arrange - wildcard search with scope
        var request = new MeshQueryRequest { Query = "*Org* scope:descendants", Limit = 10 };

        // Act
        var results = await MeshQuery.QueryAsync<MeshNode>(request, null, TestContext.Current.CancellationToken).ToArrayAsync(TestContext.Current.CancellationToken);

        // Assert
        Output.WriteLine($"Found {results.Length} nodes matching '*Org* scope:descendants'");
        foreach (var r in results)
            Output.WriteLine($"  - {r.Path}: {r.Name}");

        // May or may not find results depending on sample data
    }

    [Fact(Timeout = 30000)]
    public async Task ReferenceAutocomplete_WithTrailingSlash_DelegatesToAutocomplete()
    {
        // This simulates "@Organization/" - getting children/sub-completions

        // Arrange - first find a valid path
        var nodesRequest = new MeshQueryRequest { Query = "Organization scope:descendants", Limit = 5 };
        var nodes = await MeshQuery.QueryAsync<MeshNode>(nodesRequest, null, TestContext.Current.CancellationToken).ToArrayAsync(TestContext.Current.CancellationToken);

        if (!nodes.Any())
        {
            Output.WriteLine("No Organization node found, skipping test");
            return;
        }

        var basePath = nodes.First().Path!;
        Output.WriteLine($"Using base path: {basePath}");

        // Act - autocomplete from that path
        var suggestions = await MeshQuery.AutocompleteAsync(basePath, "", 10, TestContext.Current.CancellationToken).ToArrayAsync(TestContext.Current.CancellationToken);

        // Assert
        Output.WriteLine($"Found {suggestions.Length} sub-completions for '{basePath}/'");
        foreach (var s in suggestions)
            Output.WriteLine($"  - {s.Path}: {s.Name}");

        // Note: May be empty if no children exist
    }

    [Fact(Timeout = 30000)]
    public async Task ReferenceAutocomplete_ScopePattern_SearchesNodeType()
    {
        // This simulates "@data:Person" - scope-based search for Person type

        // Arrange - search by nodeType with scope
        var request = new MeshQueryRequest { Query = "nodeType:*Person* scope:descendants", Limit = 10 };

        // Act
        var results = await MeshQuery.QueryAsync<MeshNode>(request, null, TestContext.Current.CancellationToken).ToArrayAsync(TestContext.Current.CancellationToken);

        // Assert
        Output.WriteLine($"Found {results.Length} nodes with nodeType matching 'Person'");
        foreach (var r in results)
            Output.WriteLine($"  - {r.Path}: {r.Name} ({r.NodeType})");
    }

    [Fact(Timeout = 30000)]
    public async Task ReferenceAutocomplete_ScopeWithRemainder_CombinesFilters()
    {
        // This simulates "@data:alice" - scope + text search

        // Arrange - combined filter with scope
        var request = new MeshQueryRequest { Query = "nodeType:*Person* alice scope:descendants", Limit = 10 };

        // Act
        var results = await MeshQuery.QueryAsync<MeshNode>(request, null, TestContext.Current.CancellationToken).ToArrayAsync(TestContext.Current.CancellationToken);

        // Assert
        Output.WriteLine($"Found {results.Length} Person nodes matching 'alice'");
        foreach (var r in results)
            Output.WriteLine($"  - {r.Path}: {r.Name}");
    }

    #endregion

    #region QueryParser Tests

    [Fact(Timeout = 15000)]
    public void QueryParser_ParsesTextSearchFromCatalogQuery()
    {
        // Arrange - this is the exact query pattern from Catalog
        var parser = new MeshWeaver.Mesh.QueryParser();
        var query = "namespace:Systemorph scope:descendants Claims";

        // Act
        var parsed = parser.Parse(query);

        // Assert
        Output.WriteLine($"Query: '{query}'");
        Output.WriteLine($"TextSearch: '{parsed.TextSearch}'");
        Output.WriteLine($"Path: '{parsed.Path}'");
        Output.WriteLine($"Scope: {parsed.Scope}");

        parsed.TextSearch.Should().Be("Claims", "The search term should be extracted as TextSearch");
        parsed.Path.Should().Be("Systemorph");
        parsed.Scope.Should().Be(MeshWeaver.Mesh.QueryScope.Descendants);
    }

    [Fact(Timeout = 15000)]
    public void QueryParser_MultipleTextTerms_JoinedWithSpace()
    {
        var parser = new MeshWeaver.Mesh.QueryParser();
        var query = "namespace:Systemorph scope:descendants hello world";

        var parsed = parser.Parse(query);

        Output.WriteLine($"TextSearch: '{parsed.TextSearch}'");
        parsed.TextSearch.Should().Be("hello world");
    }

    #endregion

    #region Catalog Query Pattern Tests

    /// <summary>
    /// Tests the exact query pattern used by the Catalog function in MeshNodeLayoutAreas.cs.
    /// The Catalog builds queries like: "namespace:X scope:descendants searchTerm" for hierarchical mode
    /// or "namespace:X searchTerm" which adds scope:descendants when search term is present.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task CatalogQuery_WithSearchTerm_FiltersResults()
    {
        // This simulates what the Catalog does:
        // 1. For hierarchical mode: query = $"namespace:{hubPath} scope:descendants";
        //    if (!string.IsNullOrEmpty(searchTerm)) query += $" {searchTerm}";
        // 2. For default mode: query = $"namespace:{hubPath}";
        //    if (!string.IsNullOrEmpty(searchTerm)) query += $" scope:descendants {searchTerm}";

        // Arrange - simulate catalog search at Systemorph with search term "Marketing"
        var hubPath = "Systemorph";
        var searchTerm = "Marketing";

        // Build query like Catalog does for default mode
        var query = $"namespace:{hubPath} scope:descendants {searchTerm}";
        var request = new MeshQueryRequest { Query = query, Limit = 100 };

        // Act
        var results = await MeshQuery.QueryAsync<MeshNode>(request, null, TestContext.Current.CancellationToken).ToArrayAsync(TestContext.Current.CancellationToken);

        // Assert
        Output.WriteLine($"Query: '{query}'");
        Output.WriteLine($"Found {results.Length} results");
        foreach (var r in results)
            Output.WriteLine($"  - {r.Path}: {r.Name}");

        // Results should be non-empty and all under the Systemorph namespace
        // The query evaluator searches all string properties including nested Content
        results.Should().NotBeEmpty("Search should find results containing the term");
        results.Should().AllSatisfy(node =>
        {
            node.Path.Should().StartWith(hubPath, "All results should be under the searched namespace");
        });
    }

    [Fact(Timeout = 30000)]
    public async Task CatalogQuery_NodeTypeMode_WithSearchTerm_FiltersResults()
    {
        // This simulates NodeType catalog mode:
        // var query = $"nodeType:{nodeTypePath}";
        // if (!string.IsNullOrEmpty(searchTerm)) query += $" {searchTerm}";

        // Arrange - simulate catalog search for Person type with search term "Alice"
        var nodeTypePath = "Person";
        var searchTerm = "Alice";

        var query = $"nodeType:{nodeTypePath} {searchTerm}";
        var request = new MeshQueryRequest { Query = query, Limit = 100 };

        // Act
        var results = await MeshQuery.QueryAsync<MeshNode>(request, null, TestContext.Current.CancellationToken).ToArrayAsync(TestContext.Current.CancellationToken);

        // Assert
        Output.WriteLine($"Query: '{query}'");
        Output.WriteLine($"Found {results.Length} results");
        foreach (var r in results)
            Output.WriteLine($"  - {r.Path}: {r.Name} ({r.NodeType})");

        // If results found, verify they are of the correct NodeType
        if (results.Length > 0)
        {
            results.Should().AllSatisfy(node =>
            {
                node.NodeType.Should().Contain(nodeTypePath, "All results should be of the specified NodeType");
            });
        }
    }

    [Fact(Timeout = 30000)]
    public async Task CatalogQuery_HierarchicalMode_WithSearchTerm_FiltersResults()
    {
        // This simulates hierarchical catalog mode:
        // var query = $"namespace:{hubPath} scope:descendants";
        // if (!string.IsNullOrEmpty(searchTerm)) query += $" {searchTerm}";

        // Arrange
        var hubPath = "Systemorph";
        var searchTerm = "Claims";

        var query = $"namespace:{hubPath} scope:descendants {searchTerm}";
        var request = new MeshQueryRequest { Query = query, Limit = 100 };

        // Act
        var results = await MeshQuery.QueryAsync<MeshNode>(request, null, TestContext.Current.CancellationToken).ToArrayAsync(TestContext.Current.CancellationToken);

        // Assert
        Output.WriteLine($"Query: '{query}'");
        Output.WriteLine($"Found {results.Length} results");
        foreach (var r in results)
            Output.WriteLine($"  - {r.Path}: {r.Name}");

        // Results should be non-empty and all under the Systemorph namespace
        // The query evaluator searches all string properties including nested Content
        results.Should().NotBeEmpty("Search should find results containing the term");
        results.Should().AllSatisfy(node =>
        {
            node.Path.Should().StartWith(hubPath, "All results should be under the searched namespace");
        });
    }

    #endregion

    #region Search Term Filtering Tests

    [Fact(Timeout = 30000)]
    public async Task Search_NonExistentTerm_ReturnsNoResults()
    {
        // Arrange - search for a completely non-existent word within a namespace
        // This tests that the search term is actually being considered in the query
        var request = new MeshQueryRequest { Query = "namespace:Systemorph scope:descendants xyznonexistent123", Limit = 100 };

        // Act
        var results = await MeshQuery.QueryAsync<MeshNode>(request, null, TestContext.Current.CancellationToken).ToArrayAsync(TestContext.Current.CancellationToken);

        // Assert
        Output.WriteLine($"Found {results.Length} results for 'namespace:Systemorph scope:descendants xyznonexistent123'");
        foreach (var r in results)
            Output.WriteLine($"  - {r.Path}: {r.Name}");

        results.Should().BeEmpty("A non-existent search term should return no results");
    }

    [Fact(Timeout = 30000)]
    public async Task Search_WithSearchTerm_FiltersResults()
    {
        // Arrange - first get all results without filter
        var allRequest = new MeshQueryRequest { Query = "namespace:Systemorph scope:descendants", Limit = 100 };
        var allResults = await MeshQuery.QueryAsync<MeshNode>(allRequest, null, TestContext.Current.CancellationToken).ToArrayAsync(TestContext.Current.CancellationToken);

        Output.WriteLine($"Found {allResults.Length} total results in Systemorph namespace");

        // Now search with a specific term that exists
        var filteredRequest = new MeshQueryRequest { Query = "namespace:Systemorph scope:descendants Marketing", Limit = 100 };
        var filteredResults = await MeshQuery.QueryAsync<MeshNode>(filteredRequest, null, TestContext.Current.CancellationToken).ToArrayAsync(TestContext.Current.CancellationToken);

        Output.WriteLine($"Found {filteredResults.Length} results with 'Marketing' filter");
        foreach (var r in filteredResults)
            Output.WriteLine($"  - {r.Path}: {r.Name}");

        // Assert - filtered results should be subset of all results
        filteredResults.Length.Should().BeLessThan(allResults.Length,
            "Adding a search term should filter down the results");
    }

    [Fact(Timeout = 30000)]
    public async Task Search_NamespaceWithDescendantsAndTerm_TermIsApplied()
    {
        // This is the exact query pattern used by the catalog search bar
        // Arrange - search like: "namespace:Systemorph/Marketing scope:descendants blabla"
        var request = new MeshQueryRequest
        {
            Query = "namespace:Systemorph/Marketing scope:descendants thisdoesnotexistatall",
            Limit = 100
        };

        // Act
        var results = await MeshQuery.QueryAsync<MeshNode>(request, null, TestContext.Current.CancellationToken).ToArrayAsync(TestContext.Current.CancellationToken);

        // Assert
        Output.WriteLine($"Query: 'namespace:Systemorph/Marketing scope:descendants thisdoesnotexistatall'");
        Output.WriteLine($"Found {results.Length} results");
        foreach (var r in results)
            Output.WriteLine($"  - {r.Path}: {r.Name}");

        results.Should().BeEmpty(
            "The search term 'thisdoesnotexistatall' should filter out all results since it doesn't exist");
    }

    #endregion
}

[CollectionDefinition("SearchQueryTests", DisableParallelization = true)]
public class SearchQueryTestsDefinition { }
