using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
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
    private string? _testDirectory;

    private IPersistenceService Persistence => Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();

    private string GetOrCreateTestDirectory()
    {
        if (_testDirectory == null)
        {
            _testDirectory = Path.Combine(TestDirectoryBase, Guid.NewGuid().ToString());
            Directory.CreateDirectory(_testDirectory);
        }
        return _testDirectory;
    }

    public DataContextIntegrationTest(ITestOutputHelper output) : base(output)
    {
    }

    private static void SetupTestConfiguration(InMemoryPersistenceService persistence)
    {
        // Create Story type at type/story (NodeType = "NodeType")
        var storyCodeConfig = new CodeFile
        {
            Code = "public record Story { [Key] public string Id { get; init; } }"
        };

        var storyTypeNode = MeshNode.FromPath("type/story") with
        {
            Name = "Story",
            NodeType = "NodeType",
            Description = "A user story or task",
            IconName = "Document",
            DisplayOrder = 30,
            IsPersistent = true,
            Content = new NodeTypeDefinition
            {
                Id = "story",
                Namespace = "Type",
                DisplayName = "Story",
                IconName = "Document",
                Description = "A user story or task",
                DisplayOrder = 30
            }
        };
        persistence.SaveNodeAsync(storyTypeNode).GetAwaiter().GetResult();
        persistence.SavePartitionObjectsAsync("type/story", null, [storyCodeConfig]).GetAwaiter().GetResult();

        // Create Graph type at type/graph (NodeType = "NodeType")
        var graphCodeConfig = new CodeFile
        {
            Code = "public record GraphRoot { [Key] public string Id { get; init; } }"
        };

        var graphTypeNode = MeshNode.FromPath("type/graph") with
        {
            Name = "Graph",
            NodeType = "NodeType",
            Description = "The graph root",
            IconName = "Diagram",
            DisplayOrder = 0,
            IsPersistent = true,
            Content = new NodeTypeDefinition
            {
                Id = "graph",
                Namespace = "Type",
                DisplayName = "Graph",
                IconName = "Diagram",
                Description = "The graph root",
                DisplayOrder = 0
            }
        };
        persistence.SaveNodeAsync(graphTypeNode).GetAwaiter().GetResult();
        persistence.SavePartitionObjectsAsync("type/graph", null, [graphCodeConfig]).GetAwaiter().GetResult();
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var testDataDirectory = GetOrCreateTestDirectory();

        // Create in-memory persistence and pre-seed with test data including Content
        var persistence = new InMemoryPersistenceService();

        // Setup NodeType configurations using "type/" prefix
        SetupTestConfiguration(persistence);

        // Pre-seed the hierarchy with Content
        persistence.SaveNodeAsync(MeshNode.FromPath("graph") with
        {
            Name = "Graph",
            NodeType = "type/graph"
        }).GetAwaiter().GetResult();

        // Pre-seed story nodes WITH Content containing TestStory data
        persistence.SaveNodeAsync(MeshNode.FromPath("graph/story1") with
        {
            Name = "Story 1",
            NodeType = "type/story",
            Content = new TestStory
            {
                Id = "story1",
                Title = "First Story",
                Description = "This is the first story",
                Points = 5
            }
        }).GetAwaiter().GetResult();

        persistence.SaveNodeAsync(MeshNode.FromPath("graph/story2") with
        {
            Name = "Story 2",
            NodeType = "type/story",
            Content = new TestStory
            {
                Id = "story2",
                Title = "Second Story",
                Description = "This is the second story",
                Points = 8
            }
        }).GetAwaiter().GetResult();

        var cacheDirectory = Path.Combine(testDataDirectory, ".mesh-cache");

        return builder
            .UseMonolithMesh()
            .ConfigureServices(services => services.AddPersistence(persistence).Configure<CompilationCacheOptions>(o => o.CacheDirectory = cacheDirectory))
            .AddJsonGraphConfiguration(testDataDirectory);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();

        // Clean up test directory
        if (_testDirectory != null && Directory.Exists(_testDirectory))
        {
            try
            {
                Directory.Delete(_testDirectory, recursive: true);
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

        // Verify graph node exists in persistence with correct NodeType
        // (Name comes from persistence, NodeType references type/graph definition)
        var graphNode = await Persistence.GetNodeAsync("graph", TestContext.Current.CancellationToken);
        graphNode.Should().NotBeNull("Graph node should exist in persistence");
        graphNode!.NodeType.Should().Be("type/graph");
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
        children.Should().Contain(n => n.Path == "graph/story1");
        children.Should().Contain(n => n.Path == "graph/story2");

        // Verify content is preserved in children
        var story1 = children.FirstOrDefault(n => n.Path == "graph/story1");
        story1.Should().NotBeNull();
        story1!.Content.Should().NotBeNull();
        story1.Content.Should().BeOfType<TestStory>();
    }

    [Fact(Timeout = 90000)]
    public async Task Persistence_CanCreateNodeWithContent()
    {
        // This test verifies that nodes with Content can be created via IPersistenceService

        // Act - create new node directly via persistence
        var newStory = MeshNode.FromPath("graph/story3") with
        {
            Name = "Story 3",
            NodeType = "type/story",
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
        persistedNode.NodeType.Should().Be("type/story");
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
