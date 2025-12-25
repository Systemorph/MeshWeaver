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
    private readonly IOptions<CompilationCacheOptions> _cacheOptions;
    private readonly ICompilationCacheService _cacheService;

    public MeshNodeCompilationServiceTest()
    {
        _testCacheDir = Path.Combine(Path.GetTempPath(), $"meshnode-compile-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testCacheDir);

        _cacheOptions = Options.Create(new CompilationCacheOptions
        {
            CacheDirectory = _testCacheDir,
            EnableCompilationCache = true,
            EnableSourceDebugging = true
        });

        _cacheService = new CompilationCacheService(_cacheOptions, NullLogger<CompilationCacheService>.Instance);
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
        new(nodeTypeService, _cacheService, _cacheOptions, NullLogger<MeshNodeCompilationService>.Instance);

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

    [Fact(Timeout = 20000)]
    public async Task GetAssemblyLocationAsync_ReturnsPathToCompiledDll()
    {
        // Arrange
        var codeConfig = new CodeConfiguration
        {
            Code = @"
public record StoryType
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
}"
        };

        var nodeTypeDefinition = new NodeTypeDefinition
        {
            Id = "story",
            DisplayName = "Story"
        };

        var nodeTypeService = new TestNodeTypeService();
        nodeTypeService.AddNodeType("story", nodeTypeDefinition, codeConfig);

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
        var codeConfig = new CodeConfiguration
        {
            Code = @"
public record ProjectType
{
    public string Name { get; init; } = string.Empty;
}"
        };

        var nodeTypeDefinition = new NodeTypeDefinition
        {
            Id = "project",
            DisplayName = "Project"
        };

        var nodeTypeService = new TestNodeTypeService();
        nodeTypeService.AddNodeType("project", nodeTypeDefinition, codeConfig);

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
        var codeConfig = new CodeConfiguration
        {
            Code = @"
public record CachedItemType
{
    public string Value { get; init; } = string.Empty;
}"
        };

        var nodeTypeDefinition = new NodeTypeDefinition
        {
            Id = "cached-item",
            DisplayName = "Cached Item"
        };

        var nodeTypeService = new TestNodeTypeService();
        nodeTypeService.AddNodeType("cached-item", nodeTypeDefinition, codeConfig);

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
        var codeConfig = new CodeConfiguration
        {
            Code = @"
public record DebugItemType
{
    public string Data { get; init; } = string.Empty;
}"
        };

        var nodeTypeDefinition = new NodeTypeDefinition
        {
            Id = "debug-item",
            DisplayName = "Debug Item"
        };

        var nodeTypeService = new TestNodeTypeService();
        nodeTypeService.AddNodeType("debug-item", nodeTypeDefinition, codeConfig);

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
        var codeConfig = new CodeConfiguration
        {
            Code = @"
public record WidgetType
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
}"
        };

        var nodeTypeDefinition = new NodeTypeDefinition
        {
            Id = "widget",
            DisplayName = "Widget"
        };

        var nodeTypeService = new TestNodeTypeService();
        nodeTypeService.AddNodeType("widget", nodeTypeDefinition, codeConfig);

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
        var codeConfig = new CodeConfiguration
        {
            Code = @"
public record ComponentType
{
    public string Id { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
}"
        };

        var nodeTypeDefinition = new NodeTypeDefinition
        {
            Id = "component",
            DisplayName = "Component"
        };

        var nodeTypeService = new TestNodeTypeService();
        nodeTypeService.AddNodeType("component", nodeTypeDefinition, codeConfig);

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
        var codeConfig = new CodeConfiguration
        {
            Code = @"
public record RecordType
{
    public string Id { get; init; } = ""default-id"";
    public string Title { get; init; } = ""Default Title"";
    public int Count { get; init; } = 42;
}"
        };

        var nodeTypeDefinition = new NodeTypeDefinition
        {
            Id = "record",
            DisplayName = "Record"
        };

        var nodeTypeService = new TestNodeTypeService();
        nodeTypeService.AddNodeType("record", nodeTypeDefinition, codeConfig);

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
}

/// <summary>
/// Test implementation of INodeTypeService for unit testing.
/// </summary>
internal class TestNodeTypeService : INodeTypeService
{
    private readonly Dictionary<string, (NodeTypeDefinition Definition, CodeConfiguration? Code)> _nodeTypes = new();

    public void AddNodeType(string nodeType, NodeTypeDefinition definition, CodeConfiguration? codeConfig = null)
    {
        _nodeTypes[nodeType] = (definition, codeConfig);
    }

    public IAsyncEnumerable<MeshNode> GetNodeTypeNodesAsync(string contextPath) =>
        EmptyAsyncEnumerable<MeshNode>();

    public Task<MeshNode?> GetNodeTypeNodeAsync(string nodeType, string contextPath, CancellationToken ct = default)
    {
        if (_nodeTypes.TryGetValue(nodeType, out var entry))
        {
            var node = new MeshNode($"type/{nodeType}")
            {
                Name = entry.Definition.DisplayName ?? entry.Definition.Id,
                NodeType = "NodeType",
                Content = entry.Definition
            };
            return Task.FromResult<MeshNode?>(node);
        }
        return Task.FromResult<MeshNode?>(null);
    }

    public Task<CodeConfiguration?> GetCodeConfigurationAsync(string nodeType, string contextPath, CancellationToken ct = default)
    {
        if (_nodeTypes.TryGetValue(nodeType, out var entry))
        {
            return Task.FromResult(entry.Code);
        }
        return Task.FromResult<CodeConfiguration?>(null);
    }

    public Task SaveCodeConfigurationAsync(string nodeTypePath, CodeConfiguration config, CancellationToken ct = default) =>
        Task.CompletedTask;

    public IAsyncEnumerable<MeshNode> GetAllNodeTypeNodesAsync() =>
        EmptyAsyncEnumerable<MeshNode>();

    public Task<IReadOnlyList<CodeConfiguration>> GetAllCodeConfigurationsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CodeConfiguration>>(
            _nodeTypes.Values.Where(x => x.Code != null).Select(x => x.Code!).ToList());

    private static async IAsyncEnumerable<T> EmptyAsyncEnumerable<T>()
    {
        await Task.CompletedTask;
        yield break;
    }
}
