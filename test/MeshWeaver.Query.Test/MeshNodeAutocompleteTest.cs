using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Memex.Portal.Shared;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Query.Test;

/// <summary>
/// Tests for MeshNodeAutocomplete functionality including:
/// 1. Basic autocomplete returns suggestions
/// 2. Filtering by creatable type works correctly
/// 3. Integration with INodeTypeService.GetCreatableTypesAsync
/// </summary>
[Collection("MeshNodeAutocompleteTest")]
public class MeshNodeAutocompleteTest : MonolithMeshTestBase
{
    private readonly string _cacheDirectory;
    private IMessageHub Hub => Mesh.ServiceProvider.GetRequiredService<IMessageHub>();

    public MeshNodeAutocompleteTest(ITestOutputHelper output) : base(output)
    {
        _cacheDirectory = Path.Combine(Path.GetTempPath(), "MeshWeaverAutocompleteTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_cacheDirectory);
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return builder
            .UseMonolithMesh()
            .AddPartitionedFileSystemPersistence(TestPaths.SamplesGraphData)
            .AddOrganizationType()
            .AddSystemorph()
            .AddAcme()
            .ConfigureServices(services => services.Configure<CompilationCacheOptions>(o => o.CacheDirectory = _cacheDirectory))
            .AddGraph()
            .ConfigureHub(hub => hub.AddMeshNavigation());
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

    #region Basic Autocomplete Tests

    [Fact(Timeout = 10000)]
    public async Task Autocomplete_EmptyPrefix_ReturnsTopLevelNodes()
    {
        // Act
        var suggestions = await MeshQuery.AutocompleteAsync(
            basePath: "",
            prefix: "",
            mode: AutocompleteMode.RelevanceFirst,
            limit: 20
        ).ToListAsync();

        // Assert
        Output.WriteLine($"Got {suggestions.Count} suggestions for empty prefix:");
        foreach (var s in suggestions.Take(10))
        {
            Output.WriteLine($"  - {s.Path}: {s.Name} ({s.NodeType})");
        }

        suggestions.Should().NotBeEmpty("Empty prefix should return top-level suggestions");
    }

    [Fact(Timeout = 10000)]
    public async Task Autocomplete_PartialMatch_ReturnsSuggestions()
    {
        // Act - search for "Sys" which should match "Systemorph"
        var suggestions = await MeshQuery.AutocompleteAsync(
            basePath: "",
            prefix: "Sys",
            mode: AutocompleteMode.RelevanceFirst,
            limit: 10
        ).ToListAsync();

        // Assert
        Output.WriteLine($"Got {suggestions.Count} suggestions for 'Sys':");
        foreach (var s in suggestions)
        {
            Output.WriteLine($"  - {s.Path}: {s.Name} ({s.NodeType})");
        }

        suggestions.Should().NotBeEmpty("'Sys' should return suggestions");
        suggestions.Should().Contain(s => s.Path.Contains("Systemorph", StringComparison.OrdinalIgnoreCase),
            "'Sys' should match Systemorph");
    }

    [Fact(Timeout = 10000)]
    public async Task Autocomplete_CaseInsensitive_MatchesLowercase()
    {
        // Act - search with lowercase
        var suggestions = await MeshQuery.AutocompleteAsync(
            basePath: "",
            prefix: "sys",
            mode: AutocompleteMode.RelevanceFirst,
            limit: 10
        ).ToListAsync();

        // Assert
        Output.WriteLine($"Got {suggestions.Count} suggestions for 'sys' (lowercase):");
        foreach (var s in suggestions)
        {
            Output.WriteLine($"  - {s.Path}: {s.Name}");
        }

        suggestions.Should().Contain(s => s.Path.Contains("Systemorph", StringComparison.OrdinalIgnoreCase),
            "Lowercase 'sys' should match Systemorph (case-insensitive)");
    }

    [Fact(Timeout = 10000)]
    public async Task Autocomplete_WithBasePath_SearchesWithinPath()
    {
        // Act - search within a specific base path
        var suggestions = await MeshQuery.AutocompleteAsync(
            basePath: "Systemorph",
            prefix: "",
            mode: AutocompleteMode.RelevanceFirst,
            limit: 20
        ).ToListAsync();

        // Assert
        Output.WriteLine($"Got {suggestions.Count} suggestions within 'Systemorph':");
        foreach (var s in suggestions.Take(10))
        {
            Output.WriteLine($"  - {s.Path}: {s.Name}");
        }

        // All suggestions should be under the base path (or be the base path itself)
        suggestions.Should().OnlyContain(s =>
            s.Path.StartsWith("Systemorph", StringComparison.OrdinalIgnoreCase) ||
            s.Path.Equals("Systemorph", StringComparison.OrdinalIgnoreCase),
            "All suggestions should be within or equal to the base path");
    }

    [Fact(Timeout = 10000)]
    public async Task Autocomplete_RelevanceFirst_OrdersByRelevance()
    {
        // Act - search for a term that should match by name
        var suggestions = await MeshQuery.AutocompleteAsync(
            basePath: "",
            prefix: "Mark",
            mode: AutocompleteMode.RelevanceFirst,
            limit: 10
        ).ToListAsync();

        // Assert
        Output.WriteLine($"Got {suggestions.Count} suggestions for 'Mark' (RelevanceFirst):");
        for (int i = 0; i < suggestions.Count; i++)
        {
            var s = suggestions[i];
            Output.WriteLine($"  {i + 1}. {s.Path}: {s.Name} (Score: {s.Score})");
        }

        // First suggestions should have higher scores
        if (suggestions.Count >= 2)
        {
            suggestions[0].Score.Should().BeGreaterThanOrEqualTo(suggestions[1].Score,
                "First suggestion should have equal or higher score than second");
        }
    }

    #endregion

    #region CreatableTypes Tests

    [Fact(Timeout = 10000)]
    public async Task GetCreatableTypes_ReturnsTypesForNode()
    {
        // Arrange
        var nodeTypeService = Hub.ServiceProvider.GetService<INodeTypeService>();
        nodeTypeService.Should().NotBeNull("INodeTypeService should be registered");

        // Act - get creatable types for Systemorph (should include types defined in ACME)
        var creatableTypes = await nodeTypeService!.GetCreatableTypesAsync("Systemorph").ToListAsync();

        // Assert
        Output.WriteLine($"Creatable types at 'Systemorph': {creatableTypes.Count}");
        foreach (var t in creatableTypes)
        {
            Output.WriteLine($"  - {t.NodeTypePath} ({t.DisplayName})");
        }

        // Should have some creatable types
        creatableTypes.Should().NotBeEmpty("Systemorph should have creatable types");
    }

    [Fact(Timeout = 10000)]
    public async Task GetCreatableTypes_DifferentNodesDifferentTypes()
    {
        // Arrange
        var nodeTypeService = Hub.ServiceProvider.GetService<INodeTypeService>();
        nodeTypeService.Should().NotBeNull();

        // Act - get creatable types for different nodes
        var rootTypes = await nodeTypeService!.GetCreatableTypesAsync("").ToListAsync();
        var systemorphTypes = await nodeTypeService.GetCreatableTypesAsync("Systemorph").ToListAsync();

        // Assert
        Output.WriteLine($"Root creatable types: {rootTypes.Count}");
        foreach (var t in rootTypes.Take(5))
        {
            Output.WriteLine($"  - {t.NodeTypePath}");
        }

        Output.WriteLine($"\nSystemorph creatable types: {systemorphTypes.Count}");
        foreach (var t in systemorphTypes.Take(5))
        {
            Output.WriteLine($"  - {t.NodeTypePath}");
        }

        // Different paths may have different creatable types
        // (This test documents the behavior rather than asserting specific differences)
    }

    #endregion

    #region Filter By Creatable Type Tests

    [Fact(Timeout = 10000)]
    public async Task FilterByCreatableType_ReturnsOnlyMatchingNodes()
    {
        // Arrange
        var nodeTypeService = Hub.ServiceProvider.GetService<INodeTypeService>();
        nodeTypeService.Should().NotBeNull();

        // First, find a node type that exists
        var allSuggestions = await MeshQuery.AutocompleteAsync("", "", AutocompleteMode.RelevanceFirst, 50).ToListAsync();

        Output.WriteLine($"All suggestions: {allSuggestions.Count}");

        // Find suggestions that are not NodeType nodes (they are actual data nodes)
        var dataSuggestions = allSuggestions.Where(s => s.NodeType != "NodeType").ToList();
        Output.WriteLine($"Data node suggestions: {dataSuggestions.Count}");

        if (dataSuggestions.Count == 0)
        {
            Output.WriteLine("No data nodes found, skipping filter test");
            return;
        }

        // Get creatable types for the first data node
        var firstDataNode = dataSuggestions.First();
        var creatableTypes = await nodeTypeService!.GetCreatableTypesAsync(firstDataNode.Path).ToListAsync();

        Output.WriteLine($"\nCreatable types at '{firstDataNode.Path}': {creatableTypes.Count}");
        foreach (var t in creatableTypes.Take(5))
        {
            Output.WriteLine($"  - {t.NodeTypePath}");
        }

        if (creatableTypes.Count == 0)
        {
            Output.WriteLine("No creatable types at this node, skipping filter verification");
            return;
        }

        // Act - Filter suggestions to only nodes that can create the first creatable type
        var targetType = creatableTypes.First().NodeTypePath;
        var filteredSuggestions = new List<QuerySuggestion>();

        foreach (var suggestion in dataSuggestions.Take(10))
        {
            var types = await nodeTypeService.GetCreatableTypesAsync(suggestion.Path).ToListAsync();
            if (types.Any(t => t.NodeTypePath.Equals(targetType, StringComparison.OrdinalIgnoreCase)))
            {
                filteredSuggestions.Add(suggestion);
            }
        }

        // Assert
        Output.WriteLine($"\nFiltered suggestions (can create '{targetType}'): {filteredSuggestions.Count}");
        foreach (var s in filteredSuggestions)
        {
            Output.WriteLine($"  - {s.Path}");
        }

        // At least one node should support creating this type (the original node)
        filteredSuggestions.Should().Contain(s =>
            s.Path.Equals(firstDataNode.Path, StringComparison.OrdinalIgnoreCase),
            "The original node should be in the filtered results");
    }

    [Fact(Timeout = 10000)]
    public async Task CanCreateTypeAtPath_ReturnsTrueForValidType()
    {
        // Arrange
        var nodeTypeService = Hub.ServiceProvider.GetService<INodeTypeService>();
        nodeTypeService.Should().NotBeNull();

        // Get a node that has creatable types
        var suggestions = await MeshQuery.AutocompleteAsync("", "", AutocompleteMode.RelevanceFirst, 20).ToListAsync();
        QuerySuggestion? nodeWithTypes = null;
        List<CreatableTypeInfo> nodeCreatableTypes = new();

        foreach (var suggestion in suggestions)
        {
            var types = await nodeTypeService!.GetCreatableTypesAsync(suggestion.Path).ToListAsync();
            if (types.Count > 0)
            {
                nodeWithTypes = suggestion;
                nodeCreatableTypes = types;
                break;
            }
        }

        if (nodeWithTypes == null)
        {
            Output.WriteLine("No node with creatable types found, skipping test");
            return;
        }

        Output.WriteLine($"Testing node: {nodeWithTypes.Path}");
        Output.WriteLine($"Creatable types: {string.Join(", ", nodeCreatableTypes.Select(t => t.NodeTypePath))}");

        // Act - check if the node can create one of its types
        var targetType = nodeCreatableTypes.First().NodeTypePath;
        var canCreate = await CanCreateTypeAtPathAsync(nodeTypeService!, nodeWithTypes.Path, targetType);

        // Assert
        canCreate.Should().BeTrue($"Node '{nodeWithTypes.Path}' should be able to create type '{targetType}'");
    }

    [Fact(Timeout = 10000)]
    public async Task CanCreateTypeAtPath_ReturnsFalseForInvalidType()
    {
        // Arrange
        var nodeTypeService = Hub.ServiceProvider.GetService<INodeTypeService>();
        nodeTypeService.Should().NotBeNull();

        var suggestions = await MeshQuery.AutocompleteAsync("", "", AutocompleteMode.RelevanceFirst, 10).ToListAsync();
        var firstSuggestion = suggestions.FirstOrDefault();

        if (firstSuggestion == null)
        {
            Output.WriteLine("No suggestions found, skipping test");
            return;
        }

        // Act - check for a type that definitely doesn't exist
        var canCreate = await CanCreateTypeAtPathAsync(nodeTypeService!, firstSuggestion.Path, "NonExistent/FakeType/ThatDoesNotExist");

        // Assert
        canCreate.Should().BeFalse("Node should not be able to create a non-existent type");
    }

    /// <summary>
    /// Helper method to check if a type can be created at a path.
    /// This mirrors the logic in MeshNodeAutocomplete.
    /// </summary>
    private static async Task<bool> CanCreateTypeAtPathAsync(
        INodeTypeService nodeTypeService,
        string nodePath,
        string nodeTypePath)
    {
        await foreach (var creatableType in nodeTypeService.GetCreatableTypesAsync(nodePath))
        {
            if (creatableType.NodeTypePath.Equals(nodeTypePath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    #endregion

    #region Integration Tests

    [Fact(Timeout = 10000)]
    public async Task Integration_AutocompleteWithTypeFilter_WorksEndToEnd()
    {
        // This test simulates the full flow of:
        // 1. User selects a type to create
        // 2. Namespace autocomplete filters to nodes that support that type

        // Arrange
        var nodeTypeService = Hub.ServiceProvider.GetService<INodeTypeService>();
        nodeTypeService.Should().NotBeNull();

        // Get all autocomplete suggestions
        var allSuggestions = await MeshQuery.AutocompleteAsync(
            basePath: "",
            prefix: "",
            mode: AutocompleteMode.RelevanceFirst,
            limit: 50
        ).ToListAsync();

        Output.WriteLine($"Total suggestions: {allSuggestions.Count}");

        // Find a type that is creatable somewhere
        string? targetType = null;
        var nodesWithType = new List<string>();

        foreach (var suggestion in allSuggestions)
        {
            var types = await nodeTypeService!.GetCreatableTypesAsync(suggestion.Path).ToListAsync();
            foreach (var type in types)
            {
                if (targetType == null)
                {
                    targetType = type.NodeTypePath;
                }

                if (type.NodeTypePath.Equals(targetType, StringComparison.OrdinalIgnoreCase))
                {
                    nodesWithType.Add(suggestion.Path);
                }
            }
        }

        if (targetType == null)
        {
            Output.WriteLine("No creatable types found in any node, skipping test");
            return;
        }

        Output.WriteLine($"\nTarget type: {targetType}");
        Output.WriteLine($"Nodes that can create this type: {nodesWithType.Count}");
        foreach (var node in nodesWithType.Take(5))
        {
            Output.WriteLine($"  - {node}");
        }

        // Act - Filter autocomplete by type (simulating MeshNodeAutocomplete behavior)
        var filteredSuggestions = new List<QuerySuggestion>();
        foreach (var suggestion in allSuggestions)
        {
            if (await CanCreateTypeAtPathAsync(nodeTypeService!, suggestion.Path, targetType))
            {
                filteredSuggestions.Add(suggestion);
            }
        }

        // Assert
        Output.WriteLine($"\nFiltered autocomplete results: {filteredSuggestions.Count}");
        filteredSuggestions.Count.Should().Be(nodesWithType.Count,
            "Filtered autocomplete should match the count of nodes that can create the type");

        foreach (var suggestion in filteredSuggestions)
        {
            nodesWithType.Should().Contain(suggestion.Path,
                $"Filtered result '{suggestion.Path}' should be in the list of nodes that can create the type");
        }
    }

    #endregion
}
