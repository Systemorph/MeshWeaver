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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Namotion.Reflection;
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
    private readonly ICompilationCacheService _cacheService;
    private readonly ITypeCompilationService _typeCompiler;

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

        _cacheService = new CompilationCacheService(_cacheOptions, NullLogger<CompilationCacheService>.Instance);
        _typeCompiler = new TypeCompilationService(_typeRegistry, _cacheService, _cacheOptions, NullLogger<TypeCompilationService>.Instance);
    }

    public void Dispose()
    {
        if (_cacheService is IDisposable disposable)
            disposable.Dispose();

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

    private MeshNodeCompilationService CreateService(TestNodeTypeService nodeTypeService) =>
        new(nodeTypeService, _cacheService, _typeCompiler, NullLogger<MeshNodeCompilationService>.Instance);

    [Fact(Timeout = 15000)]
    public async Task GetAssemblyLocationAsync_ReturnsNull_WhenNodeTypeIsNull()
    {
        // Arrange
        var nodeTypeService = new TestNodeTypeService();
        var service = CreateService(nodeTypeService);

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
        var service = CreateService(nodeTypeService);

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

        var service = CreateService(nodeTypeService);

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

        var service = CreateService(nodeTypeService);

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

        var service = CreateService(nodeTypeService);

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

        var service = CreateService(nodeTypeService);

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

        var service = CreateService(nodeTypeService);

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

        var service = CreateService(nodeTypeService);

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

        var service = CreateService(nodeTypeService);

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

    [Fact(Timeout = 25000)]
    public async Task GetAssemblyLocationAsync_GeneratesXmlDocumentation()
    {
        // Arrange - DataModel with XML documentation comments
        var dataModel = new DataModel
        {
            Id = "documented-item",
            DisplayName = "Documented Item",
            TypeSource = @"
/// <summary>
/// This is a documented item type for testing XML documentation generation.
/// </summary>
public record DocumentedItemType
{
    /// <summary>
    /// The unique identifier of the item.
    /// </summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// The title or name of the item.
    /// </summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// The count or quantity of items.
    /// </summary>
    public int Count { get; init; }
}"
        };

        var nodeTypeService = new TestNodeTypeService();
        nodeTypeService.AddDataModel("documented-item", dataModel);

        var service = CreateService(nodeTypeService);

        var node = new MeshNode("docs/items/test")
        {
            Name = "Test Documented Item",
            NodeType = "documented-item",
            LastModified = DateTimeOffset.UtcNow
        };

        // Act
        var assemblyPath = await service.GetAssemblyLocationAsync(node);

        // Assert - Verify XML documentation file exists
        // The XML file uses "DynamicNode_" prefix to match the assembly name for Namotion.Reflection
        assemblyPath.Should().NotBeNull();
        var dllDir = Path.GetDirectoryName(assemblyPath)!;
        var nodeName = Path.GetFileNameWithoutExtension(assemblyPath);
        var xmlDocPath = Path.Combine(dllDir, $"DynamicNode_{nodeName}.xml");
        File.Exists(xmlDocPath).Should().BeTrue("XML documentation file should be generated alongside DLL");

        // Verify XML documentation content
        var xmlContent = await File.ReadAllTextAsync(xmlDocPath);
        xmlContent.Should().Contain("This is a documented item type");
        xmlContent.Should().Contain("The unique identifier of the item");
        xmlContent.Should().Contain("The title or name of the item");
        xmlContent.Should().Contain("The count or quantity of items");
    }

    [Fact(Timeout = 25000)]
    public async Task GetAssemblyLocationAsync_XmlDocs_CanBeReadByNamotionReflection()
    {
        // Arrange - DataModel with XML documentation comments
        var dataModel = new DataModel
        {
            Id = "namotion-test",
            DisplayName = "Namotion Test",
            TypeSource = @"
/// <summary>
/// A type with XML documentation for Namotion.Reflection testing.
/// </summary>
public record NamotionTestType
{
    /// <summary>
    /// The name property with documentation.
    /// </summary>
    public string Name { get; init; } = string.Empty;
}"
        };

        var nodeTypeService = new TestNodeTypeService();
        nodeTypeService.AddDataModel("namotion-test", dataModel);

        var service = CreateService(nodeTypeService);

        var node = new MeshNode("test/namotion/item")
        {
            Name = "Namotion Test Item",
            NodeType = "namotion-test",
            LastModified = DateTimeOffset.UtcNow
        };

        // Act
        var assemblyPath = await service.GetAssemblyLocationAsync(node);

        // Assert
        assemblyPath.Should().NotBeNull();
        var assembly = Assembly.LoadFrom(assemblyPath!);

        var namotionTestType = assembly.GetType("MeshWeaver.Graph.Dynamic.NamotionTestType");
        namotionTestType.Should().NotBeNull();

        // Get XML documentation using Namotion.Reflection
        var typeSummary = namotionTestType!.GetXmlDocsSummary();
        typeSummary.Should().Contain("A type with XML documentation");

        var nameProperty = namotionTestType.GetProperty("Name");
        nameProperty.Should().NotBeNull();
        var propertySummary = nameProperty!.GetXmlDocsSummary();
        propertySummary.Should().Contain("The name property with documentation");
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

    public Task<TypeNodePartition?> GetTypeNodePartitionAsync(string nodeType, string contextPath, CancellationToken ct = default)
    {
        // Get DataModel if exists
        var dataModels = new List<DataModel>();
        if (_dataModels.TryGetValue(nodeType, out var dataModel))
        {
            dataModels.Add(dataModel);
        }

        // Get LayoutAreas if exists
        var layoutAreas = _layoutAreas.TryGetValue(nodeType, out var areas)
            ? areas
            : new List<LayoutAreaConfig>();

        // Get HubFeatures if exists
        _hubFeatures.TryGetValue(nodeType, out var hubFeatures);

        // Return null if nothing found
        if (dataModels.Count == 0 && layoutAreas.Count == 0 && hubFeatures == null)
            return Task.FromResult<TypeNodePartition?>(null);

        return Task.FromResult<TypeNodePartition?>(new TypeNodePartition
        {
            DataModels = dataModels,
            LayoutAreas = layoutAreas,
            HubFeatures = hubFeatures,
            NewestTimestamp = DateTimeOffset.UtcNow
        });
    }

    private static async IAsyncEnumerable<T> EmptyAsyncEnumerable<T>()
    {
        await Task.CompletedTask;
        yield break;
    }
}
