using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Domain;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Namotion.Reflection;
using NSubstitute;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Integration tests for MeshNodeCompilationService - the public API for compiling
/// MeshNode assemblies on demand.
/// </summary>
public class MeshNodeCompilationServiceTest : IDisposable
{
    private static readonly JsonSerializerOptions SetupJsonOptions = new JsonSerializerOptions();
    private readonly string _testCacheDir;
    private readonly IOptions<CompilationCacheOptions> _cacheOptions;
    private readonly ICompilationCacheService _cacheService;
    private readonly IMessageHub _mockHub;

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
        _mockHub = Substitute.For<IMessageHub>();
        _mockHub.JsonSerializerOptions.Returns(SetupJsonOptions);
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

    private MeshNodeCompilationService CreateService(InMemoryPersistenceService persistence)
    {
        IServiceCollection services = new ServiceCollection();
        // Register all persistence services via the public API with the test instance
        services.AddInMemoryPersistence(persistence);
        // Register the mock hub so scoped MeshQuery can resolve it
        services.AddScoped<IMessageHub>(_ => _mockHub);
        var sp = services.BuildServiceProvider();

        // Resolve IMeshService from a scope (IMeshService is registered as scoped)
        var scope = sp.CreateScope();
        var meshQuery = scope.ServiceProvider.GetRequiredService<IMeshService>();

        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IMeshService)).Returns(meshQuery);
        _mockHub.ServiceProvider.Returns(serviceProvider);
        return new(_cacheService, _cacheOptions, _mockHub, NullLogger<MeshNodeCompilationService>.Instance);
    }

    private async Task SetupNodeType(InMemoryPersistenceService persistence, string nodeType, NodeTypeDefinition definition, CodeConfiguration? codeFile = null, string? displayName = null)
    {
        var node = MeshNode.FromPath($"type/{nodeType}") with
        {
            Name = displayName ?? nodeType,
            NodeType = MeshNode.NodeTypePath,
            Content = definition
        };
        await persistence.SaveNodeAsync(node, SetupJsonOptions, TestContext.Current.CancellationToken);

        if (codeFile != null)
        {
            // Code is stored as a child MeshNode under the Code path
            var codeNode = new MeshNode(codeFile.Id ?? "code", $"type/{nodeType}/_Source")
            {
                NodeType = "Code",
                Name = codeFile.DisplayName ?? codeFile.Id ?? "Code",
                Content = codeFile
            };
            await persistence.SaveNodeAsync(codeNode, SetupJsonOptions, TestContext.Current.CancellationToken);
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

        var nodeTypeDefinition = new NodeTypeDefinition { };

        await SetupNodeType(persistence, "story", nodeTypeDefinition, codeConfig, "Story");

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

        var nodeTypeDefinition = new NodeTypeDefinition { };

        await SetupNodeType(persistence, "project", nodeTypeDefinition, codeConfig, "Project");

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

        var nodeTypeDefinition = new NodeTypeDefinition { };

        await SetupNodeType(persistence, "cached-item", nodeTypeDefinition, codeConfig, "Cached Item");

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

        var nodeTypeDefinition = new NodeTypeDefinition { };

        await SetupNodeType(persistence, "debug-item", nodeTypeDefinition, codeConfig, "Debug Item");

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
    public async Task GetAssemblyLocationAsync_AssemblyContainsMeshNodeProviderAttribute()
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

        var nodeTypeDefinition = new NodeTypeDefinition { };

        await SetupNodeType(persistence, "widget", nodeTypeDefinition, codeConfig, "Widget");

        var service = CreateService(persistence);

        var node = MeshNode.FromPath("org/widgets/my-widget") with
        {
            Name = "My Widget",
            NodeType = "type/widget",
            LastModified = DateTimeOffset.UtcNow
        };

        // Act
        var assemblyPath = await service.GetAssemblyLocationAsync(node, TestContext.Current.CancellationToken);

        // Assert - Find MeshNodeProviderAttribute in assembly
        assemblyPath.Should().NotBeNull();
        var assembly = Assembly.LoadFrom(assemblyPath!);

        var meshNodeAttributes = assembly.GetCustomAttributes()
            .Where(a => a.GetType().Name.EndsWith("MeshNodeProviderAttribute"))
            .ToList();

        meshNodeAttributes.Should().NotBeEmpty("Assembly should contain MeshNodeProviderAttribute");
    }

    [Fact(Timeout = 25000)]
    public async Task GetAssemblyLocationAsync_MeshNodeProviderAttribute_ReturnsNodes()
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

        var nodeTypeDefinition = new NodeTypeDefinition { };

        await SetupNodeType(persistence, "component", nodeTypeDefinition, codeConfig, "Component");

        var service = CreateService(persistence);

        var node = MeshNode.FromPath("app/components/header") with
        {
            Name = "Header Component",
            NodeType = "type/component",
            Icon = "Header",
            Order = 1,
            LastModified = DateTimeOffset.UtcNow
        };

        // Act
        var assemblyPath = await service.GetAssemblyLocationAsync(node, TestContext.Current.CancellationToken);

        // Assert - Get nodes from MeshNodeProviderAttribute
        assemblyPath.Should().NotBeNull();
        var assembly = Assembly.LoadFrom(assemblyPath!);

        var meshNodeAttribute = assembly.GetCustomAttributes()
            .FirstOrDefault(a => a.GetType().Name.EndsWith("MeshNodeProviderAttribute"));

        meshNodeAttribute.Should().NotBeNull("Assembly should have MeshNodeProviderAttribute");

        // Get Nodes property
        var nodesProperty = meshNodeAttribute!.GetType().GetProperty("Nodes");
        nodesProperty.Should().NotBeNull("MeshNodeProviderAttribute should have Nodes property");

        var nodes = nodesProperty!.GetValue(meshNodeAttribute) as IEnumerable<MeshNode>;
        nodes.Should().NotBeNull();

        var nodesList = nodes!.ToList();
        nodesList.Should().HaveCount(1);

        var loadedNode = nodesList[0];
        loadedNode.Path.Should().Be("app/components/header");
        loadedNode.Name.Should().Be("Header Component");
        loadedNode.NodeType.Should().Be("type/component");
        loadedNode.Icon.Should().Be("Header");
        loadedNode.Order.Should().Be(1);
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

        var nodeTypeDefinition = new NodeTypeDefinition { };

        await SetupNodeType(persistence, "record", nodeTypeDefinition, codeConfig, "Record");

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

    [Fact(Timeout = 25000)]
    public async Task GetAssemblyLocationAsync_OrganizationFromCosmos_Compiles()
    {
        var persistence = new InMemoryPersistenceService();

        // Store the NodeType definition (mirrors Organization.json)
        var orgDefinition = new NodeTypeDefinition
        {
            Description = "An organization containing projects",
            Configuration = "config => config.WithContentType<Organization>()"
        };
        await SetupNodeType(persistence, "Organization", orgDefinition, displayName: "Organization");

        // Store Code as child MeshNode (NOT partition object)
        var codeNode = new MeshNode("Organization", "type/Organization/_Source")
        {
            NodeType = "Code",
            Name = "Organization Data Model",
            Content = new CodeConfiguration
            {
                Id = "Organization",
                Code = @"
public record Organization
{
    [System.ComponentModel.DataAnnotations.Key]
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
}",
                DisplayName = "Organization Data Model"
            }
        };
        await persistence.SaveNodeAsync(codeNode, SetupJsonOptions, TestContext.Current.CancellationToken);

        var service = CreateService(persistence);
        var node = MeshNode.FromPath("acme") with
        {
            Name = "ACME",
            NodeType = "type/Organization",
            LastModified = DateTimeOffset.UtcNow
        };

        var assemblyPath = await service.GetAssemblyLocationAsync(node, TestContext.Current.CancellationToken);
        assemblyPath.Should().NotBeNull();
        var assembly = Assembly.LoadFrom(assemblyPath!);
        assembly.GetType("Organization").Should().NotBeNull();
    }

    [Fact(Timeout = 25000)]
    public async Task GetAssemblyLocationAsync_MultipleCodeFiles_CombinesAndCompiles()
    {
        // Simulates Cosmos scenario: NodeType with multiple Code child MeshNodes
        var persistence = new InMemoryPersistenceService();

        var definition = new NodeTypeDefinition
        {
            Configuration = "config => config.WithContentType<Project>()"
        };
        await SetupNodeType(persistence, "Project", definition, displayName: "Project");

        // First code file: data model
        var dataModelNode = new MeshNode("ProjectDataModel", "type/Project/_Source")
        {
            NodeType = "Code",
            Name = "Project Data Model",
            Content = new CodeConfiguration
            {
                Id = "ProjectDataModel",
                Code = @"
public record Project
{
    [System.ComponentModel.DataAnnotations.Key]
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public ProjectStatus Status { get; init; }
}"
            }
        };
        await persistence.SaveNodeAsync(dataModelNode, SetupJsonOptions, TestContext.Current.CancellationToken);

        // Second code file: enum
        var enumNode = new MeshNode("ProjectStatus", "type/Project/_Source")
        {
            NodeType = "Code",
            Name = "Project Status Enum",
            Content = new CodeConfiguration
            {
                Id = "ProjectStatus",
                Code = @"
public enum ProjectStatus
{
    Draft,
    Active,
    Completed,
    Archived
}"
            }
        };
        await persistence.SaveNodeAsync(enumNode, SetupJsonOptions, TestContext.Current.CancellationToken);

        var service = CreateService(persistence);
        var node = MeshNode.FromPath("acme/web") with
        {
            Name = "Web Project",
            NodeType = "type/Project",
            LastModified = DateTimeOffset.UtcNow
        };

        var assemblyPath = await service.GetAssemblyLocationAsync(node, TestContext.Current.CancellationToken);
        assemblyPath.Should().NotBeNull();
        var assembly = Assembly.LoadFrom(assemblyPath!);
        assembly.GetType("Project").Should().NotBeNull("Data model type should be compiled");
        assembly.GetType("ProjectStatus").Should().NotBeNull("Enum type from second code file should be compiled");
    }

    [Fact(Timeout = 25000)]
    public async Task GetAssemblyLocationAsync_NodeTypeDefinitionNode_CompilesOwnCode()
    {
        // Simulates Cosmos: navigating to the NodeType node itself (e.g., type/Organization)
        // The NodeType node compiles its OWN Code children (at its own path)
        var persistence = new InMemoryPersistenceService();

        var definition = new NodeTypeDefinition
        {
            Configuration = "config => config.WithContentType<TaskItem>()"
        };
        await SetupNodeType(persistence, "Task", definition, new CodeConfiguration
        {
            Id = "TaskItem",
            Code = @"
public record TaskItem
{
    [System.ComponentModel.DataAnnotations.Key]
    public string Title { get; init; } = string.Empty;
    public bool Done { get; init; }
}"
        });

        var service = CreateService(persistence);

        // The node IS the NodeType definition itself
        var nodeTypeNode = MeshNode.FromPath("type/Task") with
        {
            Name = "Task",
            NodeType = MeshNode.NodeTypePath,
            Content = definition,
            LastModified = DateTimeOffset.UtcNow
        };

        var assemblyPath = await service.GetAssemblyLocationAsync(nodeTypeNode, TestContext.Current.CancellationToken);
        assemblyPath.Should().NotBeNull();
        var assembly = Assembly.LoadFrom(assemblyPath!);
        assembly.GetType("TaskItem").Should().NotBeNull();
    }

    [Fact(Timeout = 25000)]
    public async Task GetAssemblyLocationAsync_NoCodeChildren_CompilesWithoutUserCode()
    {
        // Simulates Cosmos: NodeType exists but has no Code children yet
        var persistence = new InMemoryPersistenceService();

        var definition = new NodeTypeDefinition { };
        await SetupNodeType(persistence, "EmptyType", definition, displayName: "Empty Type");

        var service = CreateService(persistence);
        var node = MeshNode.FromPath("test/empty") with
        {
            Name = "Empty",
            NodeType = "type/EmptyType",
            LastModified = DateTimeOffset.UtcNow
        };

        // Should still compile (generates MeshNodeProviderAttribute without user types)
        var assemblyPath = await service.GetAssemblyLocationAsync(node, TestContext.Current.CancellationToken);
        assemblyPath.Should().NotBeNull();
    }

    [Fact(Timeout = 25000)]
    public async Task GetAssemblyLocationAsync_ConfigurationWithContentType_GeneratesHubConfig()
    {
        // Simulates Cosmos: NodeType with Configuration that references compiled type
        var persistence = new InMemoryPersistenceService();

        var definition = new NodeTypeDefinition
        {
            Description = "A business contact",
            Configuration = "config => config.WithContentType<Contact>()"
        };
        await SetupNodeType(persistence, "Contact", definition, new CodeConfiguration
        {
            Id = "Contact",
            Code = @"
public record Contact
{
    [System.ComponentModel.DataAnnotations.Key]
    public string Email { get; init; } = string.Empty;
    public string FirstName { get; init; } = string.Empty;
    public string LastName { get; init; } = string.Empty;
}"
        });

        var service = CreateService(persistence);
        var node = MeshNode.FromPath("contacts/john") with
        {
            Name = "John Doe",
            NodeType = "type/Contact",
            LastModified = DateTimeOffset.UtcNow
        };

        var result = await service.CompileAndGetConfigurationsAsync(node, TestContext.Current.CancellationToken);
        result.Should().NotBeNull();
        result!.AssemblyLocation.Should().NotBeNullOrEmpty();
        result.NodeTypeConfigurations.Should().NotBeEmpty("Should extract HubConfiguration from compiled assembly");
        result.NodeTypeConfigurations.First().HubConfiguration.Should().NotBeNull();
    }
}
