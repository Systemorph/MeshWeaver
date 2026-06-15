using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Domain;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.PathResolution.Test;

/// <summary>
/// Tests for hierarchical browsing of Marketing stories.
/// Validates parent/child relationships, sub-stories, and namespace queries.
/// </summary>
public class HierarchicalBrowsingTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder);

    /// <summary>Reactive query snapshot: the Initial emission carries the full result set.</summary>
    private async Task<IReadOnlyList<MeshNode>> QueryNodes(MeshQueryRequest request)
        => (await MeshQuery.Query<MeshNode>(request)
            .Should().Match(c => c.ChangeType == QueryChangeType.Initial)).Items;

    private async Task<IReadOnlyList<MeshNode>> QueryNodes(string query) => await QueryNodes(MeshQueryRequest.FromQuery(query));

    /// <summary>
    /// Reactive autocomplete is a one-shot snapshot: each provider emits its matches then completes.
    /// Immediately after <c>CreateNode</c> the provider's index can still be catching up, so a single
    /// snapshot may complete empty. Re-issue the snapshot on an interval until it surfaces a match — the
    /// sanctioned re-query pattern for snapshot sources that race eventual-consistency lag.
    /// </summary>
    private async Task<IReadOnlyCollection<QueryResult>> AutocompleteUntil(
        string basePath, string prefix, Func<IReadOnlyCollection<QueryResult>, bool> predicate)
        => await Observable.Interval(TimeSpan.FromMilliseconds(100)).StartWith(0L)
            .SelectMany(_ => MeshQuery.Autocomplete(basePath, prefix, limit: 10))
            .Should().Match(predicate);

    private async Task SetupMarketingHierarchy()
    {
        // Create the Marketing story hierarchy similar to sample data
        await NodeFactory.CreateNode(MeshNode.FromPath("Systemorph/Marketing") with
        {
            Name = "Marketing",
            NodeType = "Group"
        }).Should().Emit();

        await NodeFactory.CreateNode(MeshNode.FromPath("Systemorph/Marketing/ClaimsProcessing") with
        {
            Name = "Claims Processing",
            NodeType = "Markdown"
        }).Should().Emit();

        await NodeFactory.CreateNode(MeshNode.FromPath("Systemorph/Marketing/DataIngestionStrategy") with
        {
            Name = "Data Ingestion Strategy",
            NodeType = "Markdown"
        }).Should().Emit();

        // Sub-stories of ClaimsProcessing
        await NodeFactory.CreateNode(MeshNode.FromPath("Systemorph/Marketing/ClaimsProcessing/EmailTriage") with
        {
            Name = "Email Triage",
            NodeType = "Markdown"
        }).Should().Emit();

        await NodeFactory.CreateNode(MeshNode.FromPath("Systemorph/Marketing/ClaimsProcessing/DocumentExtraction") with
        {
            Name = "Document Extraction",
            NodeType = "Markdown"
        }).Should().Emit();

        await NodeFactory.CreateNode(MeshNode.FromPath("Systemorph/Marketing/ClaimsProcessing/ClientCorrespondence") with
        {
            Name = "Client Correspondence",
            NodeType = "Markdown"
        }).Should().Emit();

        // Sub-stories of DataIngestionStrategy
        await NodeFactory.CreateNode(MeshNode.FromPath("Systemorph/Marketing/DataIngestionStrategy/AnnotatedDataModel") with
        {
            Name = "Annotated Data Model",
            NodeType = "Markdown"
        }).Should().Emit();

        await NodeFactory.CreateNode(MeshNode.FromPath("Systemorph/Marketing/DataIngestionStrategy/HistoricIngestion") with
        {
            Name = "Historic Ingestion",
            NodeType = "Markdown"
        }).Should().Emit();
    }

    [Fact]
    public async Task Query_TopLevel_ShowsAllStories()
    {
        await SetupMarketingHierarchy();

        var results = await QueryNodes("path:Systemorph/Marketing nodeType:Markdown scope:descendants");

        // Should find all 7 stories (2 parent + 5 sub-stories)
        results.Should().HaveCount(7);
        results.Select(n => n.Name).Should().Contain([
            "Claims Processing",
            "Data Ingestion Strategy",
            "Email Triage",
            "Document Extraction",
            "Client Correspondence",
            "Annotated Data Model",
            "Historic Ingestion"
        ]);
    }

    [Fact]
    public async Task Query_SubStories_OnlyReturnsChildrenOfParent()
    {
        await SetupMarketingHierarchy();

        // scope:subtree includes the base path itself plus all descendants
        var results = await QueryNodes("path:Systemorph/Marketing/ClaimsProcessing nodeType:Markdown scope:subtree");

        // Should find ClaimsProcessing + 3 sub-stories (4 total)
        results.Should().HaveCount(4);
        results.Select(n => n.Name).Should().Contain([
            "Claims Processing",
            "Email Triage",
            "Document Extraction",
            "Client Correspondence"
        ]);
        // Should NOT contain stories from other parent
        results.Select(n => n.Name).Should().NotContain("Annotated Data Model");
        results.Select(n => n.Name).Should().NotContain("Historic Ingestion");
    }

    [Fact]
    public async Task Query_ByPath_RestrictsResults()
    {
        await SetupMarketingHierarchy();

        var results = await QueryNodes("path:Systemorph/Marketing/ClaimsProcessing nodeType:Markdown scope:subtree");

        // Should only return nodes under ClaimsProcessing path (ClaimsProcessing + 3 sub-stories = 4)
        results.Should().HaveCount(4);
        var paths = results.Select(n => n.Path);
        paths.Should().AllSatisfy(p => p.Should().StartWith("Systemorph/Marketing/ClaimsProcessing"));
    }

    [Fact]
    public async Task Query_ParentStory_HasCorrectChildren()
    {
        await SetupMarketingHierarchy();

        // Get ClaimsProcessing node
        var claimsNode = await ReadNode("Systemorph/Marketing/ClaimsProcessing").Should().Emit();
        claimsNode.Should().NotBeNull();

        // Get direct children
        var children = await QueryNodes($"namespace:{claimsNode!.Path}");

        children.Should().HaveCount(3);
        children.Select(n => n.Name).Should().Contain([
            "Email Triage",
            "Document Extraction",
            "Client Correspondence"
        ]);
    }

    [Fact]
    public async Task Query_SubStory_HasCorrectParent()
    {
        await SetupMarketingHierarchy();

        // Get a sub-story
        var emailTriageNode = await ReadNode("Systemorph/Marketing/ClaimsProcessing/EmailTriage").Should().Emit();
        emailTriageNode.Should().NotBeNull();

        // Verify parent path
        emailTriageNode!.GetParentPath().Should().Be("Systemorph/Marketing/ClaimsProcessing");

        // Get parent
        var parentPath = emailTriageNode!.GetParentPath();
        parentPath.Should().NotBeNull();
        var parentNode = await ReadNode(parentPath!).Should().Emit();
        parentNode.Should().NotBeNull();
        parentNode!.Name.Should().Be("Claims Processing");
    }

    [Fact]
    public async Task Autocomplete_ReturnsMatchingNodes()
    {
        await SetupMarketingHierarchy();

        var suggestions = await AutocompleteUntil("Systemorph/Marketing", "Email", r => r.Count >= 1);

        suggestions.Should().HaveCount(1);
        suggestions.First().Name.Should().Be("Email Triage");
        suggestions.First().Path.Should().Be("Systemorph/Marketing/ClaimsProcessing/EmailTriage");
    }

    [Fact]
    public async Task Autocomplete_FuzzyMatching_FindsPartialMatches()
    {
        await SetupMarketingHierarchy();

        var suggestions = await AutocompleteUntil("Systemorph/Marketing", "claim", r => r.Any(s => s.Name == "Claims Processing"));

        // Should find Claims Processing and its sub-stories
        suggestions.Should().Contain(s => s.Name == "Claims Processing");
    }

    [Fact]
    public async Task Query_TextSearch_FindsMatchingDescriptions()
    {
        await SetupMarketingHierarchy();

        // Search for "AI" in descriptions
        var results = await QueryNodes("path:Systemorph/Marketing AI scope:descendants");

        // Should find Email Triage and Client Correspondence which mention AI
        results.Should().HaveCountGreaterThanOrEqualTo(2);
        var names = results.Select(n => n.Name).ToList();
        names.Should().Contain("Email Triage");
        names.Should().Contain("Client Correspondence");
    }

    [Fact]
    public async Task Query_WithLimit_RespectsLimit()
    {
        await SetupMarketingHierarchy();

        var results = await QueryNodes("path:Systemorph/Marketing nodeType:Markdown scope:descendants limit:3");

        results.Should().HaveCount(3);
    }

    [Fact]
    public async Task Query_WithLimitProperty_OverridesQueryLimit()
    {
        await SetupMarketingHierarchy();

        // Limit property takes precedence over limit in query string
        var results = await QueryNodes(new MeshQueryRequest
        {
            Query = "path:Systemorph/Marketing nodeType:Markdown scope:descendants limit:10",
            Limit = 2
        });

        // Limit property (2) overrides query string limit (10)
        results.Should().HaveCount(2);
    }

    [Fact]
    public async Task Query_WithSkipAndLimit_ReturnsPaginatedResults()
    {
        await SetupMarketingHierarchy();

        // Query all 7 stories to get consistent ordering
        var allQuery = "path:Systemorph/Marketing nodeType:Markdown scope:descendants";
        var allResults = await QueryNodes(allQuery);
        allResults.Should().HaveCount(7);

        // Get first page (skip 0, limit 3)
        var page1 = await QueryNodes(new MeshQueryRequest { Query = allQuery, Skip = 0, Limit = 3 });
        page1.Should().HaveCount(3);

        // Get second page (skip 3, limit 3)
        var page2 = await QueryNodes(new MeshQueryRequest { Query = allQuery, Skip = 3, Limit = 3 });
        page2.Should().HaveCount(3);

        // Get third page (skip 6, limit 3) - only 1 item left
        var page3 = await QueryNodes(new MeshQueryRequest { Query = allQuery, Skip = 6, Limit = 3 });
        page3.Should().HaveCount(1);

        // All pages should contain different items
        var allPaths = page1.Concat(page2).Concat(page3).Select(n => n.Path).ToList();
        allPaths.Should().HaveCount(7);
        allPaths.Distinct().Should().HaveCount(7);
    }

    [Fact]
    public async Task Query_Hierarchy_IncludesAncestorsAndDescendants()
    {
        await SetupMarketingHierarchy();

        // Query with hierarchy scope from a sub-story
        var results = await QueryNodes("path:Systemorph/Marketing/ClaimsProcessing/EmailTriage scope:hierarchy");

        var paths = results.Select(n => n.Path).ToList();

        // Should include ancestors
        paths.Should().Contain("Systemorph/Marketing/ClaimsProcessing");
        paths.Should().Contain("Systemorph/Marketing");
    }
}

/// <summary>
/// Tests for generic typed query extensions.
/// </summary>
public class TypedQueryTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder);

    private async Task<IReadOnlyList<MeshNode>> QueryNodes(MeshQueryRequest request)
        => (await MeshQuery.Query<MeshNode>(request)
            .Should().Match(c => c.ChangeType == QueryChangeType.Initial)).Items;

    [Fact]
    public async Task QueryAsync_Generic_ReturnsTypedResults()
    {
        // Arrange - save MeshNodes with nodeType
        await NodeFactory.CreateNode(MeshNode.FromPath("shop/inventory/1") with { Name = "Laptop", NodeType = "Markdown" }).Should().Emit();
        await NodeFactory.CreateNode(MeshNode.FromPath("shop/inventory/2") with { Name = "Phone", NodeType = "Markdown" }).Should().Emit();
        await NodeFactory.CreateNode(MeshNode.FromPath("shop/inventory/order-1") with { Name = "Order 1", NodeType = "Code" }).Should().Emit();

        // Act - query for Product nodes only
        var results = await QueryNodes(MeshQueryRequest.FromQuery("path:shop/inventory nodeType:Markdown scope:descendants"));

        // Assert - should only return Product nodes
        results.Should().HaveCount(2);
        results.Select(n => n.Name).Should().Contain(["Laptop", "Phone"]);
    }

    [Fact]
    public async Task QueryAsync_Generic_WithNodeType_FiltersCorrectly()
    {
        // Arrange - save nodes with different types
        await NodeFactory.CreateNode(MeshNode.FromPath("shop/data/1") with { Name = "Laptop", NodeType = "Markdown" }).Should().Emit();
        await NodeFactory.CreateNode(MeshNode.FromPath("shop/data/order-1") with { Name = "Order 1", NodeType = "Code" }).Should().Emit();

        // Act - query for Product nodeType
        var results = await QueryNodes(MeshQueryRequest.FromQuery("path:shop/data nodeType:Markdown scope:descendants"));

        // Assert - should find only Product nodes
        results.Should().HaveCount(1);
        results.First().Name.Should().Be("Laptop");
    }

    [Fact]
    public async Task QueryAsync_Generic_WithPaging_ReturnsPagedResults()
    {
        // Arrange - save 10 product nodes
        for (int i = 1; i <= 10; i++)
        {
            await NodeFactory.CreateNode(MeshNode.FromPath($"catalog/products/{i}") with
            {
                Name = $"Product {i}",
                NodeType = "Markdown"
            }).Should().Emit();
        }

        // Act - get page 2 (skip 3, take 3)
        var results = await QueryNodes(MeshQueryRequest.FromQuery("path:catalog/products scope:descendants") with { Skip = 3, Limit = 3 });

        // Assert
        results.Should().HaveCount(3);
    }

    [Fact]
    public async Task QueryAsync_Generic_WithAdditionalFilters_CombinesFilters()
    {
        // Arrange - save nodes with different names and types
        await NodeFactory.CreateNode(MeshNode.FromPath("shop/all/1") with { Name = "Gaming Laptop", NodeType = "Markdown" }).Should().Emit();
        await NodeFactory.CreateNode(MeshNode.FromPath("shop/all/2") with { Name = "Business Laptop", NodeType = "Markdown" }).Should().Emit();
        await NodeFactory.CreateNode(MeshNode.FromPath("shop/all/3") with { Name = "Phone", NodeType = "Markdown" }).Should().Emit();
        await NodeFactory.CreateNode(MeshNode.FromPath("shop/all/order-1") with { Name = "Order 1", NodeType = "Code" }).Should().Emit();

        // Act - query for Product nodes with name filter
        var results = await QueryNodes(MeshQueryRequest.FromQuery("path:shop/all name:*Laptop* nodeType:Markdown scope:descendants"));

        // Assert - should only return laptops
        results.Should().HaveCount(2);
        results.Should().OnlyContain(n => n.Name!.Contains("Laptop"));
    }

    [Fact]
    public async Task QueryAsync_Generic_NoMatchingNodeType_ReturnsEmpty()
    {
        // Arrange - save only Order nodes
        await NodeFactory.CreateNode(MeshNode.FromPath("shop/orders/order-1") with { Name = "Order 1", NodeType = "Code" }).Should().Emit();
        await NodeFactory.CreateNode(MeshNode.FromPath("shop/orders/order-2") with { Name = "Order 2", NodeType = "Code" }).Should().Emit();

        // Act - query for Product nodeType (none exist)
        var results = await QueryNodes(MeshQueryRequest.FromQuery("path:shop/orders nodeType:Markdown scope:descendants"));

        // Assert - no Product nodes exist, only Order
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task QueryAsync_Generic_MeshNode_WorksWithNodes()
    {
        // Arrange
        await NodeFactory.CreateNode(MeshNode.FromPath("org/acme") with { Name = "Acme Corp", NodeType = "Markdown" }).Should().Emit();
        await NodeFactory.CreateNode(MeshNode.FromPath("org/contoso") with { Name = "Contoso Ltd", NodeType = "Markdown" }).Should().Emit();

        // Act - query for MeshNode type
        var results = await QueryNodes(MeshQueryRequest.FromQuery("path:org scope:descendants"));

        // Assert
        results.Should().HaveCount(2);
        results.Select(n => n.Name).Should().Contain(["Acme Corp", "Contoso Ltd"]);
    }
}

/// <summary>
/// Test product class for typed query tests.
/// </summary>
public record TestProduct
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public decimal Price { get; init; }
}

/// <summary>
/// Test order class for typed query tests.
/// </summary>
public record TestOrder
{
    public string Id { get; init; } = "";
    public string CustomerId { get; init; } = "";
    public decimal Total { get; init; }
}

/// <summary>
/// Simple type registry for testing purposes.
/// </summary>
public class TestTypeRegistry : ITypeRegistry
{
    private readonly Dictionary<Type, string> _typeToName = new();
    private readonly Dictionary<string, Type> _nameToType = new(StringComparer.OrdinalIgnoreCase);

    public TestTypeRegistry Register<T>(string name)
    {
        _typeToName[typeof(T)] = name;
        _nameToType[name] = typeof(T);
        return this;
    }

    public bool TryGetCollectionName(Type type, out string? typeName)
    {
        return _typeToName.TryGetValue(type, out typeName);
    }

    public Type? GetType(string name)
    {
        return _nameToType.TryGetValue(name, out var type) ? type : null;
    }

    public bool TryGetType(string name, out ITypeDefinition? type)
    {
        type = null;
        return false;
    }

    // Not implemented - not needed for tests
    public ITypeRegistry WithType<TEvent>() => this;
    public ITypeRegistry WithType<TEvent>(string name) => this;
    public ITypeRegistry WithType(Type type) => this;
    public ITypeRegistry WithType(Type type, string typeName) => this;
    public KeyFunction? GetKeyFunction(string collection) => null;
    public KeyFunction? GetKeyFunction(Type type) => null;
    public ITypeDefinition WithKeyFunction(string collection, KeyFunction keyFunction) => null!;
    public ITypeRegistry WithTypesFromAssembly(Type type, Func<Type, bool> filter) => this;
    public ITypeRegistry WithTypes(params IEnumerable<Type> types) => this;
    public ITypeRegistry WithTypes(params IEnumerable<KeyValuePair<string, Type>> types) => this;
    public string GetOrAddType(Type valueType, string? defaultName = null) => defaultName ?? valueType.Name;
    public ITypeRegistry WithKeyFunctionProvider(Func<Type, KeyFunction?> key) => this;
    public ITypeDefinition? GetTypeDefinition(Type type, bool create = true, string? typeName = null) => null;
    public ITypeDefinition? GetTypeDefinition(string collection) => null;
    public IEnumerable<KeyValuePair<string, ITypeDefinition>> Types => [];
}
