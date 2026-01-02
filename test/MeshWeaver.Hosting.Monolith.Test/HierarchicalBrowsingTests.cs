using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Tests for hierarchical browsing of Marketing stories.
/// Validates parent/child relationships, sub-stories, and namespace queries.
/// </summary>
public class HierarchicalBrowsingTests
{
    private readonly InMemoryPersistenceService _persistence = new();
    private readonly InMemoryMeshQuery _meshQuery;

    public HierarchicalBrowsingTests()
    {
        _meshQuery = new InMemoryMeshQuery(_persistence);
        SetupMarketingHierarchy().GetAwaiter().GetResult();
    }

    private async Task SetupMarketingHierarchy()
    {
        // Create the Marketing story hierarchy similar to sample data
        // Parent stories
        await _persistence.SaveNodeAsync(MeshNode.FromPath("Systemorph/Marketing") with
        {
            Name = "Marketing",
            NodeType = "Namespace",
            Description = "Marketing namespace"
        });

        await _persistence.SaveNodeAsync(MeshNode.FromPath("Systemorph/Marketing/ClaimsProcessing") with
        {
            Name = "Claims Processing",
            NodeType = "Systemorph/Marketing/Story",
            Description = "Claims processing and email management use case"
        });

        await _persistence.SaveNodeAsync(MeshNode.FromPath("Systemorph/Marketing/DataIngestionStrategy") with
        {
            Name = "Data Ingestion Strategy",
            NodeType = "Systemorph/Marketing/Story",
            Description = "Data ingestion and integration patterns"
        });

        // Sub-stories of ClaimsProcessing
        await _persistence.SaveNodeAsync(MeshNode.FromPath("Systemorph/Marketing/ClaimsProcessing/EmailTriage") with
        {
            Name = "Email Triage",
            NodeType = "Systemorph/Marketing/Story",
            Description = "AI-driven email classification, prioritization, and routing"
        });

        await _persistence.SaveNodeAsync(MeshNode.FromPath("Systemorph/Marketing/ClaimsProcessing/DocumentExtraction") with
        {
            Name = "Document Extraction",
            NodeType = "Systemorph/Marketing/Story",
            Description = "Extract structured data from claims documents"
        });

        await _persistence.SaveNodeAsync(MeshNode.FromPath("Systemorph/Marketing/ClaimsProcessing/ClientCorrespondence") with
        {
            Name = "Client Correspondence",
            NodeType = "Systemorph/Marketing/Story",
            Description = "AI-assisted client communication"
        });

        // Sub-stories of DataIngestionStrategy
        await _persistence.SaveNodeAsync(MeshNode.FromPath("Systemorph/Marketing/DataIngestionStrategy/AnnotatedDataModel") with
        {
            Name = "Annotated Data Model",
            NodeType = "Systemorph/Marketing/Story",
            Description = "Self-documenting data models"
        });

        await _persistence.SaveNodeAsync(MeshNode.FromPath("Systemorph/Marketing/DataIngestionStrategy/HistoricIngestion") with
        {
            Name = "Historic Ingestion",
            NodeType = "Systemorph/Marketing/Story",
            Description = "Batch ingestion of historical data"
        });
    }

    [Fact]
    public async Task Query_TopLevel_ShowsAllStories()
    {
        // Query all Story nodes under Marketing
        var query = "nodeType:Systemorph/Marketing/Story scope:descendants";
        var results = await _persistence.QueryAsync(query, "Systemorph/Marketing").ToListAsync();

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
        // Query stories under ClaimsProcessing only
        // scope:descendants includes the base path itself, so we expect 4 items
        var query = "nodeType:Systemorph/Marketing/Story scope:descendants";
        var results = await _persistence.QueryAsync(query, "Systemorph/Marketing/ClaimsProcessing").ToListAsync();

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
    public async Task Query_ByNamespace_RestrictsResults()
    {
        // Use IMeshQuery with namespace restriction
        var request = new MeshQueryRequest
        {
            Query = "nodeType:Systemorph/Marketing/Story scope:descendants",
            BasePath = "",
            Namespace = "Systemorph/Marketing/ClaimsProcessing"
        };

        var results = new List<object>();
        await foreach (var item in _meshQuery.QueryAsync(request))
        {
            results.Add(item);
        }

        // Should only return nodes under ClaimsProcessing namespace (ClaimsProcessing + 3 sub-stories = 4)
        results.Should().HaveCount(4);
        var paths = results.Cast<MeshNode>().Select(n => n.Path);
        paths.Should().AllSatisfy(p => p.Should().StartWith("Systemorph/Marketing/ClaimsProcessing"));
    }

    [Fact]
    public async Task Query_ParentStory_HasCorrectChildren()
    {
        // Get ClaimsProcessing node
        var claimsNode = await _persistence.GetNodeAsync("Systemorph/Marketing/ClaimsProcessing");
        claimsNode.Should().NotBeNull();

        // Get direct children
        var children = await _persistence.GetChildrenAsync(claimsNode!.Path).ToListAsync();

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
        var emailTriageNode = await _persistence.GetNodeAsync("Systemorph/Marketing/ClaimsProcessing/EmailTriage");
        emailTriageNode.Should().NotBeNull();

        // Verify parent path
        emailTriageNode!.ParentPath.Should().Be("Systemorph/Marketing/ClaimsProcessing");

        // Get parent
        var parentNode = await _persistence.GetNodeAsync(emailTriageNode.ParentPath);
        parentNode.Should().NotBeNull();
        parentNode!.Name.Should().Be("Claims Processing");
    }

    [Fact]
    public async Task Autocomplete_ReturnsMatchingNodes()
    {
        // Test autocomplete for "Email"
        var suggestions = await _meshQuery.AutocompleteAsync("Systemorph/Marketing", "Email", 10).ToListAsync();

        suggestions.Should().HaveCount(1);
        suggestions[0].Name.Should().Be("Email Triage");
        suggestions[0].Path.Should().Be("Systemorph/Marketing/ClaimsProcessing/EmailTriage");
    }

    [Fact]
    public async Task Autocomplete_FuzzyMatching_FindsPartialMatches()
    {
        // Test autocomplete with partial match
        var suggestions = await _meshQuery.AutocompleteAsync("Systemorph/Marketing", "claim", 10).ToListAsync();

        // Should find Claims Processing and its sub-stories
        suggestions.Should().Contain(s => s.Name == "Claims Processing");
    }

    [Fact]
    public async Task Query_TextSearch_FindsMatchingDescriptions()
    {
        // Search for "AI" in descriptions
        var results = await _persistence.QueryAsync("AI scope:descendants", "Systemorph/Marketing").ToListAsync();

        // Should find Email Triage and Client Correspondence which mention AI
        results.Should().HaveCountGreaterThanOrEqualTo(2);
        var names = results.Cast<MeshNode>().Select(n => n.Name).ToList();
        names.Should().Contain("Email Triage");
        names.Should().Contain("Client Correspondence");
    }

    [Fact]
    public async Task Query_WithLimit_RespectsLimit()
    {
        var request = new MeshQueryRequest
        {
            Query = "nodeType:Systemorph/Marketing/Story scope:descendants",
            BasePath = "Systemorph/Marketing",
            Limit = 3
        };

        var results = new List<object>();
        await foreach (var item in _meshQuery.QueryAsync(request))
        {
            results.Add(item);
        }

        results.Should().HaveCount(3);
    }

    [Fact]
    public async Task Query_Hierarchy_IncludesAncestorsAndDescendants()
    {
        // Query with hierarchy scope from a sub-story
        var results = await _persistence.QueryAsync("scope:hierarchy", "Systemorph/Marketing/ClaimsProcessing/EmailTriage").ToListAsync();

        // Should include the node itself, ancestors, and any descendants
        var paths = results.Cast<MeshNode>().Select(n => n.Path).ToList();

        // Should include ancestors
        paths.Should().Contain("Systemorph/Marketing/ClaimsProcessing");
        paths.Should().Contain("Systemorph/Marketing");
    }
}
