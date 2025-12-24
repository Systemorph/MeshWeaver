using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Domain;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Options;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Integration tests for MeshNodeCompilationService - the public API for compiling
/// MeshNode assemblies on demand.
/// </summary>
public class MeshNodeCompilationServiceTest : IDisposable
{
    private readonly string _testCacheDir;
    private readonly ITypeRegistry _typeRegistry;
    private readonly IOptions<CompilationCacheOptions> _cacheOptions;

    public MeshNodeCompilationServiceTest()
    {
        _testCacheDir = Path.Combine(Path.GetTempPath(), $"meshnode-compile-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testCacheDir);

        _typeRegistry = new TestTypeRegistry();

        _cacheOptions = Options.Create(new CompilationCacheOptions
        {
            CacheDirectory = _testCacheDir,
            EnableCompilationCache = true,
            EnableSourceDebugging = true
        });
    }

    public void Dispose()
    {
        if (Directory.Exists(_testCacheDir))
        {
            try
            {
                Directory.Delete(_testCacheDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    [Fact(Timeout = 15000)]
    public async Task GetAssemblyLocationAsync_ReturnsNull_WhenNodeTypeIsNull()
    {
        // Arrange
        var nodeTypeService = new TestNodeTypeService();
        var service = new MeshNodeCompilationService(nodeTypeService, _typeRegistry, _cacheOptions);

        var node = new MeshNode("test/no-type")
        {
            Name = "No Type Node",
            NodeType = null,
            LastModified = DateTimeOffset.UtcNow
        };

        // Act
        var result = await service.GetAssemblyLocationAsync(node);

        // Assert
        result.Should().BeNull("Node with no NodeType should not compile");
    }

    [Fact(Timeout = 15000)]
    public async Task GetAssemblyLocationAsync_ReturnsNull_WhenDataModelNotFound()
    {
        // Arrange
        var nodeTypeService = new TestNodeTypeService(); // Empty - no data models
        var service = new MeshNodeCompilationService(nodeTypeService, _typeRegistry, _cacheOptions);

        var node = new MeshNode("test/unknown-type")
        {
            Name = "Unknown Type Node",
            NodeType = "unknown-type",
            LastModified = DateTimeOffset.UtcNow
        };

        // Act
        var result = await service.GetAssemblyLocationAsync(node);

        // Assert
        result.Should().BeNull("Node with unknown NodeType should not compile");
    }

    [Fact(Timeout = 20000)]
    public async Task GetAssemblyLocationAsync_ReturnsPathToCompiledDll()
    {
        // Arrange
        var dataModel = new DataModel
        {
            Id = "story",
            DisplayName = "Story",
            TypeSource = @"
public record StoryType
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
}"
        };

        var nodeTypeService = new TestNodeTypeService();
        nodeTypeService.AddDataModel("story", dataModel);

        var service = new MeshNodeCompilationService(nodeTypeService, _typeRegistry, _cacheOptions);

        var node = new MeshNode("org/stories/my-story")
        {
            Name = "My Story",
            NodeType = "story",
            LastModified = DateTimeOffset.UtcNow
        };

        // Act
        var assemblyPath = await service.GetAssemblyLocationAsync(node);

        // Assert
        assemblyPath.Should().NotBeNull("Assembly should be compiled");
        File.Exists(assemblyPath).Should().BeTrue("DLL file should exist at returned path");
        assemblyPath.Should().EndWith(".dll");
    }

    [Fact(Timeout = 20000)]
    public async Task GetAssemblyLocationAsync_AssemblyContainsCompiledType()
    {
        // Arrange
        var dataModel = new DataModel
        {
            Id = "project",
            DisplayName = "Project",
            TypeSource = @"
public record ProjectType
{
    public string Name { get; init; } = string.Empty;
}"
        };

        var nodeTypeService = new TestNodeTypeService();
        nodeTypeService.AddDataModel("project", dataModel);

        var service = new MeshNodeCompilationService(nodeTypeService, _typeRegistry, _cacheOptions);

        var node = new MeshNode("org/projects/my-project")
        {
            Name = "My Project",
            NodeType = "project",
            LastModified = DateTimeOffset.UtcNow
        };

        // Act
        var assemblyPath = await service.GetAssemblyLocationAsync(node);

        // Assert
        assemblyPath.Should().NotBeNull();
        var assembly = Assembly.LoadFrom(assemblyPath!);
        assembly.GetType("MeshWeaver.Graph.Dynamic.ProjectType").Should().NotBeNull();
    }

    [Fact(Timeout = 20000)]
    public async Task GetAssemblyLocationAsync_UsesCachedAssembly()
    {
        // Arrange
        var dataModel = new DataModel
        {
            Id = "cached-item",
            DisplayName = "Cached Item",
            TypeSource = @"
public record CachedItemType
{
    public string Value { get; init; } = string.Empty;
}"
        };

        var nodeTypeService = new TestNodeTypeService();
        nodeTypeService.AddDataModel("cached-item", dataModel);

        var service = new MeshNodeCompilationService(nodeTypeService, _typeRegistry, _cacheOptions);

        var node = new MeshNode("org/items/cached")
        {
            Name = "Cached Item",
            NodeType = "cached-item",
            LastModified = DateTimeOffset.UtcNow.AddMinutes(-5) // Old modification time
        };

        // First call compiles
        var firstPath = await service.GetAssemblyLocationAsync(node);
        firstPath.Should().NotBeNull();

        var firstWriteTime = File.GetLastWriteTimeUtc(firstPath!);
        await Task.Delay(100);

        // Act - Second call should use cache
        var secondPath = await service.GetAssemblyLocationAsync(node);

        // Assert
        secondPath.Should().NotBeNull();
        secondPath.Should().Be(firstPath);
        var secondWriteTime = File.GetLastWriteTimeUtc(secondPath!);
        secondWriteTime.Should().Be(firstWriteTime, "DLL should not be recompiled on second call");
    }

    [Fact(Timeout = 20000)]
    public async Task GetAssemblyLocationAsync_GeneratesSourceFileForDebugging()
    {
        // Arrange
        var dataModel = new DataModel
        {
            Id = "debug-item",
            DisplayName = "Debug Item",
            TypeSource = @"
public record DebugItemType
{
    public string Data { get; init; } = string.Empty;
}"
        };

        var nodeTypeService = new TestNodeTypeService();
        nodeTypeService.AddDataModel("debug-item", dataModel);

        var service = new MeshNodeCompilationService(nodeTypeService, _typeRegistry, _cacheOptions);

        var node = new MeshNode("debug/items/test")
        {
            Name = "Debug Item",
            NodeType = "debug-item",
            LastModified = DateTimeOffset.UtcNow
        };

        // Act
        var assemblyPath = await service.GetAssemblyLocationAsync(node);

        // Assert - Source file should exist
        assemblyPath.Should().NotBeNull();
        var sourcePath = Path.ChangeExtension(assemblyPath!, ".cs");
        File.Exists(sourcePath).Should().BeTrue("Source file should be generated for debugging");
    }

    [Fact(Timeout = 25000)]
    public async Task GetAssemblyLocationAsync_AssemblyContainsMeshNodeAttribute()
    {
        // Arrange
        var dataModel = new DataModel
        {
            Id = "widget",
            DisplayName = "Widget",
            TypeSource = @"
public record WidgetType
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
}"
        };

        var nodeTypeService = new TestNodeTypeService();
        nodeTypeService.AddDataModel("widget", dataModel);

        var service = new MeshNodeCompilationService(nodeTypeService, _typeRegistry, _cacheOptions);

        var node = new MeshNode("org/widgets/my-widget")
        {
            Name = "My Widget",
            NodeType = "widget",
            Description = "A test widget",
            LastModified = DateTimeOffset.UtcNow
        };

        // Act
        var assemblyPath = await service.GetAssemblyLocationAsync(node);

        // Assert - Find MeshNodeAttribute in assembly
        assemblyPath.Should().NotBeNull();
        var assembly = Assembly.LoadFrom(assemblyPath!);

        var meshNodeAttributes = assembly.GetCustomAttributes()
            .Where(a => a.GetType().Name.EndsWith("MeshNodeAttribute"))
            .ToList();

        meshNodeAttributes.Should().NotBeEmpty("Assembly should contain MeshNodeAttribute");
    }

    [Fact(Timeout = 25000)]
    public async Task GetAssemblyLocationAsync_MeshNodeAttribute_ReturnsNodes()
    {
        // Arrange
        var dataModel = new DataModel
        {
            Id = "component",
            DisplayName = "Component",
            TypeSource = @"
public record ComponentType
{
    public string Id { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
}"
        };

        var nodeTypeService = new TestNodeTypeService();
        nodeTypeService.AddDataModel("component", dataModel);

        var service = new MeshNodeCompilationService(nodeTypeService, _typeRegistry, _cacheOptions);

        var node = new MeshNode("app/components/header")
        {
            Name = "Header Component",
            NodeType = "component",
            Description = "The main header",
            IconName = "Header",
            DisplayOrder = 1,
            LastModified = DateTimeOffset.UtcNow
        };

        // Act
        var assemblyPath = await service.GetAssemblyLocationAsync(node);

        // Assert - Get nodes from MeshNodeAttribute
        assemblyPath.Should().NotBeNull();
        var assembly = Assembly.LoadFrom(assemblyPath!);

        var meshNodeAttribute = assembly.GetCustomAttributes()
            .FirstOrDefault(a => a.GetType().Name.EndsWith("MeshNodeAttribute"));

        meshNodeAttribute.Should().NotBeNull("Assembly should have MeshNodeAttribute");

        // Get Nodes property
        var nodesProperty = meshNodeAttribute!.GetType().GetProperty("Nodes");
        nodesProperty.Should().NotBeNull("MeshNodeAttribute should have Nodes property");

        var nodes = nodesProperty!.GetValue(meshNodeAttribute) as IEnumerable<MeshNode>;
        nodes.Should().NotBeNull();

        var nodesList = nodes!.ToList();
        nodesList.Should().HaveCount(1);

        var loadedNode = nodesList[0];
        loadedNode.Path.Should().Be("app/components/header");
        loadedNode.Name.Should().Be("Header Component");
        loadedNode.NodeType.Should().Be("component");
        loadedNode.Description.Should().Be("The main header");
        loadedNode.IconName.Should().Be("Header");
        loadedNode.DisplayOrder.Should().Be(1);
    }

    [Fact(Timeout = 25000)]
    public async Task GetAssemblyLocationAsync_CompiledDataType_CanBeInstantiated()
    {
        // Arrange
        var dataModel = new DataModel
        {
            Id = "record",
            DisplayName = "Record",
            TypeSource = @"
public record RecordType
{
    public string Id { get; init; } = ""default-id"";
    public string Title { get; init; } = ""Default Title"";
    public int Count { get; init; } = 42;
}"
        };

        var nodeTypeService = new TestNodeTypeService();
        nodeTypeService.AddDataModel("record", dataModel);

        var service = new MeshNodeCompilationService(nodeTypeService, _typeRegistry, _cacheOptions);

        var node = new MeshNode("data/records/test")
        {
            Name = "Test Record",
            NodeType = "record",
            LastModified = DateTimeOffset.UtcNow
        };

        // Act
        var assemblyPath = await service.GetAssemblyLocationAsync(node);

        // Assert - Instantiate the compiled data type
        assemblyPath.Should().NotBeNull();
        var assembly = Assembly.LoadFrom(assemblyPath!);

        var recordType = assembly.GetType("MeshWeaver.Graph.Dynamic.RecordType");
        recordType.Should().NotBeNull();

        var instance = Activator.CreateInstance(recordType!);
        instance.Should().NotBeNull();

        // Verify default property values
        var idProperty = recordType!.GetProperty("Id");
        var titleProperty = recordType.GetProperty("Title");
        var countProperty = recordType.GetProperty("Count");

        idProperty!.GetValue(instance).Should().Be("default-id");
        titleProperty!.GetValue(instance).Should().Be("Default Title");
        countProperty!.GetValue(instance).Should().Be(42);
    }
}

/// <summary>
/// Test implementation of INodeTypeService for unit testing.
/// </summary>
internal class TestNodeTypeService : INodeTypeService
{
    private readonly Dictionary<string, DataModel> _dataModels = new();
    private readonly Dictionary<string, HubFeatureConfig> _hubFeatures = new();
    private readonly Dictionary<string, List<LayoutAreaConfig>> _layoutAreas = new();

    public void AddDataModel(string nodeType, DataModel dataModel)
    {
        _dataModels[nodeType] = dataModel;
    }

    public void AddHubFeatures(string nodeType, HubFeatureConfig hubFeatures)
    {
        _hubFeatures[nodeType] = hubFeatures;
    }

    public void AddLayoutArea(string nodeType, LayoutAreaConfig layoutArea)
    {
        if (!_layoutAreas.TryGetValue(nodeType, out var list))
        {
            list = new List<LayoutAreaConfig>();
            _layoutAreas[nodeType] = list;
        }
        list.Add(layoutArea);
    }

    public Task<DataModel?> GetDataModelAsync(string nodeType, string contextPath, CancellationToken ct = default)
    {
        _dataModels.TryGetValue(nodeType, out var dataModel);
        return Task.FromResult(dataModel);
    }

    public Task<HubFeatureConfig?> GetHubFeaturesAsync(string nodeType, string contextPath, CancellationToken ct = default)
    {
        _hubFeatures.TryGetValue(nodeType, out var hubFeatures);
        return Task.FromResult(hubFeatures);
    }

    public Task<IReadOnlyList<LayoutAreaConfig>> GetLayoutAreasAsync(string nodeType, string contextPath, CancellationToken ct = default)
    {
        if (_layoutAreas.TryGetValue(nodeType, out var list))
            return Task.FromResult<IReadOnlyList<LayoutAreaConfig>>(list);
        return Task.FromResult<IReadOnlyList<LayoutAreaConfig>>(Array.Empty<LayoutAreaConfig>());
    }

    public IAsyncEnumerable<MeshNode> GetNodeTypeNodesAsync(string contextPath) =>
        EmptyAsyncEnumerable<MeshNode>();

    public Task<MeshNode?> GetNodeTypeNodeAsync(string nodeType, string contextPath, CancellationToken ct = default) =>
        Task.FromResult<MeshNode?>(null);

    public Task SaveDataModelAsync(string nodeTypePath, DataModel dataModel, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task SaveLayoutAreaAsync(string nodeTypePath, LayoutAreaConfig layoutArea, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task DeleteLayoutAreaAsync(string nodeTypePath, string layoutAreaId, CancellationToken ct = default) =>
        Task.CompletedTask;

    public IAsyncEnumerable<MeshNode> GetAllNodeTypeNodesAsync() =>
        EmptyAsyncEnumerable<MeshNode>();

    public Task<IReadOnlyList<DataModel>> GetAllDataModelsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DataModel>>(new List<DataModel>(_dataModels.Values));

    public Task<IReadOnlyList<LayoutAreaConfig>> GetAllLayoutAreasAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<LayoutAreaConfig>>(
            _layoutAreas.Values.SelectMany(x => x).ToList());

    private static async IAsyncEnumerable<T> EmptyAsyncEnumerable<T>()
    {
        await Task.CompletedTask;
        yield break;
    }
}
