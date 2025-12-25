using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Data;
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
    // Each test instance gets its own unique directory
    private static readonly string TestDirectoryBase = Path.Combine(Path.GetTempPath(), "MeshWeaverDynamicGraphTests");
    private string? _testDirectory;

    private IPersistenceService Persistence => ServiceProvider.GetRequiredService<IPersistenceService>();
    private IMeshCatalog MeshCatalog => ServiceProvider.GetRequiredService<IMeshCatalog>();
    private ITypeCompilationService TypeCompiler => ServiceProvider.GetRequiredService<ITypeCompilationService>();

    /// <summary>
    /// Gets the unique test directory for this test instance, creating it lazily.
    /// </summary>
    private string GetOrCreateTestDirectory()
    {
        if (_testDirectory == null)
        {
            _testDirectory = Path.Combine(TestDirectoryBase, Guid.NewGuid().ToString());
            Directory.CreateDirectory(_testDirectory);
        }
        return _testDirectory;
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

        // Configure unique cache directory for test isolation
        var cacheDirectory = Path.Combine(testDataDirectory, ".mesh-cache");

        return builder
            .UseMonolithMesh()
            .ConfigureServices(services =>
            {
                services.AddPersistence(persistence);
                services.Configure<CompilationCacheOptions>(o => o.CacheDirectory = cacheDirectory);
                return services;
            })
            .AddJsonGraphConfiguration(testDataDirectory, configuration);
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
        var resolution = await MeshCatalog.ResolvePathAsync("graph/org1");

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
        var resolution = await MeshCatalog.ResolvePathAsync("graph/org1/proj1/nonexistent/deep");

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
        var resolution = await MeshCatalog.ResolvePathAsync("graph/org1/proj1/story1");

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
        var resolution = await MeshCatalog.ResolvePathAsync("graph/org1/proj1/story1/Overview");

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
        var resolution = await MeshCatalog.ResolvePathAsync("nonexistent/path/here");

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
        var resolution = await MeshCatalog.ResolvePathAsync("graph/_Nodes");

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
    public async Task ResolvePath_TypeGraph_ResolvesToTypeGraphNode()
    {
        // Arrange
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Act
        var resolution = await meshCatalog.ResolvePathAsync("type/graph");

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
    public async Task ResolvePath_TypePaths_ResolveCorrectly(string typePath)
    {
        // Arrange
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Act
        var resolution = await meshCatalog.ResolvePathAsync(typePath);

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

    #region Default Layout Area Tests

    /// <summary>
    /// Tests that requesting the default layout area for an Organization node
    /// returns successfully without hanging.
    /// This validates that default views are properly configured for dynamically compiled node types.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task Organization_GetDefaultLayoutArea_DoesNotHang()
    {
        var graphAddress = new Address("graph");
        var orgAddress = new Address("graph/org1");

        // Get a client with data services configured
        var client = GetClient(c => c.AddData(data => data));

        // Initialize graph hub first (required for routing)
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(graphAddress),
            TestContext.Current.CancellationToken);

        // Initialize org hub - this should also set up default views
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(orgAddress),
            TestContext.Current.CancellationToken);

        // Act: Request the default layout area (Details) using stream
        // This should not hang if default views are properly configured
        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(MeshNodeView.DetailsArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(orgAddress, reference);

        // Wait for the stream to emit a value (with timeout from test attribute)
        var value = await stream.FirstAsync();

        // Assert
        value.Should().NotBe(default(JsonElement), "Default layout area should return content");
    }

    /// <summary>
    /// Tests that requesting an empty area (default view) for an Organization node works.
    /// When area is empty/null, the default view should be returned (Details).
    /// This matches the pattern used in LayoutTest.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task Organization_GetEmptyArea_ReturnsDefaultView()
    {
        var graphAddress = new Address("graph");
        var orgAddress = new Address("graph/org1");

        // Get a client with data services configured
        var client = GetClient(c => c.AddData(data => data));

        // Initialize graph hub first (required for routing)
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(graphAddress),
            TestContext.Current.CancellationToken);

        // Initialize org hub - this should also set up default views
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(orgAddress),
            TestContext.Current.CancellationToken);

        // Act: Request empty area - should return default view (Details)
        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(string.Empty);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(orgAddress, reference);

        // Wait for the stream to emit a value (with timeout from test attribute)
        var value = await stream.FirstAsync();

        // Assert
        value.Should().NotBe(default(JsonElement), "Empty area should return default view content");
    }

    #endregion
}

/// <summary>
/// Tests that replicate the exact structure from samples/Graph/Data:
/// - Node "Organizations" in Root namespace (no namespace)
/// - NodeType = "Type/Organizations"
/// - Type definition at Type/Organizations with DataModel
/// </summary>
[Collection("OrganizationsLayoutTests")]
public class OrganizationsLayoutTest : MonolithMeshTestBase
{
    private static readonly string TestDirectoryBase = Path.Combine(Path.GetTempPath(), "MeshWeaverOrganizationsTests");
    private string? _testDirectory;

    private string GetOrCreateTestDirectory()
    {
        if (_testDirectory == null)
        {
            _testDirectory = Path.Combine(TestDirectoryBase, Guid.NewGuid().ToString());
            Directory.CreateDirectory(_testDirectory);
        }
        return _testDirectory;
    }

    public OrganizationsLayoutTest(ITestOutputHelper output) : base(output)
    {
    }

    /// <summary>
    /// Sets up the exact structure from samples/Graph/Data:
    /// - Type/Organizations (NodeType definition)
    /// - Type/Organizations/dataModel.json
    /// - Root/Organizations.json (instance with nodeType: "Type/Organizations")
    /// </summary>
    private static void SetupOrganizationsStructure(InMemoryPersistenceService persistence)
    {
        // 1. Create Type/Organizations - the NodeType definition
        var organizationsTypeNode = new MeshNode("Organizations", "Type")
        {
            Name = "Organizations",
            NodeType = "NodeType",
            Description = "Catalog of organizations",
            IconName = "Building",
            DisplayOrder = 8,
            IsPersistent = true,
            Content = new NodeTypeDefinition
            {
                Id = "Organizations",
                DisplayName = "Organizations",
                IconName = "Building",
                Description = "Catalog of organizations",
                DisplayOrder = 8
            }
        };
        persistence.SaveNodeAsync(organizationsTypeNode).GetAwaiter().GetResult();

        // 2. Create the DataModel for Type/Organizations
        var organizationsDataModel = new DataModel
        {
            Id = "Organizations",
            DisplayName = "Organizations",
            IconName = "Building",
            Description = "Catalog of organizations",
            DisplayOrder = 8,
            TypeSource = "public record Organizations { }"
        };
        persistence.SavePartitionObjectsAsync("Type/Organizations", null, [organizationsDataModel]).GetAwaiter().GetResult();

        // 3. Create Organizations instance node in Root namespace (no namespace)
        // This matches samples/Graph/Data/Root/Organizations.json
        var organizationsInstance = new MeshNode("Organizations") // No namespace = Root
        {
            Name = "Organizations",
            NodeType = "Type/Organizations", // Points to Type/Organizations
            Description = "Catalog of organizations",
            IconName = "Building",
            DisplayOrder = 10,
            IsPersistent = true
        };
        persistence.SaveNodeAsync(organizationsInstance).GetAwaiter().GetResult();

        // 4. Create the graph root node (needed for initialization)
        var graphNode = new MeshNode("graph")
        {
            Name = "Graph",
            NodeType = "type/graph",
            IsPersistent = true
        };
        persistence.SaveNodeAsync(graphNode).GetAwaiter().GetResult();

        // 5. Create type/graph type definition
        var graphTypeNode = new MeshNode("graph", "type")
        {
            Name = "Graph",
            NodeType = "NodeType",
            IsPersistent = true,
            Content = new NodeTypeDefinition
            {
                Id = "graph",
                DisplayName = "Graph"
            }
        };
        persistence.SaveNodeAsync(graphTypeNode).GetAwaiter().GetResult();

        var graphDataModel = new DataModel
        {
            Id = "graph",
            DisplayName = "Graph",
            TypeSource = "public record Graph { }"
        };
        persistence.SavePartitionObjectsAsync("type/graph", null, [graphDataModel]).GetAwaiter().GetResult();
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var testDataDirectory = GetOrCreateTestDirectory();
        var persistence = new InMemoryPersistenceService();

        // Setup the exact structure from samples
        SetupOrganizationsStructure(persistence);

        var configBuilder = new ConfigurationBuilder();
        configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Graph:DataDirectory"] = testDataDirectory
        });
        var configuration = configBuilder.Build();

        // Configure unique cache directory for test isolation
        var cacheDirectory = Path.Combine(testDataDirectory, ".mesh-cache");

        return builder
            .UseMonolithMesh()
            .ConfigureServices(services =>
            {
                services.AddPersistence(persistence);
                services.Configure<CompilationCacheOptions>(o => o.CacheDirectory = cacheDirectory);
                return services;
            })
            .AddJsonGraphConfiguration(testDataDirectory, configuration);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();

        if (_testDirectory != null && Directory.Exists(_testDirectory))
        {
            try { Directory.Delete(_testDirectory, recursive: true); }
            catch { /* Ignore cleanup errors */ }
        }
    }

    /// <summary>
    /// Test that exactly replicates the sample data structure.
    /// Address "Organizations" should be reachable and return default layout area.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task Organizations_InRootNamespace_GetDefaultLayoutArea()
    {
        // Address is just "Organizations" since it has no namespace (Root)
        var graphAddress = new Address("graph");
        var organizationsAddress = new Address("Organizations");

        // Get a client with data services configured
        var client = GetClient(c => c.AddData(data => data));

        // IMPORTANT: Initialize the graph hub first to trigger NodeTypeRegistrationInitializer
        // This registers all NodeTypeConfigurations including "Type/Organizations"
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(graphAddress),
            TestContext.Current.CancellationToken);

        // Now initialize the Organizations hub - it should find the NodeTypeConfiguration
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(organizationsAddress),
            TestContext.Current.CancellationToken);

        // Act: Request the default layout area (empty = default view)
        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(string.Empty);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(organizationsAddress, reference);

        // Wait for the stream to emit a value
        var value = await stream.FirstAsync();

        // Assert
        value.Should().NotBe(default(JsonElement),
            "Organizations node should return default layout area content");
    }

    /// <summary>
    /// Test that the Organizations node can be resolved via path.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task Organizations_CanBeResolved()
    {
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Act
        var resolution = await meshCatalog.ResolvePathAsync("Organizations");

        // Assert
        resolution.Should().NotBeNull("Organizations should be resolvable");
        resolution!.Prefix.Should().Be("Organizations");
    }

    /// <summary>
    /// Test that the Type/Organizations NodeType can be resolved.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task TypeOrganizations_CanBeResolved()
    {
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Act
        var resolution = await meshCatalog.ResolvePathAsync("Type/Organizations");

        // Assert
        resolution.Should().NotBeNull("Type/Organizations should be resolvable");
        resolution!.Prefix.Should().Be("Type/Organizations");
    }

    /// <summary>
    /// Test that NodeTypeService can find the NodeType node for "Type/Organizations"
    /// when searching from context "Organizations".
    ///
    /// The key fix: NodeTypeService now also searches in the parent path of the nodeType
    /// when the nodeType contains a path separator (e.g., "Type/Organizations").
    /// This ensures types in "Type/" folder are found even if GlobalTypesPrefix is "type".
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task NodeTypeService_FindsNodeTypeNode_ForTypeOrganizations()
    {
        // Arrange
        var nodeTypeService = Mesh.ServiceProvider.GetRequiredService<INodeTypeService>();

        // Act - this should find Type/Organizations by searching in its parent path "Type"
        var nodeTypeNode = await nodeTypeService.GetNodeTypeNodeAsync("Type/Organizations", "Organizations");

        // Assert
        nodeTypeNode.Should().NotBeNull(
            "NodeTypeService should find the NodeType node for 'Type/Organizations'. " +
            "The search now includes the parent path of the nodeType.");
        nodeTypeNode!.Prefix.Should().Be("Type/Organizations");
        nodeTypeNode.NodeType.Should().Be("NodeType");
    }

    /// <summary>
    /// Test that NodeTypeService can find the DataModel for "Type/Organizations"
    /// when searching from context "Organizations".
    ///
    /// This is the critical path: when initializing the Organizations hub,
    /// we need to find the Type/Organizations NodeType definition to get
    /// the DataModel and compile the HubConfiguration.
    ///
    /// The bug: GetSearchPaths("Organizations") returns ["Organizations", "", "_types"]
    /// but Type/Organizations is a child of "Type" which is not in the search paths.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task NodeTypeService_FindsDataModel_ForTypeOrganizations()
    {
        // Arrange
        var nodeTypeService = Mesh.ServiceProvider.GetRequiredService<INodeTypeService>();

        // Act - this is what happens when compiling the Organizations node
        // The nodeType is "Type/Organizations" and the context is "Organizations"
        var dataModel = await nodeTypeService.GetDataModelAsync("Type/Organizations", "Organizations");

        // Assert - this should find the DataModel from Type/Organizations/dataModel partition
        dataModel.Should().NotBeNull(
            "NodeTypeService should find the DataModel for 'Type/Organizations' " +
            "even when searching from context 'Organizations'. " +
            "The search paths should include 'Type' since 'Type/Organizations' is a child of 'Type'.");
        dataModel!.Id.Should().Be("Organizations");
        dataModel.TypeSource.Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// Test that the Organizations node gets HubConfiguration from compiled assembly.
    ///
    /// When MeshCatalog.GetNodeAsync("Organizations") is called:
    /// 1. It loads the Organizations node from persistence (nodeType = "Type/Organizations")
    /// 2. It looks for NodeTypeConfiguration for "Type/Organizations" - not found initially
    /// 3. It calls CompilationService.CompileAndGetConfigurationsAsync(organizationsNode)
    /// 4. CompilationService calls NodeTypeService.GetDataModelAsync("Type/Organizations", "Organizations")
    /// 5. This MUST find the DataModel to compile the type and generate HubConfiguration
    /// 6. The compiled NodeTypeConfiguration is registered
    /// 7. MeshCatalog sets HubConfiguration on the returned node
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task Organizations_GetsHubConfiguration_FromCompiledAssembly()
    {
        // Arrange
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Act - get the Organizations node (this triggers on-demand compilation)
        var node = await meshCatalog.GetNodeAsync(new Address("Organizations"));

        // Assert
        node.Should().NotBeNull("Organizations node should exist");
        node!.HubConfiguration.Should().NotBeNull(
            "Organizations node should have HubConfiguration from the compiled Type/Organizations assembly. " +
            "If HubConfiguration is null, it means the on-demand compilation failed to find the DataModel.");
    }
}

[CollectionDefinition("OrganizationsLayoutTests", DisableParallelization = true)]
public class OrganizationsLayoutTestsCollection { }

/// <summary>
/// Tests that use FileSystemPersistenceService to validate JSON deserialization with $type discriminator.
/// This replicates the exact production scenario where nodes and partition objects are read from disk.
/// </summary>
[Collection("FileSystemPersistenceTests")]
public class FileSystemPersistenceTest : MonolithMeshTestBase
{
    private static readonly string TestDirectoryBase = Path.Combine(Path.GetTempPath(), "MeshWeaverFileSystemTests");
    private string? _testDirectory;

    private string GetOrCreateTestDirectory()
    {
        if (_testDirectory == null)
        {
            _testDirectory = Path.Combine(TestDirectoryBase, Guid.NewGuid().ToString());
            Directory.CreateDirectory(_testDirectory);
        }
        return _testDirectory;
    }

    public FileSystemPersistenceTest(ITestOutputHelper output) : base(output)
    {
    }

    /// <summary>
    /// Creates the same structure as samples/Graph/Data on disk with actual JSON files.
    /// This tests the real FileSystemPersistenceService path with JSON deserialization.
    /// </summary>
    private void SetupOrganizationsStructureOnDisk(string dataDirectory)
    {
        // 1. Create Type/Organizations.json - the NodeType definition
        var typeDir = Path.Combine(dataDirectory, "Type");
        Directory.CreateDirectory(typeDir);

        var organizationsTypeJson = """
        {
          "id": "Organizations",
          "namespace": "Type",
          "name": "Organizations",
          "nodeType": "NodeType",
          "description": "Catalog of organizations",
          "iconName": "Building",
          "displayOrder": 8,
          "isPersistent": true,
          "content": {
            "$type": "NodeTypeDefinition",
            "id": "Organizations",
            "namespace": "Type",
            "displayName": "Organizations",
            "iconName": "Building",
            "description": "Catalog of organizations",
            "displayOrder": 8
          }
        }
        """;
        File.WriteAllText(Path.Combine(typeDir, "Organizations.json"), organizationsTypeJson);

        // 2. Create Type/Organizations/dataModel.json - the DataModel
        var organizationsTypeDir = Path.Combine(typeDir, "Organizations");
        Directory.CreateDirectory(organizationsTypeDir);

        var dataModelJson = """
        {
          "$type": "DataModel",
          "id": "Organizations",
          "namespace": "Type",
          "displayName": "Organizations",
          "iconName": "Building",
          "description": "Catalog of organizations",
          "displayOrder": 8,
          "typeSource": "public record Organizations { }"
        }
        """;
        File.WriteAllText(Path.Combine(organizationsTypeDir, "dataModel.json"), dataModelJson);

        // 3. Create Organizations.json - the instance node in root namespace
        var organizationsInstanceJson = """
        {
          "id": "Organizations",
          "name": "Organizations",
          "nodeType": "Type/Organizations",
          "description": "Catalog of organizations",
          "iconName": "Building",
          "displayOrder": 10,
          "isPersistent": true,
          "content": {}
        }
        """;
        File.WriteAllText(Path.Combine(dataDirectory, "Organizations.json"), organizationsInstanceJson);

        // 4. Create graph.json - the root node
        var graphJson = """
        {
          "id": "graph",
          "name": "Graph",
          "nodeType": "type/graph",
          "isPersistent": true
        }
        """;
        File.WriteAllText(Path.Combine(dataDirectory, "graph.json"), graphJson);

        // 5. Create type/graph - type definition for graph
        var typeGraphDir = Path.Combine(dataDirectory, "type");
        Directory.CreateDirectory(typeGraphDir);

        var graphTypeJson = """
        {
          "id": "graph",
          "namespace": "type",
          "name": "Graph",
          "nodeType": "NodeType",
          "isPersistent": true,
          "content": {
            "$type": "NodeTypeDefinition",
            "id": "graph",
            "displayName": "Graph"
          }
        }
        """;
        File.WriteAllText(Path.Combine(typeGraphDir, "graph.json"), graphTypeJson);

        var graphTypeDataDir = Path.Combine(typeGraphDir, "graph");
        Directory.CreateDirectory(graphTypeDataDir);

        var graphDataModelJson = """
        {
          "$type": "DataModel",
          "id": "graph",
          "displayName": "Graph",
          "typeSource": "public record Graph { }"
        }
        """;
        File.WriteAllText(Path.Combine(graphTypeDataDir, "dataModel.json"), graphDataModelJson);
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var testDataDirectory = GetOrCreateTestDirectory();

        // Create actual JSON files on disk - this is the key difference from InMemoryPersistenceService tests
        SetupOrganizationsStructureOnDisk(testDataDirectory);

        var configBuilder = new ConfigurationBuilder();
        configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Graph:DataDirectory"] = testDataDirectory
        });
        var configuration = configBuilder.Build();

        var cacheDirectory = Path.Combine(testDataDirectory, ".mesh-cache");

        return builder
            .UseMonolithMesh()
            // Use FileSystemPersistence - this is the production path we're testing
            .AddFileSystemPersistence(testDataDirectory)
            .ConfigureServices(services =>
            {
                services.Configure<CompilationCacheOptions>(o => o.CacheDirectory = cacheDirectory);
                return services;
            })
            .AddJsonGraphConfiguration(testDataDirectory, configuration);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();

        if (_testDirectory != null && Directory.Exists(_testDirectory))
        {
            try { Directory.Delete(_testDirectory, recursive: true); }
            catch { /* Ignore cleanup errors */ }
        }
    }

    /// <summary>
    /// Tests that NodeTypeService can find the NodeType node when reading from disk.
    /// The node.Content must be deserialized as NodeTypeDefinition (not JsonElement)
    /// for the check `node.Content is NodeTypeDefinition` to succeed.
    ///
    /// This validates that:
    /// 1. ITypeRegistry is available at mesh level
    /// 2. ObjectPolymorphicConverter is properly added to FileSystemStorageAdapter
    /// 3. $type discriminator is respected during JSON deserialization
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task FileSystem_NodeTypeService_FindsNodeTypeNode_WithPolymorphicDeserialization()
    {
        // Arrange
        var nodeTypeService = Mesh.ServiceProvider.GetRequiredService<INodeTypeService>();

        // Act - this should find Type/Organizations by reading from disk
        var nodeTypeNode = await nodeTypeService.GetNodeTypeNodeAsync("Type/Organizations", "Organizations");

        // Assert
        nodeTypeNode.Should().NotBeNull(
            "NodeTypeService should find the NodeType node from disk. " +
            "If null, the Content property was likely deserialized as JsonElement instead of NodeTypeDefinition.");
        nodeTypeNode!.Prefix.Should().Be("Type/Organizations");
        nodeTypeNode.NodeType.Should().Be("NodeType");

        // Critical: Content must be NodeTypeDefinition, not JsonElement
        nodeTypeNode.Content.Should().BeOfType<NodeTypeDefinition>(
            "The $type discriminator in the JSON should cause Content to be deserialized as NodeTypeDefinition. " +
            "If this fails, ITypeRegistry is not properly configured for FileSystemStorageAdapter.");
    }

    /// <summary>
    /// Tests that DataModel can be loaded from disk partition files.
    /// This validates that GetPartitionObjectsAsync properly deserializes objects with $type.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task FileSystem_NodeTypeService_FindsDataModel_FromDiskPartition()
    {
        // Arrange
        var nodeTypeService = Mesh.ServiceProvider.GetRequiredService<INodeTypeService>();

        // Act - this reads dataModel.json from Type/Organizations/ folder
        var dataModel = await nodeTypeService.GetDataModelAsync("Type/Organizations", "Organizations");

        // Assert
        dataModel.Should().NotBeNull(
            "DataModel should be loaded from Type/Organizations/dataModel.json. " +
            "If null, the $type discriminator was not processed during JSON deserialization.");
        dataModel!.Id.Should().Be("Organizations");
        dataModel.TypeSource.Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// Tests the complete flow: node loading, type compilation, and HubConfiguration setting.
    /// This is the end-to-end test for the production scenario.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task FileSystem_Organizations_GetsHubConfiguration_FromCompiledAssembly()
    {
        // Arrange
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Act - get the Organizations node (triggers on-demand compilation from disk files)
        var node = await meshCatalog.GetNodeAsync(new Address("Organizations"));

        // Assert
        node.Should().NotBeNull("Organizations node should exist on disk");
        node!.HubConfiguration.Should().NotBeNull(
            "Organizations node should have HubConfiguration from the compiled assembly. " +
            "If null, the on-demand compilation failed - likely because NodeTypeDefinition or DataModel " +
            "were not properly deserialized from JSON (returned as JsonElement instead).");
    }
}

[CollectionDefinition("FileSystemPersistenceTests", DisableParallelization = true)]
public class FileSystemPersistenceTestsCollection { }

/// <summary>
/// Collection definition for DynamicGraphIntegrationTests.
/// Ensures tests in this collection run serially to avoid test isolation issues.
/// </summary>
[CollectionDefinition("DynamicGraphIntegrationTests", DisableParallelization = true)]
public class DynamicGraphIntegrationTestsCollection
{
}
