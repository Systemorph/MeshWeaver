using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
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
/// Integration tests for dynamically configured Graph using JSON configuration.
/// Tests verify that the JSON-based configuration works end-to-end with real messages.
/// </summary>
/// <remarks>
/// These tests use AddJsonGraphConfiguration instead of InstallAssemblies.
/// The types (Story, Organization, Project) are compiled at runtime from JSON TypeSource.
/// </remarks>
[Collection("DynamicGraphIntegrationTests")]
public class DynamicGraphIntegrationTest : MonolithMeshTestBase
{
    // Static field to hold test directory - initialized before base constructor runs
    private static readonly string TestDirectoryBase = Path.Combine(Path.GetTempPath(), "MeshWeaverDynamicGraphTests");

    [ThreadStatic]
    private static string? _currentTestDirectory;

    private IPersistenceService Persistence => ServiceProvider.GetRequiredService<IPersistenceService>();
    private IMeshCatalog MeshCatalog => ServiceProvider.GetRequiredService<IMeshCatalog>();
    private ITypeCompilationService TypeCompiler => ServiceProvider.GetRequiredService<ITypeCompilationService>();

    /// <summary>
    /// Gets or creates the test directory for this test instance.
    /// Called during ConfigureMesh which runs during base constructor.
    /// </summary>
    private static string GetOrCreateTestDirectory()
    {
        if (_currentTestDirectory == null)
        {
            _currentTestDirectory = Path.Combine(TestDirectoryBase, Guid.NewGuid().ToString());
            Directory.CreateDirectory(_currentTestDirectory);
        }
        return _currentTestDirectory;
    }

    public DynamicGraphIntegrationTest(ITestOutputHelper output) : base(output)
    {
    }

    private static void SetupTestConfiguration(InMemoryPersistenceService persistence)
    {
        // Create Story type using "type/" prefix for global types
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
    public string? Text { get; init; }
    public StoryStatus Status { get; init; } = StoryStatus.Todo;
    public int Points { get; init; }
}

public enum StoryStatus
{
    Todo,
    InProgress,
    Review,
    Done
}"
        };

        var storyNode = new MeshNode("type/story")
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
                DisplayName = "Story",
                IconName = "Document",
                Description = "A user story or task",
                DisplayOrder = 30
            }
        };
        persistence.SaveNodeAsync(storyNode).GetAwaiter().GetResult();
        persistence.SavePartitionObjectsAsync("type/story", null, [storyDataModel]).GetAwaiter().GetResult();

        // Create Organization type
        var orgDataModel = new DataModel
        {
            Id = "org",
            DisplayName = "Organization",
            IconName = "Building",
            Description = "An organization",
            DisplayOrder = 10,
            TypeSource = @"
public record Organization
{
    [Key]
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
}"
        };

        var orgNode = new MeshNode("type/org")
        {
            Name = "Organization",
            NodeType = "NodeType",
            Description = "An organization",
            IconName = "Building",
            DisplayOrder = 10,
            IsPersistent = true,
            Content = new NodeTypeDefinition
            {
                Id = "org",
                DisplayName = "Organization",
                IconName = "Building",
                Description = "An organization",
                DisplayOrder = 10
            }
        };
        persistence.SaveNodeAsync(orgNode).GetAwaiter().GetResult();
        persistence.SavePartitionObjectsAsync("type/org", null, [orgDataModel]).GetAwaiter().GetResult();

        // Create Project type
        var projectDataModel = new DataModel
        {
            Id = "project",
            DisplayName = "Project",
            IconName = "Folder",
            Description = "A project",
            DisplayOrder = 20,
            TypeSource = @"
public record Project
{
    [Key]
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
}"
        };

        var projectNode = new MeshNode("type/project")
        {
            Name = "Project",
            NodeType = "NodeType",
            Description = "A project",
            IconName = "Folder",
            DisplayOrder = 20,
            IsPersistent = true,
            Content = new NodeTypeDefinition
            {
                Id = "project",
                DisplayName = "Project",
                IconName = "Folder",
                Description = "A project",
                DisplayOrder = 20
            }
        };
        persistence.SaveNodeAsync(projectNode).GetAwaiter().GetResult();
        persistence.SavePartitionObjectsAsync("type/project", null, [projectDataModel]).GetAwaiter().GetResult();

        // Create Graph type
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

        var graphTypeNode = new MeshNode("type/graph")
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
                DisplayName = "Graph",
                IconName = "Diagram",
                Description = "The graph root",
                DisplayOrder = 0
            }
        };
        persistence.SaveNodeAsync(graphTypeNode).GetAwaiter().GetResult();
        persistence.SavePartitionObjectsAsync("type/graph", null, [graphDataModel]).GetAwaiter().GetResult();
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var testDataDirectory = GetOrCreateTestDirectory();

        // Create in-memory persistence and pre-seed with test data
        var persistence = new InMemoryPersistenceService();

        // Setup NodeType configurations using "type/" prefix
        SetupTestConfiguration(persistence);

        // Pre-seed the hierarchy: graph -> org -> project -> story
        // NodeType uses full path to type definition (e.g., "type/graph", "type/org")
        persistence.SaveNodeAsync(new MeshNode("graph") { Name = "Graph", NodeType = "type/graph" }).GetAwaiter().GetResult();
        persistence.SaveNodeAsync(new MeshNode("graph/org1") { Name = "Organization 1", NodeType = "type/org", Description = "First org" }).GetAwaiter().GetResult();
        persistence.SaveNodeAsync(new MeshNode("graph/org2") { Name = "Organization 2", NodeType = "type/org", Description = "Second org" }).GetAwaiter().GetResult();
        persistence.SaveNodeAsync(new MeshNode("graph/org1/proj1") { Name = "Project 1", NodeType = "type/project" }).GetAwaiter().GetResult();
        persistence.SaveNodeAsync(new MeshNode("graph/org1/proj2") { Name = "Project 2", NodeType = "type/project" }).GetAwaiter().GetResult();
        persistence.SaveNodeAsync(new MeshNode("graph/org1/proj1/story1") { Name = "Story 1", NodeType = "type/story" }).GetAwaiter().GetResult();
        persistence.SaveNodeAsync(new MeshNode("graph/org1/proj1/story2") { Name = "Story 2", NodeType = "type/story" }).GetAwaiter().GetResult();

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

    #region Hub Initialization Tests

    [Fact(Timeout = 90000)]
    public async Task GraphHub_LoadsChildrenFromPersistence_AtInitialization()
    {
        var client = GetClient();
        var graphAddress = new Address("graph");

        // Initialize graph hub via ping
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(graphAddress),
            TestContext.Current.CancellationToken);

        // Verify persistence has the pre-seeded data
        var children = await Persistence.GetChildrenAsync("graph").ToListAsync(TestContext.Current.CancellationToken);
        children.Should().HaveCount(2, "graph should have 2 org children pre-seeded");
        children.Should().Contain(n => n.Prefix == "graph/org1");
        children.Should().Contain(n => n.Prefix == "graph/org2");
    }

    [Fact(Timeout = 90000)]
    public async Task OrgHub_LoadsChildrenFromPersistence_AtInitialization()
    {
        var client = GetClient();
        var graphAddress = new Address("graph");
        var orgAddress = new Address("graph/org1");

        // Initialize graph hub first (required for routing to child hubs)
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(graphAddress),
            TestContext.Current.CancellationToken);

        // Initialize org hub via ping
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(orgAddress),
            TestContext.Current.CancellationToken);

        // Verify persistence has the pre-seeded projects
        var children = await Persistence.GetChildrenAsync("graph/org1").ToListAsync(TestContext.Current.CancellationToken);
        children.Should().HaveCount(2, "org1 should have 2 project children pre-seeded");
        children.Should().Contain(n => n.Prefix == "graph/org1/proj1");
        children.Should().Contain(n => n.Prefix == "graph/org1/proj2");
    }

    [Fact(Timeout = 90000)]
    public async Task ProjectHub_LoadsChildrenFromPersistence_AtInitialization()
    {
        var client = GetClient();
        var graphAddress = new Address("graph");
        var projAddress = new Address("graph/org1/proj1");

        // Initialize graph hub first (required for routing to child hubs)
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(graphAddress),
            TestContext.Current.CancellationToken);

        // Initialize project hub via ping
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(projAddress),
            TestContext.Current.CancellationToken);

        // Verify persistence has the pre-seeded stories
        var children = await Persistence.GetChildrenAsync("graph/org1/proj1").ToListAsync(TestContext.Current.CancellationToken);
        children.Should().HaveCount(2, "proj1 should have 2 story children pre-seeded");
        children.Should().Contain(n => n.Prefix == "graph/org1/proj1/story1");
        children.Should().Contain(n => n.Prefix == "graph/org1/proj1/story2");
    }

    #endregion

    #region ResolvePath Tests

    [Fact(Timeout = 90000)]
    public async Task ResolvePath_FindsPersistedNode_NotInConfig()
    {
        var client = GetClient();
        var graphAddress = new Address("graph");

        // Initialize graph hub first - this loads NodeTypeConfigurations
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(graphAddress),
            TestContext.Current.CancellationToken);

        // Act
        var resolution = MeshCatalog.ResolvePath("graph/org1");

        // Assert
        resolution.Should().NotBeNull("persistence has graph/org1");
        resolution!.Prefix.Should().Be("graph/org1");
        resolution.Remainder.Should().BeNull();
    }

    [Fact(Timeout = 90000)]
    public async Task ResolvePath_WalksUpHierarchy_FindsBestMatch()
    {
        var client = GetClient();
        var graphAddress = new Address("graph");

        // Initialize graph hub first - this loads NodeTypeConfigurations
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(graphAddress),
            TestContext.Current.CancellationToken);

        // Act: resolve path that goes deeper than persisted (nonexistent/deep doesn't exist)
        var resolution = MeshCatalog.ResolvePath("graph/org1/proj1/nonexistent/deep");

        // Assert: should match graph/org1/proj1 with remainder
        resolution.Should().NotBeNull();
        resolution!.Prefix.Should().Be("graph/org1/proj1");
        resolution.Remainder.Should().Be("nonexistent/deep");
    }

    [Fact(Timeout = 90000)]
    public async Task ResolvePath_ReturnsExactMatch_WhenFullPathExists()
    {
        var client = GetClient();
        var graphAddress = new Address("graph");

        // Initialize graph hub first - this loads NodeTypeConfigurations
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(graphAddress),
            TestContext.Current.CancellationToken);

        // Act
        var resolution = MeshCatalog.ResolvePath("graph/org1/proj1/story1");

        // Assert
        resolution.Should().NotBeNull();
        resolution!.Prefix.Should().Be("graph/org1/proj1/story1");
        resolution.Remainder.Should().BeNull();
    }

    [Fact(Timeout = 90000)]
    public async Task ResolvePath_WithRemainder_ReturnsCorrectParts()
    {
        var client = GetClient();
        var graphAddress = new Address("graph");

        // Initialize graph hub first - this loads NodeTypeConfigurations
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(graphAddress),
            TestContext.Current.CancellationToken);

        // Act: resolve path with additional segments beyond existing node
        var resolution = MeshCatalog.ResolvePath("graph/org1/proj1/story1/Overview");

        // Assert
        resolution.Should().NotBeNull();
        resolution!.Prefix.Should().Be("graph/org1/proj1/story1");
        resolution.Remainder.Should().Be("Overview");
    }

    [Fact(Timeout = 90000)]
    public async Task ResolvePath_ReturnsNull_WhenNoMatchFound()
    {
        var client = GetClient();
        var graphAddress = new Address("graph");

        // Initialize graph hub first - this loads NodeTypeConfigurations
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(graphAddress),
            TestContext.Current.CancellationToken);

        // Act: resolve path that doesn't exist anywhere
        var resolution = MeshCatalog.ResolvePath("nonexistent/path/here");

        // Assert
        resolution.Should().BeNull();
    }

    [Fact(Timeout = 90000)]
    public async Task ResolvePath_UnderscorePrefixedSegment_ParsesAsRemainder()
    {
        var client = GetClient();
        var graphAddress = new Address("graph");

        // Initialize graph hub first - this loads NodeTypeConfigurations
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(graphAddress),
            TestContext.Current.CancellationToken);

        // Act: resolve path with underscore-prefixed segment (layout area)
        var resolution = MeshCatalog.ResolvePath("graph/_Nodes");

        // Assert: "graph" is the address, "_Nodes" is the remainder (layout area)
        resolution.Should().NotBeNull();
        resolution!.Prefix.Should().Be("graph");
        resolution.Remainder.Should().Be("_Nodes");
    }

    #endregion

    #region Type Node Navigation Tests

    /// <summary>
    /// Navigating to type/graph should resolve to the type/graph node.
    /// The type node has NodeType = "NodeType" and contains the type definition.
    /// </summary>
    [Fact(Timeout = 10000)]
    public void ResolvePath_TypeGraph_ResolvesToTypeGraphNode()
    {
        // Arrange
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Act
        var resolution = meshCatalog.ResolvePath("type/graph");

        // Assert
        resolution.Should().NotBeNull("type/graph should be resolvable");
        resolution!.Prefix.Should().Be("type/graph");
        resolution.Remainder.Should().BeNull();
    }

    /// <summary>
    /// GetNodeAsync for type/graph should return the NodeType definition node.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task GetNodeAsync_TypeGraph_ReturnsNodeTypeDefinition()
    {
        // Arrange
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Act
        var node = await meshCatalog.GetNodeAsync(new Address("type", "graph"));

        // Assert
        node.Should().NotBeNull("type/graph node should exist");
        node!.Path.Should().Be("type/graph");
        node.NodeType.Should().Be("NodeType");
        node.Content.Should().BeOfType<NodeTypeDefinition>();
    }

    /// <summary>
    /// Navigating to type/* paths should work for all type definitions.
    /// </summary>
    [Theory]
    [InlineData("type/graph")]
    [InlineData("type/org")]
    [InlineData("type/project")]
    [InlineData("type/story")]
    public void ResolvePath_TypePaths_ResolveCorrectly(string typePath)
    {
        // Arrange
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Act
        var resolution = meshCatalog.ResolvePath(typePath);

        // Assert
        resolution.Should().NotBeNull($"{typePath} should be resolvable");
        resolution!.Prefix.Should().Be(typePath);
        resolution.Remainder.Should().BeNull();
    }

    /// <summary>
    /// Type nodes should exist in persistence and be retrievable.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task TypeNodes_ExistInPersistence()
    {
        // Assert that type nodes exist
        var graphType = await Persistence.GetNodeAsync("type/graph");
        graphType.Should().NotBeNull("type/graph should exist in persistence");
        graphType!.NodeType.Should().Be("NodeType");

        var orgType = await Persistence.GetNodeAsync("type/org");
        orgType.Should().NotBeNull("type/org should exist in persistence");
        orgType!.NodeType.Should().Be("NodeType");
    }

    #endregion

    #region MoveNodeAsync Tests

    /// <summary>
    /// Move single node to new path.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task MoveNodeAsync_MovesNodeToNewPath()
    {
        // Arrange - create a node to move
        await Persistence.SaveNodeAsync(new MeshNode("graph/movetest") { Name = "Move Test", NodeType = "type/org" });

        // Act
        var moved = await Persistence.MoveNodeAsync("graph/movetest", "graph/movetest-renamed");

        // Assert
        moved.Should().NotBeNull();
        moved.Prefix.Should().Be("graph/movetest-renamed");
        moved.Name.Should().Be("Move Test");

        var oldNode = await Persistence.GetNodeAsync("graph/movetest", TestContext.Current.CancellationToken);
        oldNode.Should().BeNull("Original node should be deleted");

        var newNode = await Persistence.GetNodeAsync("graph/movetest-renamed", TestContext.Current.CancellationToken);
        newNode.Should().NotBeNull("Node should exist at new path");
        newNode!.Name.Should().Be("Move Test");
    }

    /// <summary>
    /// Move node with descendants - all paths should be updated.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task MoveNodeAsync_MovesDescendantsWithUpdatedPaths()
    {
        // Arrange - create a hierarchy to move
        await Persistence.SaveNodeAsync(new MeshNode("graph/parent") { Name = "Parent", NodeType = "type/org" });
        await Persistence.SaveNodeAsync(new MeshNode("graph/parent/child1") { Name = "Child 1", NodeType = "type/project" });
        await Persistence.SaveNodeAsync(new MeshNode("graph/parent/child2") { Name = "Child 2", NodeType = "type/project" });
        await Persistence.SaveNodeAsync(new MeshNode("graph/parent/child1/grandchild") { Name = "Grandchild", NodeType = "type/story" });

        // Act
        await Persistence.MoveNodeAsync("graph/parent", "graph/newparent");

        // Assert - old paths should not exist
        (await Persistence.GetNodeAsync("graph/parent", TestContext.Current.CancellationToken)).Should().BeNull();
        (await Persistence.GetNodeAsync("graph/parent/child1", TestContext.Current.CancellationToken)).Should().BeNull();
        (await Persistence.GetNodeAsync("graph/parent/child2", TestContext.Current.CancellationToken)).Should().BeNull();
        (await Persistence.GetNodeAsync("graph/parent/child1/grandchild", TestContext.Current.CancellationToken)).Should().BeNull();

        // Assert - new paths should exist with correct data
        var newParent = await Persistence.GetNodeAsync("graph/newparent", TestContext.Current.CancellationToken);
        newParent.Should().NotBeNull();
        newParent!.Name.Should().Be("Parent");

        var newChild1 = await Persistence.GetNodeAsync("graph/newparent/child1", TestContext.Current.CancellationToken);
        newChild1.Should().NotBeNull();
        newChild1!.Name.Should().Be("Child 1");

        var newChild2 = await Persistence.GetNodeAsync("graph/newparent/child2", TestContext.Current.CancellationToken);
        newChild2.Should().NotBeNull();
        newChild2!.Name.Should().Be("Child 2");

        var newGrandchild = await Persistence.GetNodeAsync("graph/newparent/child1/grandchild", TestContext.Current.CancellationToken);
        newGrandchild.Should().NotBeNull();
        newGrandchild!.Name.Should().Be("Grandchild");
    }

    /// <summary>
    /// Move node with comments - comments should be migrated to new path.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task MoveNodeAsync_MigratesCommentsToNewPath()
    {
        // Arrange - create node with comments
        await Persistence.SaveNodeAsync(new MeshNode("graph/commented") { Name = "Commented Node", NodeType = "type/org" });
        await Persistence.AddCommentAsync(new Comment { NodePath = "graph/commented", Text = "Comment 1", Author = "User1" });
        await Persistence.AddCommentAsync(new Comment { NodePath = "graph/commented", Text = "Comment 2", Author = "User2" });

        // Verify comments exist at old path
        var oldComments = await Persistence.GetCommentsAsync("graph/commented").ToListAsync(TestContext.Current.CancellationToken);
        oldComments.Should().HaveCount(2);

        // Act
        await Persistence.MoveNodeAsync("graph/commented", "graph/commented-moved");

        // Assert - comments should be at new path
        var newComments = await Persistence.GetCommentsAsync("graph/commented-moved").ToListAsync(TestContext.Current.CancellationToken);
        newComments.Should().HaveCount(2, "Comments should be migrated to new path");
        newComments.Should().Contain(c => c.Text == "Comment 1");
        newComments.Should().Contain(c => c.Text == "Comment 2");

        // Assert - no comments at old path
        var remainingOldComments = await Persistence.GetCommentsAsync("graph/commented").ToListAsync(TestContext.Current.CancellationToken);
        remainingOldComments.Should().BeEmpty("Comments should not remain at old path");
    }

    /// <summary>
    /// Move node throws when source doesn't exist.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task MoveNodeAsync_ThrowsWhenSourceNotFound()
    {
        // Act & Assert
        var act = () => Persistence.MoveNodeAsync("graph/nonexistent", "graph/newpath");
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*");
    }

    /// <summary>
    /// Move node throws when target path already exists.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task MoveNodeAsync_ThrowsWhenTargetExists()
    {
        // Arrange
        await Persistence.SaveNodeAsync(new MeshNode("graph/source") { Name = "Source", NodeType = "type/org" });
        await Persistence.SaveNodeAsync(new MeshNode("graph/target") { Name = "Target", NodeType = "type/org" });

        // Act & Assert
        var act = () => Persistence.MoveNodeAsync("graph/source", "graph/target");
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already exists*");
    }

    #endregion
}

/// <summary>
/// Collection definition for DynamicGraphIntegrationTests.
/// Ensures tests in this collection run serially to avoid test isolation issues.
/// </summary>
[CollectionDefinition("DynamicGraphIntegrationTests", DisableParallelization = true)]
public class DynamicGraphIntegrationTestsCollection
{
}
