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
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
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

    private MeshNodeCompilationService CreateService(IPersistenceService persistence) =>
        new(persistence, _cacheService, _cacheOptions, NullLogger<MeshNodeCompilationService>.Instance);

    private async Task SetupNodeType(InMemoryPersistenceService persistence, string nodeType, NodeTypeDefinition definition, CodeConfiguration? codeFile = null)
    {
        var node = MeshNode.FromPath($"type/{nodeType}") with
        {
            Name = definition.DisplayName ?? definition.Id,
            NodeType = MeshNode.NodeTypePath,
            Content = definition
        };
        await persistence.SaveNodeAsync(node);

        if (codeFile != null)
        {
            // CodeConfiguration is stored in the "Code" sub-partition (e.g., "type/project/Code")
            await persistence.SavePartitionObjectsAsync($"type/{nodeType}", "Code", [codeFile]);
        }
    }

    [Fact(Timeout = 15000)]
    public async Task GetAssemblyLocationAsync_ReturnsNull_WhenNodeTypeIsNull()
    {
        // Arrange
        var persistence = new InMemoryPersistenceService();
        var service = CreateService(persistence);

        var node = MeshNode.FromPath("test/no-type") with
        {
            Name = "No Type Node",
            NodeType = null,
            LastModified = DateTimeOffset.UtcNow
        };

        // Act
        var result = await service.GetAssemblyLocationAsync(node, TestContext.Current.CancellationToken);

        // Assert
        result.Should().BeNull("Node with no NodeType should not compile");
    }

    [Fact(Timeout = 20000)]
    public async Task GetAssemblyLocationAsync_ReturnsPathToCompiledDll()
    {
        // Arrange
        var persistence = new InMemoryPersistenceService();
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
            Id = "story", Namespace = "Type",
            DisplayName = "Story"
        };

        await SetupNodeType(persistence, "story", nodeTypeDefinition, codeConfig);

        var service = CreateService(persistence);

        var node = MeshNode.FromPath("org/stories/my-story") with
        {
            Name = "My Story",
            NodeType = "type/story",
            LastModified = DateTimeOffset.UtcNow
        };

        // Act
        var assemblyPath = await service.GetAssemblyLocationAsync(node, TestContext.Current.CancellationToken);

        // Assert
        assemblyPath.Should().NotBeNull("Assembly should be compiled");
        File.Exists(assemblyPath).Should().BeTrue("DLL file should exist at returned path");
        assemblyPath.Should().EndWith(".dll");
    }

    [Fact(Timeout = 20000)]
    public async Task GetAssemblyLocationAsync_AssemblyContainsCompiledType()
    {
        // Arrange
        var persistence = new InMemoryPersistenceService();
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
            Id = "project", Namespace = "Type",
            DisplayName = "Project"
        };

        await SetupNodeType(persistence, "project", nodeTypeDefinition, codeConfig);

        var service = CreateService(persistence);

        var node = MeshNode.FromPath("org/projects/my-project") with
        {
            Name = "My Project",
            NodeType = "type/project",
            LastModified = DateTimeOffset.UtcNow
        };

        // Act
        var assemblyPath = await service.GetAssemblyLocationAsync(node, TestContext.Current.CancellationToken);

        // Assert - user code is in global namespace (no namespace wrapper)
        assemblyPath.Should().NotBeNull();
        var assembly = Assembly.LoadFrom(assemblyPath!);
        assembly.GetType("ProjectType").Should().NotBeNull();
    }

    [Fact(Timeout = 20000)]
    public async Task GetAssemblyLocationAsync_UsesCachedAssembly()
    {
        // Arrange
        var persistence = new InMemoryPersistenceService();
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
            Id = "cached-item", Namespace = "Type",
            DisplayName = "Cached Item"
        };

        await SetupNodeType(persistence, "cached-item", nodeTypeDefinition, codeConfig);

        var service = CreateService(persistence);

        var node = MeshNode.FromPath("org/items/cached") with
        {
            Name = "Cached Item",
            NodeType = "type/cached-item",
            LastModified = DateTimeOffset.UtcNow.AddMinutes(-5) // Old modification time
        };

        // First call compiles
        var firstPath = await service.GetAssemblyLocationAsync(node, TestContext.Current.CancellationToken);
        firstPath.Should().NotBeNull();

        var firstWriteTime = File.GetLastWriteTimeUtc(firstPath!);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Act - Second call should use cache
        var secondPath = await service.GetAssemblyLocationAsync(node, TestContext.Current.CancellationToken);

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
        var persistence = new InMemoryPersistenceService();
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
            Id = "debug-item", Namespace = "Type",
            DisplayName = "Debug Item"
        };

        await SetupNodeType(persistence, "debug-item", nodeTypeDefinition, codeConfig);

        var service = CreateService(persistence);

        var node = MeshNode.FromPath("debug/items/test") with
        {
            Name = "Debug Item",
            NodeType = "type/debug-item",
            LastModified = DateTimeOffset.UtcNow
        };

        // Act
        var assemblyPath = await service.GetAssemblyLocationAsync(node, TestContext.Current.CancellationToken);

        // Assert - Source file should exist
        assemblyPath.Should().NotBeNull();
        var sourcePath = Path.ChangeExtension(assemblyPath!, ".cs");
        File.Exists(sourcePath).Should().BeTrue("Source file should be generated for debugging");
    }

    [Fact(Timeout = 25000)]
    public async Task GetAssemblyLocationAsync_AssemblyContainsMeshNodeAttribute()
    {
        // Arrange
        var persistence = new InMemoryPersistenceService();
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
            Id = "widget", Namespace = "Type",
            DisplayName = "Widget"
        };

        await SetupNodeType(persistence, "widget", nodeTypeDefinition, codeConfig);

        var service = CreateService(persistence);

        var node = MeshNode.FromPath("org/widgets/my-widget") with
        {
            Name = "My Widget",
            NodeType = "type/widget",
            Description = "A test widget",
            LastModified = DateTimeOffset.UtcNow
        };

        // Act
        var assemblyPath = await service.GetAssemblyLocationAsync(node, TestContext.Current.CancellationToken);

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
        var persistence = new InMemoryPersistenceService();
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
            Id = "component", Namespace = "Type",
            DisplayName = "Component"
        };

        await SetupNodeType(persistence, "component", nodeTypeDefinition, codeConfig);

        var service = CreateService(persistence);

        var node = MeshNode.FromPath("app/components/header") with
        {
            Name = "Header Component",
            NodeType = "type/component",
            Description = "The main header",
            Icon = "Header",
            DisplayOrder = 1,
            LastModified = DateTimeOffset.UtcNow
        };

        // Act
        var assemblyPath = await service.GetAssemblyLocationAsync(node, TestContext.Current.CancellationToken);

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
        loadedNode.NodeType.Should().Be("type/component");
        loadedNode.Description.Should().Be("The main header");
        loadedNode.Icon.Should().Be("Header");
        loadedNode.DisplayOrder.Should().Be(1);
    }

    [Fact(Timeout = 25000)]
    public async Task GetAssemblyLocationAsync_CompiledDataType_CanBeInstantiated()
    {
        // Arrange
        var persistence = new InMemoryPersistenceService();
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
            Id = "record", Namespace = "Type",
            DisplayName = "Record"
        };

        await SetupNodeType(persistence, "record", nodeTypeDefinition, codeConfig);

        var service = CreateService(persistence);

        var node = MeshNode.FromPath("data/records/test") with
        {
            Name = "Test Record",
            NodeType = "type/record",
            LastModified = DateTimeOffset.UtcNow
        };

        // Act
        var assemblyPath = await service.GetAssemblyLocationAsync(node, TestContext.Current.CancellationToken);

        // Assert - Instantiate the compiled data type
        assemblyPath.Should().NotBeNull();
        var assembly = Assembly.LoadFrom(assemblyPath!);

        // User code is in global namespace (no namespace wrapper)
        var recordType = assembly.GetType("RecordType");
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
