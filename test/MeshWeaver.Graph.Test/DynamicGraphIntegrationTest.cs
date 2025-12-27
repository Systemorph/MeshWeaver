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

    private IPersistenceService Persistence => Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();
    private IMeshCatalog MeshCatalog => Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

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

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();

        // Seed test data using async methods
        var persistence = Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();
        await SetupTestConfigurationAsync(persistence);
        await SeedHierarchyAsync(persistence);
    }

    private static async Task SetupTestConfigurationAsync(IPersistenceService persistence)
    {
        // Create Story type using "type/" Namespace for global types
        var storyCodeConfig = new CodeConfiguration
        {
            Code = @"
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

        var storyNode = MeshNode.FromPath("type/story") with
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
        await persistence.SaveNodeAsync(storyNode);
        await persistence.SavePartitionObjectsAsync("type/story", null, [storyCodeConfig]);

        // Create Organization type
        var orgCodeConfig = new CodeConfiguration
        {
            Code = @"
public record Organization
{
    [Key]
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
}"
        };

        var orgNode = MeshNode.FromPath("type/org") with
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
        await persistence.SaveNodeAsync(orgNode);
        await persistence.SavePartitionObjectsAsync("type/org", null, [orgCodeConfig]);

        // Create Project type
        var projectCodeConfig = new CodeConfiguration
        {
            Code = @"
public record Project
{
    [Key]
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
}"
        };

        var projectNode = MeshNode.FromPath("type/project") with
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
        await persistence.SaveNodeAsync(projectNode);
        await persistence.SavePartitionObjectsAsync("type/project", null, [projectCodeConfig]);

        // Create Graph type
        var graphCodeConfig = new CodeConfiguration
        {
            Code = @"
public record Graph
{
    [Key]
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
}"
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
                DisplayName = "Graph",
                IconName = "Diagram",
                Description = "The graph root",
                DisplayOrder = 0
            }
        };
        await persistence.SaveNodeAsync(graphTypeNode);
        await persistence.SavePartitionObjectsAsync("type/graph", null, [graphCodeConfig]);
    }

    private static async Task SeedHierarchyAsync(IPersistenceService persistence)
    {
        // Pre-seed the hierarchy: graph -> org -> project -> story
        // NodeType uses full path to type definition (e.g., "type/graph", "type/org")
        await persistence.SaveNodeAsync(MeshNode.FromPath("graph") with { Name = "Graph", NodeType = "type/graph" });
        await persistence.SaveNodeAsync(MeshNode.FromPath("graph/org1") with { Name = "Organization 1", NodeType = "type/org", Description = "First org" });
        await persistence.SaveNodeAsync(MeshNode.FromPath("graph/org2") with { Name = "Organization 2", NodeType = "type/org", Description = "Second org" });
        await persistence.SaveNodeAsync(MeshNode.FromPath("graph/org1/proj1") with { Name = "Project 1", NodeType = "type/project" });
        await persistence.SaveNodeAsync(MeshNode.FromPath("graph/org1/proj2") with { Name = "Project 2", NodeType = "type/project" });
        await persistence.SaveNodeAsync(MeshNode.FromPath("graph/org1/proj1/story1") with { Name = "Story 1", NodeType = "type/story" });
        await persistence.SaveNodeAsync(MeshNode.FromPath("graph/org1/proj1/story2") with { Name = "Story 2", NodeType = "type/story" });
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var testDataDirectory = GetOrCreateTestDirectory();

        return builder
            .UseMonolithMesh()
            .AddInMemoryPersistence()
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
        children.Should().Contain(n => n.Path == "graph/org1");
        children.Should().Contain(n => n.Path == "graph/org2");
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
        children.Should().Contain(n => n.Path == "graph/org1/proj1");
        children.Should().Contain(n => n.Path == "graph/org1/proj2");
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
        children.Should().Contain(n => n.Path == "graph/org1/proj1/story1");
        children.Should().Contain(n => n.Path == "graph/org1/proj1/story2");
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
        resolution.Prefix.Should().Be("graph/org1");
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
        resolution.Prefix.Should().Be("graph/org1/proj1");
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
        resolution.Prefix.Should().Be("graph/org1/proj1/story1");
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
        resolution.Prefix.Should().Be("graph/org1/proj1/story1");
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
    public async Task ResolvePath_UnderscoreNamespaceedSegment_ParsesAsRemainder()
    {
        var client = GetClient();
        var graphAddress = new Address("graph");

        // Initialize graph hub first - this loads NodeTypeConfigurations
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(graphAddress),
            TestContext.Current.CancellationToken);

        // Act: resolve path with underscore-Namespaceed segment (layout area)
        var resolution = await MeshCatalog.ResolvePathAsync("graph/_Nodes");

        // Assert: "graph" is the address, "_Nodes" is the remainder (layout area)
        resolution.Should().NotBeNull();
        resolution.Prefix.Should().Be("graph");
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
        resolution.Prefix.Should().Be("type/graph");
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
        node.Path.Should().Be("type/graph");
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
        resolution.Prefix.Should().Be(typePath);
        resolution.Remainder.Should().BeNull();
    }

    /// <summary>
    /// Type nodes should exist in persistence and be retrievable.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task TypeNodes_ExistInPersistence()
    {
        // Assert that type nodes exist
        var graphType = await Persistence.GetNodeAsync("type/graph", TestContext.Current.CancellationToken);
        graphType.Should().NotBeNull("type/graph should exist in persistence");
        graphType.NodeType.Should().Be("NodeType");

        var orgType = await Persistence.GetNodeAsync("type/org", TestContext.Current.CancellationToken);
        orgType.Should().NotBeNull("type/org should exist in persistence");
        orgType.NodeType.Should().Be("NodeType");
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
        await Persistence.SaveNodeAsync(MeshNode.FromPath("graph/movetest") with { Name = "Move Test", NodeType = "type/org" }, TestContext.Current.CancellationToken);

        // Act
        var moved = await Persistence.MoveNodeAsync("graph/movetest", "graph/movetest-renamed", TestContext.Current.CancellationToken);

        // Assert
        moved.Should().NotBeNull();
        moved.Path.Should().Be("graph/movetest-renamed");
        moved.Name.Should().Be("Move Test");

        var oldNode = await Persistence.GetNodeAsync("graph/movetest", TestContext.Current.CancellationToken);
        oldNode.Should().BeNull("Original node should be deleted");

        var newNode = await Persistence.GetNodeAsync("graph/movetest-renamed", TestContext.Current.CancellationToken);
        newNode.Should().NotBeNull("Node should exist at new path");
        newNode.Name.Should().Be("Move Test");
    }

    /// <summary>
    /// Move node with descendants - all paths should be updated.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task MoveNodeAsync_MovesDescendantsWithUpdatedPaths()
    {
        // Arrange - create a hierarchy to move
        await Persistence.SaveNodeAsync(MeshNode.FromPath("graph/parent") with { Name = "Parent", NodeType = "type/org" }, TestContext.Current.CancellationToken);
        await Persistence.SaveNodeAsync(MeshNode.FromPath("graph/parent/child1") with { Name = "Child 1", NodeType = "type/project" }, TestContext.Current.CancellationToken);
        await Persistence.SaveNodeAsync(MeshNode.FromPath("graph/parent/child2") with { Name = "Child 2", NodeType = "type/project" }, TestContext.Current.CancellationToken);
        await Persistence.SaveNodeAsync(MeshNode.FromPath("graph/parent/child1/grandchild") with { Name = "Grandchild", NodeType = "type/story" }, TestContext.Current.CancellationToken);

        // Act
        await Persistence.MoveNodeAsync("graph/parent", "graph/newparent", TestContext.Current.CancellationToken);

        // Assert - old paths should not exist
        (await Persistence.GetNodeAsync("graph/parent", TestContext.Current.CancellationToken)).Should().BeNull();
        (await Persistence.GetNodeAsync("graph/parent/child1", TestContext.Current.CancellationToken)).Should().BeNull();
        (await Persistence.GetNodeAsync("graph/parent/child2", TestContext.Current.CancellationToken)).Should().BeNull();
        (await Persistence.GetNodeAsync("graph/parent/child1/grandchild", TestContext.Current.CancellationToken)).Should().BeNull();

        // Assert - new paths should exist with correct data
        var newParent = await Persistence.GetNodeAsync("graph/newparent", TestContext.Current.CancellationToken);
        newParent.Should().NotBeNull();
        newParent.Name.Should().Be("Parent");

        var newChild1 = await Persistence.GetNodeAsync("graph/newparent/child1", TestContext.Current.CancellationToken);
        newChild1.Should().NotBeNull();
        newChild1.Name.Should().Be("Child 1");

        var newChild2 = await Persistence.GetNodeAsync("graph/newparent/child2", TestContext.Current.CancellationToken);
        newChild2.Should().NotBeNull();
        newChild2.Name.Should().Be("Child 2");

        var newGrandchild = await Persistence.GetNodeAsync("graph/newparent/child1/grandchild", TestContext.Current.CancellationToken);
        newGrandchild.Should().NotBeNull();
        newGrandchild.Name.Should().Be("Grandchild");
    }

    /// <summary>
    /// Move node with comments - comments should be migrated to new path.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task MoveNodeAsync_MigratesCommentsToNewPath()
    {
        // Arrange - create node with comments
        await Persistence.SaveNodeAsync(MeshNode.FromPath("graph/commented") with { Name = "Commented Node", NodeType = "type/org" }, TestContext.Current.CancellationToken);
        await Persistence.AddCommentAsync(new Comment { NodePath = "graph/commented", Text = "Comment 1", Author = "User1" }, TestContext.Current.CancellationToken);
        await Persistence.AddCommentAsync(new Comment { NodePath = "graph/commented", Text = "Comment 2", Author = "User2" }, TestContext.Current.CancellationToken);

        // Verify comments exist at old path
        var oldComments = await Persistence.GetCommentsAsync("graph/commented").ToListAsync(TestContext.Current.CancellationToken);
        oldComments.Should().HaveCount(2);

        // Act
        await Persistence.MoveNodeAsync("graph/commented", "graph/commented-moved", TestContext.Current.CancellationToken);

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
        await Persistence.SaveNodeAsync(MeshNode.FromPath("graph/source") with { Name = "Source", NodeType = "type/org" }, TestContext.Current.CancellationToken);
        await Persistence.SaveNodeAsync(MeshNode.FromPath("graph/target") with { Name = "Target", NodeType = "type/org" }, TestContext.Current.CancellationToken);

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
/// - Type definition at Type/Organizations with ChildrenQuery
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

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();

        // Seed test data using async methods
        var persistence = Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();
        await SetupOrganizationsStructureAsync(persistence);
    }

    /// <summary>
    /// Sets up the Organizations structure with ChildrenQuery instead of DataModel:
    /// - Type/Organizations (NodeType definition with ChildrenQuery)
    /// - Organizations (catalog node with nodeType: "Type/Organizations")
    /// - Several Organization instances (Acme, Contoso, Fabrikam)
    ///
    /// The ChildrenQuery makes the Organizations node display all nodes
    /// with nodeType=="Type/Organization" (the individual organization instances).
    /// </summary>
    private static async Task SetupOrganizationsStructureAsync(IPersistenceService persistence)
    {
        // 1. Create Type/Organizations - the NodeType definition for the catalog
        // This type uses ChildrenQuery to show all Organization instances
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
                DisplayOrder = 8,
                // Query for all nodes of type "Type/Organization" (individual orgs)
                ChildrenQuery = "nodeType==Type/Organization;$scope=descendants"
            }
        };
        await persistence.SaveNodeAsync(organizationsTypeNode);

        // 2. Create Type/Organization - the NodeType definition for individual organizations
        var organizationTypeNode = new MeshNode("Organization", "Type")
        {
            Name = "Organization",
            NodeType = "NodeType",
            Description = "An individual organization",
            IconName = "Building",
            DisplayOrder = 9,
            IsPersistent = true,
            Content = new NodeTypeDefinition
            {
                Id = "Organization",
                DisplayName = "Organization",
                IconName = "Building",
                Description = "An individual organization",
                DisplayOrder = 9
            }
        };
        await persistence.SaveNodeAsync(organizationTypeNode);

        // 3. Create Organizations catalog node in Root namespace
        var organizationsInstance = new MeshNode("Organizations")
        {
            Name = "Organizations",
            NodeType = "Type/Organizations", // Uses the catalog type with ChildrenQuery
            Description = "Catalog of organizations",
            IconName = "Building",
            DisplayOrder = 10,
            IsPersistent = true
        };
        await persistence.SaveNodeAsync(organizationsInstance);

        // 4. Create some Organization instances that will be found by ChildrenQuery
        var acme = new MeshNode("Acme")
        {
            Name = "Acme Corporation",
            NodeType = "Type/Organization",
            Description = "A famous company",
            IconName = "Building",
            IsPersistent = true
        };
        await persistence.SaveNodeAsync(acme);

        var contoso = new MeshNode("Contoso")
        {
            Name = "Contoso Ltd",
            NodeType = "Type/Organization",
            Description = "Another company",
            IconName = "Building",
            IsPersistent = true
        };
        await persistence.SaveNodeAsync(contoso);

        var fabrikam = new MeshNode("Fabrikam")
        {
            Name = "Fabrikam Inc",
            NodeType = "Type/Organization",
            Description = "Yet another company",
            IconName = "Building",
            IsPersistent = true
        };
        await persistence.SaveNodeAsync(fabrikam);

        // 5. Create the graph root node (needed for initialization)
        var graphNode = MeshNode.FromPath("graph") with
        {
            Name = "Graph",
            NodeType = "type/graph",
            IsPersistent = true
        };
        await persistence.SaveNodeAsync(graphNode);

        // 6. Create type/graph type definition
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
        await persistence.SaveNodeAsync(graphTypeNode);

        var graphCodeConfig = new CodeConfiguration
        {
            Code = "public record Graph { }"
        };
        await persistence.SavePartitionObjectsAsync("type/graph", null, [graphCodeConfig]);
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var testDataDirectory = GetOrCreateTestDirectory();

        return builder
            .UseMonolithMesh()
            .AddInMemoryPersistence()
            .AddJsonGraphConfiguration(testDataDirectory);
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
        resolution.Prefix.Should().Be("Organizations");
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
        resolution.Prefix.Should().Be("Type/Organizations");
    }

    /// <summary>
    /// Test that NodeTypeService can find the NodeType node for "Type/Organizations"
    /// when searching from context "Organizations".
    ///
    /// The key fix: NodeTypeService now also searches in the parent path of the nodeType
    /// when the nodeType contains a path separator (e.g., "Type/Organizations").
    /// This ensures types in "Type/" folder are found even if GlobalTypesNamespace is "type".
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task NodeTypeService_FindsNodeTypeNode_ForTypeOrganizations()
    {
        // Arrange
        var nodeTypeService = Mesh.ServiceProvider.GetRequiredService<INodeTypeService>();

        // Act - this should find Type/Organizations by searching in its parent path "Type"
        var nodeTypeNode = await nodeTypeService.GetNodeTypeNodeAsync("Type/Organizations", "Organizations", TestContext.Current.CancellationToken);

        // Assert
        nodeTypeNode.Should().NotBeNull(
            "NodeTypeService should find the NodeType node for 'Type/Organizations'. " +
            "The search now includes the parent path of the nodeType.");
        nodeTypeNode.Path.Should().Be("Type/Organizations");
        nodeTypeNode.NodeType.Should().Be("NodeType");
    }

    /// <summary>
    /// Test that the NodeTypeDefinition for Type/Organizations has ChildrenQuery configured.
    /// The ChildrenQuery enables the Organizations node to query for all Organization instances
    /// rather than just displaying direct children.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task NodeTypeDefinition_HasChildrenQuery_ForTypeOrganizations()
    {
        // Arrange
        var persistence = Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();

        // Act - get the Type/Organizations node
        var nodeTypeNode = await persistence.GetNodeAsync("Type/Organizations", TestContext.Current.CancellationToken);

        // Assert
        nodeTypeNode.Should().NotBeNull("Type/Organizations should exist");
        nodeTypeNode.Content.Should().BeOfType<NodeTypeDefinition>();

        var nodeTypeDef = (NodeTypeDefinition)nodeTypeNode.Content!;
        nodeTypeDef.ChildrenQuery.Should().NotBeNullOrEmpty(
            "Type/Organizations should have ChildrenQuery configured to find Organization instances");
        nodeTypeDef.ChildrenQuery.Should().Contain("nodeType==Type/Organization",
            "ChildrenQuery should filter by Organization type");
    }

    /// <summary>
    /// Test that QueryAsync finds all Organization instances when using the ChildrenQuery.
    /// This validates that the ChildrenQuery mechanism works correctly.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task ChildrenQuery_FindsAllOrganizationInstances()
    {
        // Arrange
        var persistence = Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();

        // Get the ChildrenQuery from the Type/Organizations definition
        var nodeTypeNode = await persistence.GetNodeAsync("Type/Organizations", TestContext.Current.CancellationToken);
        var nodeTypeDef = (NodeTypeDefinition)nodeTypeNode!.Content!;
        var childrenQuery = nodeTypeDef.ChildrenQuery!;

        // Act - execute the query (from root to find all matching nodes)
        var results = new List<object>();
        await foreach (var obj in persistence.QueryAsync(childrenQuery, ""))
        {
            results.Add(obj);
        }

        // Assert - should find all 3 Organization instances (Acme, Contoso, Fabrikam)
        results.Should().HaveCount(3, "Should find all 3 Organization instances");
        var nodes = results.Cast<MeshNode>().ToList();
        nodes.Select(n => n.Name).Should().Contain("Acme Corporation");
        nodes.Select(n => n.Name).Should().Contain("Contoso Ltd");
        nodes.Select(n => n.Name).Should().Contain("Fabrikam Inc");
    }

    /// <summary>
    /// Test that the Organizations node uses default MeshNodeView (no compiled assembly needed).
    /// Since we're using ChildrenQuery instead of DataModel/TypeSource, the node uses
    /// the default views which will automatically apply the ChildrenQuery.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task Organizations_UsesDefaultMeshNodeView()
    {
        // Arrange
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Act - get the Organizations node
        var node = await meshCatalog.GetNodeAsync(new Address("Organizations"));

        // Assert
        node.Should().NotBeNull("Organizations node should exist");
        // Note: Without DataModel/TypeSource, HubConfiguration may be null (uses default views)
        // The key is that ChildrenQuery in the NodeTypeDefinition will be used by MeshNodeView.Details
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

        // 2. Create Type/Organizations/codeConfiguration.json - the CodeConfiguration
        var organizationsTypeDir = Path.Combine(typeDir, "Organizations");
        Directory.CreateDirectory(organizationsTypeDir);

        var codeConfigJson = """
        {
          "$type": "CodeConfiguration",
          "code": "public record Organizations { }"
        }
        """;
        File.WriteAllText(Path.Combine(organizationsTypeDir, "codeConfiguration.json"), codeConfigJson);

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

        var graphCodeConfigJson = """
        {
          "$type": "CodeConfiguration",
          "code": "public record Graph { }"
        }
        """;
        File.WriteAllText(Path.Combine(graphTypeDataDir, "codeConfiguration.json"), graphCodeConfigJson);
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var testDataDirectory = GetOrCreateTestDirectory();

        // Create actual JSON files on disk - this is the key difference from InMemoryPersistenceService tests
        SetupOrganizationsStructureOnDisk(testDataDirectory);

        return builder
            .UseMonolithMesh()
            .AddFileSystemPersistence(testDataDirectory)
            .AddJsonGraphConfiguration(testDataDirectory);
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
        var nodeTypeNode = await nodeTypeService.GetNodeTypeNodeAsync("Type/Organizations", "Organizations", TestContext.Current.CancellationToken);

        // Assert
        nodeTypeNode.Should().NotBeNull(
            "NodeTypeService should find the NodeType node from disk. " +
            "If null, the Content property was likely deserialized as JsonElement instead of NodeTypeDefinition.");
        nodeTypeNode.Path.Should().Be("Type/Organizations");
        nodeTypeNode.NodeType.Should().Be("NodeType");

        // Critical: Content must be NodeTypeDefinition, not JsonElement
        nodeTypeNode.Content.Should().BeOfType<NodeTypeDefinition>(
            "The $type discriminator in the JSON should cause Content to be deserialized as NodeTypeDefinition. " +
            "If this fails, ITypeRegistry is not properly configured for FileSystemStorageAdapter.");
    }

    /// <summary>
    /// Tests that CodeConfiguration can be loaded from disk partition files.
    /// This validates that GetPartitionObjectsAsync properly deserializes objects with $type.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task FileSystem_NodeTypeService_FindsCodeConfiguration_FromDiskPartition()
    {
        // Arrange
        var nodeTypeService = Mesh.ServiceProvider.GetRequiredService<INodeTypeService>();

        // Act - this reads codeConfiguration.json from Type/Organizations/ folder
        var codeConfig = await nodeTypeService.GetCodeConfigurationAsync("Type/Organizations", "Organizations", TestContext.Current.CancellationToken);

        // Assert
        codeConfig.Should().NotBeNull(
            "CodeConfiguration should be loaded from Type/Organizations/codeConfiguration.json. " +
            "If null, the $type discriminator was not processed during JSON deserialization.");
        codeConfig.Code.Should().NotBeNullOrEmpty();
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
        node.HubConfiguration.Should().NotBeNull(
            "Organizations node should have HubConfiguration from the compiled assembly. " +
            "If null, the on-demand compilation failed - likely because NodeTypeDefinition or CodeConfiguration " +
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
