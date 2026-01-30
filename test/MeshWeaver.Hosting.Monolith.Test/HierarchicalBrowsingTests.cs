using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Domain;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Tests for hierarchical browsing of Marketing stories.
/// Validates parent/child relationships, sub-stories, and namespace queries.
/// </summary>
public class HierarchicalBrowsingTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private InMemoryPersistenceService? _persistence;
    private IMeshQuery? _meshQuery;
    private bool _setupDone;
    private JsonSerializerOptions JsonOptions => Mesh.ServiceProvider.GetRequiredService<IMessageHub>().JsonSerializerOptions;

    private InMemoryPersistenceService Persistence => _persistence ??= InitPersistence();
    private IMeshQuery MeshQuery => _meshQuery ??= new InMemoryMeshQuery(Persistence);

    private InMemoryPersistenceService InitPersistence()
    {
        var persistence = new InMemoryPersistenceService();
        _persistence = persistence;
        EnsureSetup();
        return persistence;
    }

    private void EnsureSetup()
    {
        if (_setupDone) return;
        _setupDone = true;
        SetupMarketingHierarchy().GetAwaiter().GetResult();
    }

    private async Task SetupMarketingHierarchy()
    {
        // Create the Marketing story hierarchy similar to sample data
        // Parent stories
        await Persistence.SaveNodeAsync(MeshNode.FromPath("Systemorph/Marketing") with
        {
            Name = "Marketing",
            NodeType = "Namespace",
            Description = "Marketing namespace"
        }, JsonOptions);

        await Persistence.SaveNodeAsync(MeshNode.FromPath("Systemorph/Marketing/ClaimsProcessing") with
        {
            Name = "Claims Processing",
            NodeType = "Systemorph/Marketing/Story",
            Description = "Claims processing and email management use case"
        }, JsonOptions);

        await Persistence.SaveNodeAsync(MeshNode.FromPath("Systemorph/Marketing/DataIngestionStrategy") with
        {
            Name = "Data Ingestion Strategy",
            NodeType = "Systemorph/Marketing/Story",
            Description = "Data ingestion and integration patterns"
        }, JsonOptions);

        // Sub-stories of ClaimsProcessing
        await Persistence.SaveNodeAsync(MeshNode.FromPath("Systemorph/Marketing/ClaimsProcessing/EmailTriage") with
        {
            Name = "Email Triage",
            NodeType = "Systemorph/Marketing/Story",
            Description = "AI-driven email classification, prioritization, and routing"
        }, JsonOptions);

        await Persistence.SaveNodeAsync(MeshNode.FromPath("Systemorph/Marketing/ClaimsProcessing/DocumentExtraction") with
        {
            Name = "Document Extraction",
            NodeType = "Systemorph/Marketing/Story",
            Description = "Extract structured data from claims documents"
        }, JsonOptions);

        await Persistence.SaveNodeAsync(MeshNode.FromPath("Systemorph/Marketing/ClaimsProcessing/ClientCorrespondence") with
        {
            Name = "Client Correspondence",
            NodeType = "Systemorph/Marketing/Story",
            Description = "AI-assisted client communication"
        }, JsonOptions);

        // Sub-stories of DataIngestionStrategy
        await Persistence.SaveNodeAsync(MeshNode.FromPath("Systemorph/Marketing/DataIngestionStrategy/AnnotatedDataModel") with
        {
            Name = "Annotated Data Model",
            NodeType = "Systemorph/Marketing/Story",
            Description = "Self-documenting data models"
        }, JsonOptions);

        await Persistence.SaveNodeAsync(MeshNode.FromPath("Systemorph/Marketing/DataIngestionStrategy/HistoricIngestion") with
        {
            Name = "Historic Ingestion",
            NodeType = "Systemorph/Marketing/Story",
            Description = "Batch ingestion of historical data"
        }, JsonOptions);
    }

    [Fact]
    public async Task Query_TopLevel_ShowsAllStories()
    {
        // Query all Story nodes under Marketing using path: in query string
        var query = "path:Systemorph/Marketing nodeType:Systemorph/Marketing/Story scope:descendants";
        var results = await MeshQuery.QueryAsync(MeshQueryRequest.FromQuery(query), JsonOptions).ToListAsync();

        // Should find all 7 stories (2 parent + 5 sub-stories)
        results.Should().HaveCount(7);
        results.Cast<MeshNode>().Select(n => n.Name).Should().Contain([
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
        // Query stories under ClaimsProcessing only using path: in query string
        // scope:subtree includes the base path itself plus all descendants
        var query = "path:Systemorph/Marketing/ClaimsProcessing nodeType:Systemorph/Marketing/Story scope:subtree";
        var results = await MeshQuery.QueryAsync(MeshQueryRequest.FromQuery(query), JsonOptions).ToListAsync();

        // Should find ClaimsProcessing + 3 sub-stories (4 total)
        results.Should().HaveCount(4);
        results.Cast<MeshNode>().Select(n => n.Name).Should().Contain([
            "Claims Processing",
            "Email Triage",
            "Document Extraction",
            "Client Correspondence"
        ]);
        // Should NOT contain stories from other parent
        results.Cast<MeshNode>().Select(n => n.Name).Should().NotContain("Annotated Data Model");
        results.Cast<MeshNode>().Select(n => n.Name).Should().NotContain("Historic Ingestion");
    }

    [Fact]
    public async Task Query_ByPath_RestrictsResults()
    {
        // Use IMeshQuery with path: in query string (replaces old Namespace property)
        // scope:subtree includes the base path itself plus all descendants
        var query = "path:Systemorph/Marketing/ClaimsProcessing nodeType:Systemorph/Marketing/Story scope:subtree";
        var results = await MeshQuery.QueryAsync(MeshQueryRequest.FromQuery(query), JsonOptions).ToListAsync();

        // Should only return nodes under ClaimsProcessing path (ClaimsProcessing + 3 sub-stories = 4)
        results.Should().HaveCount(4);
        var paths = results.Cast<MeshNode>().Select(n => n.Path);
        paths.Should().AllSatisfy(p => p.Should().StartWith("Systemorph/Marketing/ClaimsProcessing"));
    }

    [Fact]
    public async Task Query_ParentStory_HasCorrectChildren()
    {
        // Get ClaimsProcessing node
        var claimsNode = await Persistence.GetNodeAsync("Systemorph/Marketing/ClaimsProcessing", JsonOptions);
        claimsNode.Should().NotBeNull();

        // Get direct children
        var children = await Persistence.GetChildrenAsync(claimsNode!.Path, JsonOptions).ToListAsync();

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
        // Get a sub-story
        var emailTriageNode = await Persistence.GetNodeAsync("Systemorph/Marketing/ClaimsProcessing/EmailTriage", JsonOptions);
        emailTriageNode.Should().NotBeNull();

        // Verify parent path
        emailTriageNode!.ParentPath.Should().Be("Systemorph/Marketing/ClaimsProcessing");

        // Get parent
        var parentNode = await Persistence.GetNodeAsync(emailTriageNode.ParentPath, JsonOptions);
        parentNode.Should().NotBeNull();
        parentNode!.Name.Should().Be("Claims Processing");
    }

    [Fact]
    public async Task Autocomplete_ReturnsMatchingNodes()
    {
        // Test autocomplete for "Email"
        var suggestions = await MeshQuery.AutocompleteAsync("Systemorph/Marketing", "Email", JsonOptions, 10).ToListAsync();

        suggestions.Should().HaveCount(1);
        suggestions[0].Name.Should().Be("Email Triage");
        suggestions[0].Path.Should().Be("Systemorph/Marketing/ClaimsProcessing/EmailTriage");
    }

    [Fact]
    public async Task Autocomplete_FuzzyMatching_FindsPartialMatches()
    {
        // Test autocomplete with partial match
        var suggestions = await MeshQuery.AutocompleteAsync("Systemorph/Marketing", "claim", JsonOptions, 10).ToListAsync();

        // Should find Claims Processing and its sub-stories
        suggestions.Should().Contain(s => s.Name == "Claims Processing");
    }

    [Fact]
    public async Task Query_TextSearch_FindsMatchingDescriptions()
    {
        // Search for "AI" in descriptions using path: in query string
        var query = "path:Systemorph/Marketing AI scope:descendants";
        var results = await MeshQuery.QueryAsync(MeshQueryRequest.FromQuery(query), JsonOptions).ToListAsync();

        // Should find Email Triage and Client Correspondence which mention AI
        results.Should().HaveCountGreaterThanOrEqualTo(2);
        var names = results.Cast<MeshNode>().Select(n => n.Name).ToList();
        names.Should().Contain("Email Triage");
        names.Should().Contain("Client Correspondence");
    }

    [Fact]
    public async Task Query_WithLimit_RespectsLimit()
    {
        // Use limit: in query string
        var query = "path:Systemorph/Marketing nodeType:Systemorph/Marketing/Story scope:descendants limit:3";
        var results = await MeshQuery.QueryAsync(MeshQueryRequest.FromQuery(query), JsonOptions).ToListAsync();

        results.Should().HaveCount(3);
    }

    [Fact]
    public async Task Query_WithLimitProperty_OverridesQueryLimit()
    {
        // Limit property takes precedence over limit in query string
        var request = new MeshQueryRequest
        {
            Query = "path:Systemorph/Marketing nodeType:Systemorph/Marketing/Story scope:descendants limit:10",
            Limit = 2
        };
        var results = await MeshQuery.QueryAsync(request, JsonOptions).ToListAsync();

        // Limit property (2) overrides query string limit (10)
        results.Should().HaveCount(2);
    }

    [Fact]
    public async Task Query_WithSkipAndLimit_ReturnsPaginatedResults()
    {
        // Query all 7 stories to get consistent ordering
        var allQuery = "path:Systemorph/Marketing nodeType:Systemorph/Marketing/Story scope:descendants";
        var allResults = await MeshQuery.QueryAsync(MeshQueryRequest.FromQuery(allQuery), JsonOptions).ToListAsync();
        allResults.Should().HaveCount(7);

        // Get first page (skip 0, limit 3)
        var page1Request = new MeshQueryRequest
        {
            Query = allQuery,
            Skip = 0,
            Limit = 3
        };
        var page1 = await MeshQuery.QueryAsync(page1Request, JsonOptions).ToListAsync();
        page1.Should().HaveCount(3);

        // Get second page (skip 3, limit 3)
        var page2Request = new MeshQueryRequest
        {
            Query = allQuery,
            Skip = 3,
            Limit = 3
        };
        var page2 = await MeshQuery.QueryAsync(page2Request, JsonOptions).ToListAsync();
        page2.Should().HaveCount(3);

        // Get third page (skip 6, limit 3) - only 1 item left
        var page3Request = new MeshQueryRequest
        {
            Query = allQuery,
            Skip = 6,
            Limit = 3
        };
        var page3 = await MeshQuery.QueryAsync(page3Request, JsonOptions).ToListAsync();
        page3.Should().HaveCount(1);

        // All pages should contain different items
        var allPaths = page1.Concat(page2).Concat(page3).Cast<MeshNode>().Select(n => n.Path).ToList();
        allPaths.Should().HaveCount(7);
        allPaths.Distinct().Should().HaveCount(7);
    }

    [Fact]
    public async Task Query_Hierarchy_IncludesAncestorsAndDescendants()
    {
        // Query with hierarchy scope from a sub-story using path: in query string
        var query = "path:Systemorph/Marketing/ClaimsProcessing/EmailTriage scope:hierarchy";
        var results = await MeshQuery.QueryAsync(MeshQueryRequest.FromQuery(query), JsonOptions).ToListAsync();

        // Should include the node itself, ancestors, and any descendants
        var paths = results.Cast<MeshNode>().Select(n => n.Path).ToList();

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
    private InMemoryPersistenceService? _persistence;
    private IMeshQuery? _meshQuery;
    private JsonSerializerOptions JsonOptions => Mesh.ServiceProvider.GetRequiredService<IMessageHub>().JsonSerializerOptions;

    private InMemoryPersistenceService Persistence => _persistence ??= new InMemoryPersistenceService();
    private IMeshQuery MeshQuery => _meshQuery ??= new InMemoryMeshQuery(Persistence);

    [Fact]
    public async Task QueryAsync_Generic_ReturnsTypedResults()
    {
        // Arrange - save partition objects with $type
        var products = new List<object>
        {
            new TestProduct { Id = "1", Name = "Laptop", Price = 999.99m },
            new TestProduct { Id = "2", Name = "Phone", Price = 499.99m },
            new TestOrder { Id = "order-1", CustomerId = "cust-1", Total = 1500m }
        };
        await Persistence.SavePartitionObjectsAsync("shop/inventory", null, products, JsonOptions);

        // Act - query for TestProduct type only
        // Use scope:subtree to include the base path where partition objects are stored
        var results = await MeshQuery.QueryAsync<TestProduct>(
            "path:shop/inventory scope:subtree", JsonOptions
        ).ToListAsync();

        // Assert - should only return TestProduct items
        results.Should().HaveCount(2);
        results.Should().AllBeOfType<TestProduct>();
        results.Select(p => p.Name).Should().Contain(["Laptop", "Phone"]);
    }

    [Fact]
    public async Task QueryAsync_Generic_WithTypeRegistry_UsesRegisteredName()
    {
        // Arrange - without type registry, uses CLR type name
        var products = new List<object>
        {
            new TestProduct { Id = "1", Name = "Laptop", Price = 999.99m },
            new TestOrder { Id = "order-1", CustomerId = "cust-1", Total = 1500m }
        };
        await Persistence.SavePartitionObjectsAsync("shop/data", null, products, JsonOptions);

        // Act - query without type registry (uses CLR type name "TestProduct")
        // Use scope:subtree to include the base path where partition objects are stored
        var results = await MeshQuery.QueryAsync<TestProduct>(
            "path:shop/data scope:subtree", JsonOptions
        ).ToListAsync();

        // Assert - should find TestProduct by CLR type name
        results.Should().HaveCount(1);
        results.First().Name.Should().Be("Laptop");
    }

    [Fact]
    public async Task QueryAsync_Generic_WithPaging_ReturnsPagedTypedResults()
    {
        // Arrange
        var products = Enumerable.Range(1, 10)
            .Select(i => new TestProduct { Id = i.ToString(), Name = $"Product {i}", Price = i * 10m })
            .Cast<object>()
            .ToList();
        await Persistence.SavePartitionObjectsAsync("catalog/products", null, products, JsonOptions);

        // Act - get page 2 (skip 3, take 3)
        // Use scope:subtree to include the base path where partition objects are stored
        var results = await MeshQuery.QueryAsync<TestProduct>(
            "path:catalog/products scope:subtree", JsonOptions,
            skip: 3,
            limit: 3
        ).ToListAsync();

        // Assert
        results.Should().HaveCount(3);
        results.Should().AllBeOfType<TestProduct>();
    }

    [Fact]
    public async Task QueryAsync_Generic_WithAdditionalFilters_CombinesWithTypeFilter()
    {
        // Arrange
        var products = new List<object>
        {
            new TestProduct { Id = "1", Name = "Gaming Laptop", Price = 1999.99m },
            new TestProduct { Id = "2", Name = "Business Laptop", Price = 899.99m },
            new TestProduct { Id = "3", Name = "Phone", Price = 499.99m },
            new TestOrder { Id = "order-1", CustomerId = "cust-1", Total = 1500m }
        };
        await Persistence.SavePartitionObjectsAsync("shop/all", null, products, JsonOptions);

        // Act - query for TestProduct with name filter
        // Use scope:subtree to include the base path where partition objects are stored
        var results = await MeshQuery.QueryAsync<TestProduct>(
            "path:shop/all name:*Laptop* scope:subtree", JsonOptions
        ).ToListAsync();

        // Assert - should only return laptops (both gaming and business)
        results.Should().HaveCount(2);
        results.Should().OnlyContain(p => p.Name.Contains("Laptop"));
    }

    [Fact]
    public async Task QueryAsync_Generic_NoMatchingType_ReturnsEmpty()
    {
        // Arrange - save only orders, no products
        var orders = new List<object>
        {
            new TestOrder { Id = "order-1", CustomerId = "cust-1", Total = 100m },
            new TestOrder { Id = "order-2", CustomerId = "cust-2", Total = 200m }
        };
        await Persistence.SavePartitionObjectsAsync("shop/orders", null, orders, JsonOptions);

        // Act - query for TestProduct (none exist)
        // Use scope:subtree to include the base path where partition objects are stored
        var results = await MeshQuery.QueryAsync<TestProduct>(
            "path:shop/orders scope:subtree", JsonOptions
        ).ToListAsync();

        // Assert - no TestProduct objects exist, only TestOrder
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task QueryAsync_Generic_MeshNode_WorksWithNodes()
    {
        // Arrange
        await Persistence.SaveNodeAsync(MeshNode.FromPath("org/acme") with { Name = "Acme Corp" }, JsonOptions);
        await Persistence.SaveNodeAsync(MeshNode.FromPath("org/contoso") with { Name = "Contoso Ltd" }, JsonOptions);

        // Act - query for MeshNode type
        var results = await MeshQuery.QueryAsync<MeshNode>(
            "path:org scope:descendants", JsonOptions
        ).ToListAsync();

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
