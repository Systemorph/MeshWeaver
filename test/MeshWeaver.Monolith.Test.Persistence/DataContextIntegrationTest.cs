using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace MeshWeaver.Monolith.Test.Persistence;

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
    private InMemoryPersistenceService? _persistence;


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

    private static readonly JsonSerializerOptions SetupJsonOptions = new();

    private static void SetupTestConfiguration(InMemoryPersistenceService persistence)
    {
        // Create Story type at type/story (NodeType = "NodeType")
        var storyCodeConfig = new CodeConfiguration
        {
            Code = "public record Story { [Key] public string Id { get; init; } }"
        };

        var storyTypeNode = MeshNode.FromPath("type/story") with
        {
            Name = "Story",
            NodeType = "NodeType",
            Icon = "Document",
            Order = 30,
            Content = new NodeTypeDefinition
            {
                Description = "A user story or task"
            }
        };
        persistence.SaveNodeAsync(storyTypeNode, SetupJsonOptions).GetAwaiter().GetResult();
        persistence.SavePartitionObjectsAsync("type/story", null, [storyCodeConfig], SetupJsonOptions).GetAwaiter().GetResult();

        // Create Graph type at type/graph (NodeType = "NodeType")
        var graphCodeConfig = new CodeConfiguration
        {
            Code = "public record GraphRoot { [Key] public string Id { get; init; } }"
        };

        var graphTypeNode = MeshNode.FromPath("type/graph") with
        {
            Name = "Graph",
            NodeType = "NodeType",
            Icon = "Diagram",
            Order = 0,
            Content = new NodeTypeDefinition
            {
                Description = "The graph root"
            }
        };
        persistence.SaveNodeAsync(graphTypeNode, SetupJsonOptions).GetAwaiter().GetResult();
        persistence.SavePartitionObjectsAsync("type/graph", null, [graphCodeConfig], SetupJsonOptions).GetAwaiter().GetResult();
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var testDataDirectory = GetOrCreateTestDirectory();

        // Create in-memory persistence and pre-seed with test data including Content
        var persistence = _persistence = new InMemoryPersistenceService();

        // Setup NodeType configurations using "type/" prefix
        SetupTestConfiguration(persistence);

        // Pre-seed the hierarchy with Content
        persistence.SaveNodeAsync(MeshNode.FromPath("graph") with
        {
            Name = "Graph",
            NodeType = "type/graph"
        }, SetupJsonOptions).GetAwaiter().GetResult();

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
        }, SetupJsonOptions).GetAwaiter().GetResult();

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
        }, SetupJsonOptions).GetAwaiter().GetResult();

        var cacheDirectory = Path.Combine(testDataDirectory, ".mesh-cache");

        return builder
            .UseMonolithMesh()
            .ConfigureServices(services => services.AddInMemoryPersistence(persistence).Configure<CompilationCacheOptions>(o => o.CacheDirectory = cacheDirectory))
            .AddGraph();
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

    [Fact(Timeout = 10000)]
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
        var graphNode = await MeshQuery.QueryAsync<MeshNode>("path:graph scope:exact", ct: TestContext.Current.CancellationToken).FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        graphNode.Should().NotBeNull("Graph node should exist in persistence");
        graphNode!.NodeType.Should().Be("type/graph");
    }

    [Fact(Timeout = 10000)]
    public async Task Persistence_StoryContentIsPreserved()
    {
        // This test verifies that MeshNode.Content is correctly persisted
        // and can be loaded back from the persistence service

        // Get story node from persistence (directly, without routing)
        var storyNode = await MeshQuery.QueryAsync<MeshNode>("path:graph/story1 scope:exact", ct: TestContext.Current.CancellationToken).FirstOrDefaultAsync(TestContext.Current.CancellationToken);

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

    [Fact(Timeout = 10000)]
    public async Task MeshNode_ChildrenAvailable_ViaPersistence()
    {
        var client = GetClient();
        var graphAddress = new Address("graph");

        // Initialize graph hub
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(graphAddress),
            TestContext.Current.CancellationToken);

        // Act - get children via IMeshService
        var children = await MeshQuery.QueryAsync<MeshNode>("namespace:graph", null, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        // Assert - children should be available
        children.Should().NotBeNull("Children should be available via IMeshService");
        children.Should().HaveCountGreaterThanOrEqualTo(2, "Should have story1 and story2 as children");
        children.Should().Contain(n => n.Path == "graph/story1");
        children.Should().Contain(n => n.Path == "graph/story2");

        // Verify content is preserved in children
        var story1 = children.FirstOrDefault(n => n.Path == "graph/story1");
        story1.Should().NotBeNull();
        story1!.Content.Should().NotBeNull();
        story1.Content.Should().BeOfType<TestStory>();
    }

    [Fact(Timeout = 10000)]
    public async Task Persistence_CanCreateNodeWithContent()
    {
        // This test verifies that nodes with Content can be created via IMeshStorage

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
        await NodeFactory.CreateNodeAsync(newStory, ct: TestContext.Current.CancellationToken);

        // Assert - verify the node with content is persisted
        var persistedNode = await MeshQuery.QueryAsync<MeshNode>("path:graph/story3 scope:exact", ct: TestContext.Current.CancellationToken).FirstOrDefaultAsync(TestContext.Current.CancellationToken);
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

    [Fact(Timeout = 10000)]
    public async Task Persistence_CanUpdateNodeWithContent()
    {
        // This test verifies that nodes with Content can be updated directly via persistence.
        // Note: CreateNodeAsync rejects existing nodes ("Node already exists"),
        // and UpdateNodeRequest requires DataChangeRequest handlers which are not
        // registered on the mesh hub in this minimal test setup.

        // Verify initial data exists
        var initialNode = await MeshQuery.QueryAsync<MeshNode>("path:graph/story1 scope:exact", ct: TestContext.Current.CancellationToken).FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        initialNode.Should().NotBeNull();
        var initialContent = initialNode!.Content as TestStory;
        initialContent!.Points.Should().Be(5);

        // Act - update node directly via persistence (SaveNodeAsync is create-or-update)
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
        await _persistence!.SaveNodeAsync(updatedNode, SetupJsonOptions, TestContext.Current.CancellationToken);

        // Assert - get updated data from persistence
        var persistedNode = await MeshQuery.QueryAsync<MeshNode>("path:graph/story1 scope:exact", ct: TestContext.Current.CancellationToken).FirstOrDefaultAsync(TestContext.Current.CancellationToken);
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
