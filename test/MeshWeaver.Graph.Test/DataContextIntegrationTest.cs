using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Test data model that is pre-compiled (not using runtime compilation).
/// Used for testing DataContext integration without the complexity of runtime type compilation.
/// </summary>
public record TestStory
{
    [Key]
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }
    public int Points { get; init; }
}

/// <summary>
/// Integration tests for DataContext initialization and data availability.
/// Tests verify that configuration is loaded correctly and MeshNode.Content is persisted properly.
/// Note: Tests only target the "graph" hub since child hub routing (e.g., graph/story1)
/// requires NodeTypeConfiguration registration which needs type compilation.
/// </summary>
[Collection("DataContextIntegrationTests")]
public class DataContextIntegrationTest : MonolithMeshTestBase
{
    private static readonly string TestDirectoryBase = Path.Combine(Path.GetTempPath(), "MeshWeaverDataContextTests");

    [ThreadStatic]
    private static string? _currentTestDirectory;

    private IPersistenceService Persistence => ServiceProvider.GetRequiredService<IPersistenceService>();

    private static string GetOrCreateTestDirectory()
    {
        if (_currentTestDirectory == null)
        {
            _currentTestDirectory = Path.Combine(TestDirectoryBase, Guid.NewGuid().ToString());
            Directory.CreateDirectory(_currentTestDirectory);
        }
        return _currentTestDirectory;
    }

    public DataContextIntegrationTest(ITestOutputHelper output) : base(output)
    {
    }

    private static void SetupTestConfiguration(string testDataDirectory)
    {
        // Create _config directories
        var configDir = Path.Combine(testDataDirectory, "_config");
        Directory.CreateDirectory(Path.Combine(configDir, "dataModels"));
        Directory.CreateDirectory(Path.Combine(configDir, "nodeTypes"));
        Directory.CreateDirectory(Path.Combine(configDir, "hubFeatures"));
        Directory.CreateDirectory(Path.Combine(configDir, "contentCollections"));
        Directory.CreateDirectory(Path.Combine(configDir, "layoutAreas"));

        // Create a simple Story data model (we'll use TestStory which is pre-compiled)
        var storyDataModel = new DataModel
        {
            Id = "story",
            DisplayName = "Story",
            IconName = "Document",
            Description = "A user story or task",
            DisplayOrder = 30,
            TypeSource = @"
public record Story
{
    [Key]
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }
    public int Points { get; init; }
}"
        };

        // Create Graph data model
        var graphDataModel = new DataModel
        {
            Id = "graph",
            DisplayName = "Graph",
            IconName = "Diagram",
            Description = "The graph root",
            DisplayOrder = 0,
            TypeSource = @"
public record Graph
{
    [Key]
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
}"
        };

        // Write data models
        WriteJsonConfig(configDir, "dataModels", "story.json", storyDataModel);
        WriteJsonConfig(configDir, "dataModels", "graph.json", graphDataModel);

        // Create node types
        var storyNodeType = new NodeTypeConfig { NodeType = "story", DataModelId = "story", DisplayName = "Story" };
        var graphNodeType = new NodeTypeConfig { NodeType = "graph", DataModelId = "graph", DisplayName = "Graph" };

        WriteJsonConfig(configDir, "nodeTypes", "story.json", storyNodeType);
        WriteJsonConfig(configDir, "nodeTypes", "graph.json", graphNodeType);

        // Create hub feature
        var hubFeature = new HubFeatureConfig
        {
            Id = "graph",
            EnableMeshNavigation = true,
            EnableDynamicNodeTypeAreas = true
        };
        WriteJsonConfig(configDir, "hubFeatures", "graph.json", hubFeature);

        // Create layout area config
        var layoutArea = new LayoutAreaConfig
        {
            Id = "Details",
            Area = "Details",
            Title = "Details View",
            Group = "Main",
            Order = 1
        };
        WriteJsonConfig(configDir, "layoutAreas", "details.json", layoutArea);
    }

    private static void WriteJsonConfig<T>(string configDir, string subDir, string fileName, T config)
    {
        var filePath = Path.Combine(configDir, subDir, fileName);
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });
        File.WriteAllText(filePath, json);
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var testDataDirectory = GetOrCreateTestDirectory();
        SetupTestConfiguration(testDataDirectory);

        // Create in-memory persistence and pre-seed with test data including Content
        var persistence = new InMemoryPersistenceService();

        // Pre-seed the hierarchy with Content
        persistence.SaveNodeAsync(new MeshNode("graph")
        {
            Name = "Graph",
            NodeType = "graph"
        }).GetAwaiter().GetResult();

        // Pre-seed story nodes WITH Content containing TestStory data
        persistence.SaveNodeAsync(new MeshNode("graph/story1")
        {
            Name = "Story 1",
            NodeType = "story",
            Content = new TestStory
            {
                Id = "story1",
                Title = "First Story",
                Description = "This is the first story",
                Points = 5
            }
        }).GetAwaiter().GetResult();

        persistence.SaveNodeAsync(new MeshNode("graph/story2")
        {
            Name = "Story 2",
            NodeType = "story",
            Content = new TestStory
            {
                Id = "story2",
                Title = "Second Story",
                Description = "This is the second story",
                Points = 8
            }
        }).GetAwaiter().GetResult();

        // Build configuration
        var configBuilder = new ConfigurationBuilder();
        configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Graph:DataDirectory"] = testDataDirectory
        });
        var configuration = configBuilder.Build();

        return builder
            .UseMonolithMesh()
            .ConfigureServices(services => services.AddPersistence(persistence))
            .AddJsonGraphConfiguration(testDataDirectory, configuration);
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration).AddLayoutClient();
    }

    public override async ValueTask DisposeAsync()
    {
        var dir = _currentTestDirectory;
        _currentTestDirectory = null;

        await base.DisposeAsync();

        // Clean up test directory
        if (dir != null && Directory.Exists(dir))
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    #region DataContext Tests - Configuration and Persistence

    [Fact(Timeout = 90000)]
    public async Task GraphHub_InitializesWithConfiguration()
    {
        var client = GetClient();
        var graphAddress = new Address("graph");

        // Initialize graph hub via ping
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(graphAddress),
            TestContext.Current.CancellationToken);

        // Verify graph node exists in persistence
        var graphNode = await Persistence.GetNodeAsync("graph", TestContext.Current.CancellationToken);
        graphNode.Should().NotBeNull("Graph node should exist in persistence");
        graphNode!.Name.Should().Be("Graph");
        graphNode.NodeType.Should().Be("graph");
    }

    [Fact(Timeout = 90000)]
    public async Task Persistence_StoryContentIsPreserved()
    {
        // This test verifies that MeshNode.Content is correctly persisted
        // and can be loaded back from the persistence service

        // Get story node from persistence (directly, without routing)
        var storyNode = await Persistence.GetNodeAsync("graph/story1", TestContext.Current.CancellationToken);

        // Assert - content should be available
        storyNode.Should().NotBeNull("Story node should exist in persistence");
        storyNode!.Content.Should().NotBeNull("Story node should have Content");
        storyNode.Content.Should().BeOfType<TestStory>("Content should be TestStory");

        var story = (TestStory)storyNode.Content!;
        story.Id.Should().Be("story1");
        story.Title.Should().Be("First Story");
        story.Description.Should().Be("This is the first story");
        story.Points.Should().Be(5);
    }

    [Fact(Timeout = 90000)]
    public async Task MeshNode_ChildrenAvailable_ViaPersistence()
    {
        var client = GetClient();
        var graphAddress = new Address("graph");

        // Initialize graph hub
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(graphAddress),
            TestContext.Current.CancellationToken);

        // Act - get children from persistence
        var children = await Persistence.GetChildrenAsync("graph")
            .ToListAsync(TestContext.Current.CancellationToken);

        // Assert - children should be available
        children.Should().NotBeNull("Children should be available in Persistence");
        children.Should().HaveCountGreaterThanOrEqualTo(2, "Should have story1 and story2 as children");
        children.Should().Contain(n => n.Prefix == "graph/story1");
        children.Should().Contain(n => n.Prefix == "graph/story2");

        // Verify content is preserved in children
        var story1 = children.FirstOrDefault(n => n.Prefix == "graph/story1");
        story1.Should().NotBeNull();
        story1!.Content.Should().NotBeNull();
        story1.Content.Should().BeOfType<TestStory>();
    }

    [Fact(Timeout = 90000)]
    public async Task Persistence_CanCreateNodeWithContent()
    {
        // This test verifies that nodes with Content can be created via IPersistenceService

        // Act - create new node directly via persistence
        var newStory = new MeshNode("graph/story3")
        {
            Name = "Story 3",
            NodeType = "story",
            Content = new TestStory
            {
                Id = "story3",
                Title = "Third Story",
                Description = "A new story created via persistence",
                Points = 21
            }
        };
        await Persistence.SaveNodeAsync(newStory, TestContext.Current.CancellationToken);

        // Assert - verify the node with content is persisted
        var persistedNode = await Persistence.GetNodeAsync("graph/story3", TestContext.Current.CancellationToken);
        persistedNode.Should().NotBeNull("New story should be persisted");
        persistedNode!.Name.Should().Be("Story 3");
        persistedNode.NodeType.Should().Be("story");
        persistedNode.Content.Should().NotBeNull("Content should be persisted");
        persistedNode.Content.Should().BeOfType<TestStory>();

        var content = (TestStory)persistedNode.Content!;
        content.Id.Should().Be("story3");
        content.Title.Should().Be("Third Story");
        content.Points.Should().Be(21);
    }

    [Fact(Timeout = 90000)]
    public async Task Persistence_CanUpdateNodeWithContent()
    {
        // This test verifies that nodes with Content can be updated via IPersistenceService

        // Verify initial data exists
        var initialNode = await Persistence.GetNodeAsync("graph/story1", TestContext.Current.CancellationToken);
        initialNode.Should().NotBeNull();
        var initialContent = initialNode!.Content as TestStory;
        initialContent!.Points.Should().Be(5);

        // Act - update node directly via persistence
        var updatedNode = initialNode with
        {
            Content = new TestStory
            {
                Id = "story1",
                Title = "Updated Story",
                Description = "This story has been updated",
                Points = 13
            }
        };
        await Persistence.SaveNodeAsync(updatedNode, TestContext.Current.CancellationToken);

        // Assert - get updated data from persistence
        var persistedNode = await Persistence.GetNodeAsync("graph/story1", TestContext.Current.CancellationToken);
        persistedNode.Should().NotBeNull();
        var content = persistedNode!.Content as TestStory;
        content.Should().NotBeNull();
        content!.Title.Should().Be("Updated Story");
        content.Points.Should().Be(13);
    }

    #endregion
}

/// <summary>
/// Collection definition for DataContextIntegrationTests.
/// Ensures tests in this collection run serially to avoid test isolation issues.
/// </summary>
[CollectionDefinition("DataContextIntegrationTests", DisableParallelization = true)]
public class DataContextIntegrationTestsCollection
{
}
