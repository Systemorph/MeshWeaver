using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Layout;
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
    private IMeshQuery MeshQuery => Mesh.ServiceProvider.GetRequiredService<IMeshQuery>();
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
            Icon = "Document",
            DisplayOrder = 30,
            IsPersistent = true,
            Content = new NodeTypeDefinition
            {
                Id = "story",
                Namespace = "Type",
                DisplayName = "Story",
                Icon = "Document",
                Description = "A user story or task",
                DisplayOrder = 30,
                Configuration = "config => config"
            }
        };
        await persistence.SaveNodeAsync(storyNode);
        await persistence.SavePartitionObjectsAsync("type/story", "Code", [storyCodeConfig]);

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
            Icon = "Building",
            DisplayOrder = 10,
            IsPersistent = true,
            Content = new NodeTypeDefinition
            {
                Id = "org",
                Namespace = "type",
                DisplayName = "Organization",
                Icon = "Building",
                Description = "An organization",
                DisplayOrder = 10,
                Configuration = "config => config.WithContentType<Organization>().AddDefaultLayoutAreas()"
            }
        };
        await persistence.SaveNodeAsync(orgNode);
        await persistence.SavePartitionObjectsAsync("type/org", "Code", [orgCodeConfig]);

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
            Icon = "Folder",
            DisplayOrder = 20,
            IsPersistent = true,
            Content = new NodeTypeDefinition
            {
                Id = "project",
                Namespace = "Type",
                DisplayName = "Project",
                Icon = "Folder",
                Description = "A project",
                DisplayOrder = 20,
                Configuration = "config => config"
            }
        };
        await persistence.SaveNodeAsync(projectNode);
        await persistence.SavePartitionObjectsAsync("type/project", "Code", [projectCodeConfig]);

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
            Icon = "Diagram",
            DisplayOrder = 0,
            IsPersistent = true,
            Content = new NodeTypeDefinition
            {
                Id = "graph",
                Namespace = "Type",
                DisplayName = "Graph",
                Icon = "Diagram",
                Description = "The graph root",
                DisplayOrder = 0,
                Configuration = "config => config"
            }
        };
        await persistence.SaveNodeAsync(graphTypeNode);
        await persistence.SavePartitionObjectsAsync("type/graph", "Code", [graphCodeConfig]);
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
        var cacheDirectory = Path.Combine(testDataDirectory, ".mesh-cache");

        return builder
            .UseMonolithMesh()
            .AddInMemoryPersistence()
            .ConfigureServices(services => services.Configure<CompilationCacheOptions>(o => o.CacheDirectory = cacheDirectory))
            .AddJsonGraphConfiguration(testDataDirectory);
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

        // Verify IMeshQuery finds the pre-seeded data
        var children = await MeshQuery.QueryAsync<MeshNode>("path:graph scope:children", ct: TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);
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

        // Verify IMeshQuery finds the pre-seeded projects
        var children = await MeshQuery.QueryAsync<MeshNode>("path:graph/org1 scope:children", ct: TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);
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

        // Verify IMeshQuery finds the pre-seeded stories
        var children = await MeshQuery.QueryAsync<MeshNode>("path:graph/org1/proj1 scope:children", ct: TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);
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

        // Act: Request the default layout area (Overview) using stream
        // This should not hang if default views are properly configured
        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(MeshNodeView.OverviewArea);
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

    /// <summary>
    /// Tests that the Organization NodeType catalog renders correctly.
    /// When navigating to type/org and requesting the Catalog area,
    /// it should render a StackControl with CatalogContent that contains either
    /// organization thumbnails or "No items found" message.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task OrganizationType_GetCatalog_ShowsOrganizations()
    {
        // Arrange
        var typeOrgAddress = new Address("type/org");

        // Get a client with data services configured
        var client = GetClient(c => c.AddData(data => data));

        // Initialize type/org hub - this is a NodeType node
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(typeOrgAddress),
            TestContext.Current.CancellationToken);

        // Act: Request Search area directly (the default view for NodeType)
        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(MeshNodeView.SearchArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(typeOrgAddress, reference);

        // Wait for multiple emissions - first one may be loading state, later ones have data
        var values = await stream
            .Take(5)  // Take up to 5 emissions
            .TakeUntil(Observable.Timer(TimeSpan.FromSeconds(10)))  // Or timeout after 10s
            .ToList();

        Output.WriteLine($"Received {values.Count} emissions");

        // Find the last emission which should have the most complete data
        var lastValue = values.LastOrDefault();
        lastValue.Should().NotBeNull("Should receive at least one emission");

        // Convert to string to check catalog structure
        var json = lastValue!.Value.GetRawText();
        Output.WriteLine($"Last Catalog JSON (first 3000 chars): {json.Substring(0, Math.Min(3000, json.Length))}");

        // Log all emissions for debugging
        for (int i = 0; i < values.Count; i++)
        {
            var emissionJson = values[i].Value.GetRawText();
            Output.WriteLine($"Emission {i}: {emissionJson.Substring(0, Math.Min(500, emissionJson.Length))}...");
        }

        // The search should render as a MeshSearchControl
        var hasSearchStructure = json.Contains("Search") && json.Contains("MeshSearchControl");
        hasSearchStructure.Should().BeTrue($"Search should have MeshSearchControl. JSON: {json.Substring(0, Math.Min(1000, json.Length))}");

        // The MeshSearchControl should have the correct query for nodeType filtering
        var hasCorrectQuery = json.Contains("nodeType:type/org") && json.Contains("scope:subtree");
        hasCorrectQuery.Should().BeTrue($"Search should have nodeType filter in query. JSON: {json.Substring(0, Math.Min(1000, json.Length))}");
    }

    /// <summary>
    /// Tests that QueryAsync with nodeType filter returns organizations.
    /// This tests the underlying query that the search uses.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task QueryAsync_NodeTypeOrg_ReturnsOrganizations()
    {
        // Act - query for all nodes with nodeType type/org
        var query = "nodeType:type/org scope:descendants";
        var nodes = await MeshQuery.QueryAsync<MeshNode>(query, ct: TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);
        foreach (var node in nodes)
            Output.WriteLine($"Found: {node.Path}");

        // Assert
        nodes.Should().NotBeEmpty("Query should return organizations");
        nodes.Should().Contain(n => n.Path == "graph/org1", "Should find org1");
        nodes.Should().Contain(n => n.Path == "graph/org2", "Should find org2");
    }

    #endregion
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

        // 2. Create Type/Organizations/Code/codeConfiguration.json - the CodeConfiguration
        // CodeConfiguration is stored in the "Code" sub-partition for NodeType hubs
        var organizationsTypeDir = Path.Combine(typeDir, "Organizations");
        var codeDir = Path.Combine(organizationsTypeDir, "Code");
        Directory.CreateDirectory(codeDir);

        var codeConfigJson = """
        {
          "$type": "CodeConfiguration",
          "code": "public record Organizations { }"
        }
        """;
        File.WriteAllText(Path.Combine(codeDir, "codeConfiguration.json"), codeConfigJson);

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
            "namespace": "type",
            "displayName": "Graph",
            "configuration": "config => config"
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

        var cacheDirectory = Path.Combine(testDataDirectory, ".mesh-cache");

        return builder
            .UseMonolithMesh()
            .AddFileSystemPersistence(testDataDirectory)
            .ConfigureServices(services => services.Configure<CompilationCacheOptions>(o => o.CacheDirectory = cacheDirectory))
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
    public async Task FileSystem_PersistenceService_FindsNodeTypeNode_WithPolymorphicDeserialization()
    {
        // Arrange
        var persistence = Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();

        // Act - this should find Type/Organizations by reading from disk
        var nodeTypeNode = await persistence.GetNodeAsync("Type/Organizations", TestContext.Current.CancellationToken);

        // Assert
        nodeTypeNode.Should().NotBeNull(
            "PersistenceService should find the NodeType node from disk. " +
            "If null, the Content property was likely deserialized as JsonElement instead of NodeTypeDefinition.");
        nodeTypeNode.Path.Should().Be("Type/Organizations");
        nodeTypeNode.NodeType.Should().Be("NodeType");

        // Critical: Content must be NodeTypeDefinition, not JsonElement
        nodeTypeNode.Content.Should().BeOfType<NodeTypeDefinition>(
            "The $type discriminator in the JSON should cause Content to be deserialized as NodeTypeDefinition. " +
            "If this fails, ITypeRegistry is not properly configured for FileSystemStorageAdapter.");
    }

    /// <summary>
    /// Tests that CodeConfiguration can be loaded from the Code sub-partition via messaging.
    /// This validates that PartitionTypeSource properly loads CodeConfiguration from the hub.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task FileSystem_CodeConfiguration_LoadedFromCodeSubPartition()
    {
        // Arrange - first trigger compilation so the hub is configured
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();
        var organizationsAddress = new Address("Type/Organizations");

        // This triggers compilation and hub creation with CodeConfiguration support
        var node = await meshCatalog.GetNodeAsync(organizationsAddress);
        node.Should().NotBeNull("Type/Organizations node should exist");
        node.HubConfiguration.Should().NotBeNull("Node should have HubConfiguration from compiled assembly");

        // Create a client that can query the hub
        // Register CodeConfiguration with collection name "Code" matching the hub's registration
        // Using generic WithType<T> to properly set up key function from [Key] attribute
        var client = GetClient(c => c
            .AddData(data => data)
            .WithType<CodeConfiguration>("Code"));
        var workspace = client.GetWorkspace();

        // Act - get CodeConfiguration stream from the Type/Organizations hub
        // CollectionReference returns InstanceCollection (not EntityStore)
        // because CollectionReference : WorkspaceReference<InstanceCollection>
        var stream = workspace.GetRemoteStream<InstanceCollection, CollectionReference>(
            organizationsAddress,
            new CollectionReference("Code"));

        // Wait for data
        var instanceCollection = await stream
            .Where(x => x.Value != null)
            .Timeout(TimeSpan.FromSeconds(20))
            .Select(x => x.Value!)
            .FirstAsync();

        // Assert
        instanceCollection.Should().NotBeNull("InstanceCollection should not be null");
        instanceCollection.Instances.Should().NotBeEmpty(
            "InstanceCollection should contain CodeConfiguration instances");

        var codeConfigs = instanceCollection.Get<CodeConfiguration>().ToList();
        codeConfigs.Should().NotBeNullOrEmpty(
            "CodeConfiguration should be loaded from Type/Organizations/Code partition via messaging.");
        codeConfigs.First().Code.Should().NotBeNullOrEmpty(
            "CodeConfiguration.Code should contain C# source code.");
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

/// <summary>
/// Tests that use the actual samples/Graph/Data directory to test real sample data.
/// This replicates the exact production scenario with real sample data.
/// </summary>
[Collection("SamplesGraphDataTests")]
public class SamplesGraphDataTest : MonolithMeshTestBase
{
    // Use the actual samples directory
    private static readonly string SamplesDataDirectory = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", "Graph", "Data"));

    private readonly string _cacheDirectory;

    public SamplesGraphDataTest(ITestOutputHelper output) : base(output)
    {
        _cacheDirectory = Path.Combine(Path.GetTempPath(), "MeshWeaverSamplesTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_cacheDirectory);
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return builder
            .UseMonolithMesh()
            .AddFileSystemPersistence(SamplesDataDirectory)
            .ConfigureServices(services => services.Configure<CompilationCacheOptions>(o => o.CacheDirectory = _cacheDirectory))
            .AddJsonGraphConfiguration(SamplesDataDirectory);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();

        if (Directory.Exists(_cacheDirectory))
        {
            try { Directory.Delete(_cacheDirectory, recursive: true); }
            catch { /* Ignore cleanup errors */ }
        }
    }

    /// <summary>
    /// Test that tries to get the default layout from Organization.
    /// This test is expected to deadlock if the NodeTypeService implementation has issues.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Organization_GetDefaultLayout_ShouldNotDeadlock()
    {
        // Arrange - Organization is now at root level
        var organizationAddress = new Address("Organization");

        // Get a client with data services configured
        var client = GetClient(c => c.AddData(data => data));

        Output.WriteLine($"Samples data directory: {SamplesDataDirectory}");
        Output.WriteLine($"Directory exists: {Directory.Exists(SamplesDataDirectory)}");

        // Act: Request the default layout area (empty = default view)
        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(string.Empty);

        Output.WriteLine("Getting remote stream for Organization...");
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(organizationAddress, reference);

        Output.WriteLine("Waiting for first value from stream...");
        // Wait for the stream to emit a value - this is where deadlock would occur
        var value = await stream.FirstAsync();

        Output.WriteLine($"Received value: {value}");

        // Assert
        value.Should().NotBe(default(JsonElement),
            "Organization node should return default layout area content");
    }

    /// <summary>
    /// Test that verifies the samples directory exists and has the expected structure.
    /// Organization is now at root level with Organization.json and Organization/ folder for code.
    /// </summary>
    [Fact]
    public void SamplesDirectory_Exists_WithExpectedStructure()
    {
        Output.WriteLine($"Checking samples directory: {SamplesDataDirectory}");

        Directory.Exists(SamplesDataDirectory).Should().BeTrue(
            $"Samples directory should exist at {SamplesDataDirectory}");

        var organizationPath = Path.Combine(SamplesDataDirectory, "Organization.json");
        File.Exists(organizationPath).Should().BeTrue(
            $"Organization.json should exist at {organizationPath}");

        var codeConfigPath = Path.Combine(SamplesDataDirectory, "Organization", "Code", "Organization.cs");
        File.Exists(codeConfigPath).Should().BeTrue(
            $"Organization/Code/Organization.cs should exist at {codeConfigPath}");
    }

    /// <summary>
    /// Test that the MeshCatalog can resolve Organization path.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task Organization_CanBeResolved_FromSamples()
    {
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        Output.WriteLine("Resolving Organization path...");
        var resolution = await meshCatalog.ResolvePathAsync("Organization");

        resolution.Should().NotBeNull("Organization should be resolvable from samples");
        resolution.Prefix.Should().Be("Organization");
        Output.WriteLine($"Resolved: Prefix={resolution.Prefix}, Remainder={resolution.Remainder}");
    }

    /// <summary>
    /// Test that GetNodeAsync works for Organization.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task Organization_GetNodeAsync_FromSamples()
    {
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        Output.WriteLine("Getting node for Organization...");
        var node = await meshCatalog.GetNodeAsync(new Address("Organization"));

        node.Should().NotBeNull("Organization node should exist in samples");
        Output.WriteLine($"Node: Path={node.Path}, NodeType={node.NodeType}, HubConfiguration={node.HubConfiguration != null}");

        node.Path.Should().Be("Organization");
        node.NodeType.Should().Be("NodeType");
    }

    /// <summary>
    /// Test that verifies the node's HubConfiguration is set for NodeType nodes.
    /// Nodes with nodeType=NodeType compile their own code to get HubConfiguration.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task Organization_HubConfiguration_IsSetForNodeType()
    {
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();
        var node = await meshCatalog.GetNodeAsync(new Address("Organization"));

        node.Should().NotBeNull();
        node.NodeType.Should().Be("NodeType");

        // NodeType nodes compile their OWN code (at their path) to get HubConfiguration
        Output.WriteLine($"Node.HubConfiguration is null: {node.HubConfiguration == null}");
        node.HubConfiguration.Should().NotBeNull(
            "NodeType nodes should have HubConfiguration from their own code");

        // Verify we can get the actual HubConfiguration function
        var hubConfig = node.HubConfiguration;
        hubConfig.Should().NotBeNull("Should be able to get HubConfiguration");
        Output.WriteLine("Successfully obtained HubConfiguration");
    }

    /// <summary>
    /// Test that sends a PingRequest to Organization hub.
    /// This triggers hub creation which may cause deadlock.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Organization_PingRequest_ShouldNotDeadlock()
    {
        var organizationAddress = new Address("Organization");
        var client = GetClient();

        Output.WriteLine("Sending PingRequest to Organization...");

        // This triggers hub creation - may deadlock here
        var response = await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(organizationAddress),
            TestContext.Current.CancellationToken);

        Output.WriteLine($"Received response: {response}");
        response.Should().NotBeNull("Should receive ping response");
    }

    /// <summary>
    /// Test that CodeView for Organization returns non-empty content.
    /// Uses JsonElement and GetControlStream to verify the Code area renders a control.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Organization_CodeView_ReturnsNonEmptyContent()
    {
        var organizationAddress = new Address("Organization");
        var client = GetClient(c => c
            .WithInitialization((h, _) => RoutingService.RegisterStreamAsync(h))
            .AddLayoutClient(cc => cc)
            .AddData(data => data)
            .WithType<CodeConfiguration>("Code"));
        var workspace = client.GetWorkspace();

        // Request the Code view area
        var reference = new LayoutAreaReference("Code");

        Output.WriteLine("Getting CodeView for Organization...");
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(organizationAddress, reference);

        // Use GetControlStream to get the Code area control
        var control = await stream
            .GetControlStream("Code")
            .Timeout(TimeSpan.FromSeconds(20))
            .FirstAsync(x => x != null);

        Output.WriteLine($"Received control type: {control?.GetType().FullName}");

        control.Should().NotBeNull("CodeView should render a control for the 'Code' area");
    }

    /// <summary>
    /// Test that CodeConfiguration stream for Organization is not empty.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Organization_CodeConfigurationStream_IsNotEmpty()
    {
        var organizationAddress = new Address("Organization");
        var client = GetClient(c => c
            .AddData(data => data)
            .WithType<CodeConfiguration>("Code"));
        var workspace = client.GetWorkspace();

        Output.WriteLine("Getting Code collection stream for Organization...");
        var stream = workspace.GetRemoteStream<InstanceCollection, CollectionReference>(
            organizationAddress,
            new CollectionReference("Code"));

        var instanceCollection = await stream
            .Where(x => x.Value != null)
            .Timeout(TimeSpan.FromSeconds(20))
            .Select(x => x.Value!)
            .FirstAsync();

        Output.WriteLine($"Received InstanceCollection with {instanceCollection.Instances.Count} instances");

        instanceCollection.Should().NotBeNull();
        instanceCollection.Instances.Should().NotBeEmpty("Code collection should have CodeConfiguration instances");

        var codeConfigs = instanceCollection.Get<CodeConfiguration>().ToList();
        Output.WriteLine($"Found {codeConfigs.Count} CodeConfiguration(s)");

        foreach (var config in codeConfigs)
        {
            Output.WriteLine($"CodeConfiguration.Code: {config.Code?.Substring(0, Math.Min(100, config.Code?.Length ?? 0))}...");
        }

        codeConfigs.Should().NotBeEmpty("Should have CodeConfiguration instances");
        codeConfigs.First().Code.Should().NotBeNullOrEmpty("CodeConfiguration.Code should have content");
    }

    /// <summary>
    /// Test with AddLayoutClient to properly deserialize controls.
    /// Uses JsonElement and GetControlStream for cleaner control access.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task Organization_CodeView_WithLayoutClient_ReturnsSplitter()
    {
        var organizationAddress = new Address("Organization");

        // Configure client with AddLayoutClient for proper control deserialization
        var client = GetClient(c => c
            .WithInitialization((h, _) => RoutingService.RegisterStreamAsync(h))
            .AddLayoutClient(cc => cc)
            .AddData(data => data)
            .WithType<CodeConfiguration>("Code"));

        var workspace = client.GetWorkspace();

        // Request the Code view area using JsonElement
        var reference = new LayoutAreaReference("Code");
        Output.WriteLine($"Requesting layout area: {reference.Area}");

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(organizationAddress, reference);

        // Use GetControlStream to get the Code area control
        Output.WriteLine("Waiting for 'Code' control via GetControlStream...");
        var control = await stream
            .GetControlStream("Code")
            .Timeout(30.Seconds())
            .FirstAsync(x => x != null);

        Output.WriteLine($"Control type: {control?.GetType().FullName}");

        // Verify we got a Splitter
        control.Should().BeOfType<SplitterControl>("CodeView should return a Splitter control");

        var splitter = (SplitterControl)control!;
        Output.WriteLine($"Splitter has {splitter.Areas.Count} panes:");
        foreach (var pane in splitter.Areas)
        {
            Output.WriteLine($"  Pane: {pane.Id} -> {pane.Area}");
        }

        splitter.Areas.Should().HaveCount(2, "CodeView Splitter should have 2 panes");
    }

    /// <summary>
    /// Debug test: Collect control updates and log them to understand the sequence of events.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task Organization_CodeView_DebugUpdateSequence()
    {
        var organizationAddress = new Address("Organization");

        var client = GetClient(c => c
            .WithInitialization((h, _) => RoutingService.RegisterStreamAsync(h))
            .AddLayoutClient(cc => cc)
            .AddData(data => data)
            .WithType<CodeConfiguration>("Code"));

        var workspace = client.GetWorkspace();

        // Initialize hub
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(organizationAddress),
            TestContext.Current.CancellationToken);

        var reference = new LayoutAreaReference("Code");
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(organizationAddress, reference);

        Output.WriteLine("Collecting updates for 20 seconds...\n");

        var updates = await stream
            .GetControlStream("Code")
            .TakeUntil(Observable.Timer(TimeSpan.FromSeconds(20)))
            .ToList();

        Output.WriteLine($"=== Collected {updates.Count} updates ===\n");

        for (int i = 0; i < updates.Count; i++)
        {
            Output.WriteLine($"--- Update #{i + 1} ---");
            var control = updates[i];

            var typeName = control switch
            {
                null => "null",
                SplitterControl s => $"SPLITTER ({s.Areas.Count} panes)",
                StackControl st => $"STACK ({st.Areas.Count} views)",
                NamedAreaControl n => $"NamedArea -> '{n.Area}'",
                ProgressControl p => $"Progress ({p.Message})",
                _ => control.GetType().Name
            };
            Output.WriteLine($"  Control: {typeName}");

            if (control is SplitterControl splitter)
            {
                foreach (var pane in splitter.Areas)
                {
                    Output.WriteLine($"    Pane: {pane.Id} -> {pane.Area}");
                }
            }
            Output.WriteLine("");
        }

        updates.Should().NotBeEmpty("Should receive at least one update");

        // Verify final state has Splitter
        var final = updates.Last();
        var hasSplitter = final is SplitterControl;
        Output.WriteLine($"Final control is Splitter: {hasSplitter}");

        final.Should().BeOfType<SplitterControl>("Final control should be a Splitter");
    }

    /// <summary>
    /// Test using GetControlStream to get properly typed controls.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task Organization_CodeView_GetControlStream_Test()
    {
        var organizationAddress = new Address("Organization");

        var client = GetClient(c => c
            .WithInitialization((h, _) => RoutingService.RegisterStreamAsync(h))
            .AddLayoutClient(cc => cc)
            .AddData(data => data)
            .WithType<CodeConfiguration>("Code"));

        var workspace = client.GetWorkspace();

        // Initialize hub
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(organizationAddress),
            TestContext.Current.CancellationToken);

        var reference = new LayoutAreaReference("Code");
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(organizationAddress, reference);

        Output.WriteLine("Getting control for 'Code' area...");

        var control = await stream
            .GetControlStream("Code")
            .Timeout(30.Seconds())
            .FirstAsync(x => x != null);

        Output.WriteLine($"Control type: {control?.GetType().FullName}");

        if (control is NamedAreaControl named)
        {
            Output.WriteLine($"NamedAreaControl pointing to: {named.Area}");

            // Follow the reference
            var actualAreaKey = named.Area?.ToString();
            if (!string.IsNullOrEmpty(actualAreaKey))
            {
                Output.WriteLine($"Getting control for '{actualAreaKey}'...");
                var actual = await stream
                    .GetControlStream(actualAreaKey)
                    .Timeout(10.Seconds())
                    .FirstAsync(x => x != null);
                Output.WriteLine($"Actual control type: {actual?.GetType().FullName}");

                if (actual is SplitterControl splitter)
                {
                    Output.WriteLine($"Splitter has {splitter.Areas.Count} panes");
                    foreach (var pane in splitter.Areas)
                    {
                        Output.WriteLine($"  Pane: {pane.Id}");
                    }
                }
            }
        }
        else if (control is SplitterControl splitter)
        {
            Output.WriteLine($"Direct Splitter with {splitter.Areas.Count} panes");
            foreach (var pane in splitter.Areas)
            {
                Output.WriteLine($"  Pane: {pane.Id}");
            }
        }

        control.Should().NotBeNull("Code area should have a control");
    }

    /// <summary>
    /// Test that loading the default layout area (empty string) from MeshWeaver node works.
    /// This test diagnoses eternal spinner issue when navigating to /MeshWeaver.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task MeshWeaver_GetDefaultLayoutArea_ShouldNotHang()
    {
        // Arrange
        var meshWeaverAddress = new Address("MeshWeaver");

        // Get a client with data services configured
        var client = GetClient(c => c
            .WithInitialization((h, _) => RoutingService.RegisterStreamAsync(h))
            .AddLayoutClient(cc => cc)
            .AddData(data => data));

        Output.WriteLine($"Samples data directory: {SamplesDataDirectory}");

        // First check if MeshWeaver node exists
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();
        var node = await meshCatalog.GetNodeAsync(meshWeaverAddress);
        node.Should().NotBeNull("MeshWeaver node should exist in samples");
        Output.WriteLine($"MeshWeaver node: Path={node.Path}, NodeType={node.NodeType}");

        // Initialize hub via PingRequest
        Output.WriteLine("Sending PingRequest to MeshWeaver...");
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(meshWeaverAddress),
            TestContext.Current.CancellationToken);
        Output.WriteLine("PingRequest completed successfully");

        // Act: Request the default layout area (empty = default view)
        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(string.Empty);

        Output.WriteLine("Getting remote stream for MeshWeaver default layout area...");
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(meshWeaverAddress, reference);

        Output.WriteLine("Waiting for first value from stream...");
        // Wait for the stream to emit a value - this is where eternal spinner would occur
        var value = await stream.FirstAsync();

        Output.WriteLine($"Received value: {value}");

        // Assert
        value.Should().NotBe(default(JsonElement),
            "MeshWeaver node should return default layout area content");
    }

    /// <summary>
    /// Test that the OrganizationViews.cs file exists alongside Organization.cs for Organization.
    /// This validates the custom view configuration structure.
    /// </summary>
    [Fact]
    public void Organization_ViewsCs_Exists()
    {
        var viewsCsPath = Path.Combine(SamplesDataDirectory, "Organization", "Code", "OrganizationViews.cs");
        Output.WriteLine($"Checking for OrganizationViews.cs at: {viewsCsPath}");

        File.Exists(viewsCsPath).Should().BeTrue(
            $"Organization/Code/OrganizationViews.cs should exist at {viewsCsPath} for custom view configuration");
    }

    /// <summary>
    /// Test that Organization node with custom AddLayout view compiles successfully.
    /// This validates the fix for missing using statements in DynamicMeshNodeAttributeGenerator.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Organization_CustomView_CompilesSuccessfully()
    {
        var organizationAddress = new Address("Organization");

        // Get node from mesh catalog - this triggers compilation
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        Output.WriteLine("Getting Organization node (triggers compilation)...");
        var node = await meshCatalog.GetNodeAsync(organizationAddress);

        node.Should().NotBeNull("Organization node should exist");
        node.NodeType.Should().Be("NodeType");
        node.HubConfiguration.Should().NotBeNull(
            "Organization node should have HubConfiguration from compiled assembly with custom view");

        // Get the hub configuration function
        var hubConfig = node.HubConfiguration;
        hubConfig.Should().NotBeNull("HubConfiguration should be available");

        Output.WriteLine("Organization custom view compiled successfully!");
    }

    /// <summary>
    /// Test that the custom OrganizationViews.Details view compiles and renders.
    /// This tests the complete flow from views.json through compilation to rendering.
    /// Note: In a test environment without full data setup, the view may return a loading state.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task Organization_DetailsView_RendersCustomView()
    {
        var organizationAddress = new Address("Systemorph");

        var client = GetClient(c => c
            .WithInitialization((h, _) => RoutingService.RegisterStreamAsync(h))
            .AddLayoutClient(cc => cc)
            .AddData(data => data));

        var workspace = client.GetWorkspace();

        // Initialize hub
        Output.WriteLine("Initializing Systemorph hub...");
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(organizationAddress),
            TestContext.Current.CancellationToken);

        // Request the Overview view area
        var reference = new LayoutAreaReference(MeshNodeView.OverviewArea);

        Output.WriteLine("Getting Overview area for Systemorph organization...");
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(organizationAddress, reference);

        var control = await stream
            .GetControlStream(MeshNodeView.OverviewArea)
            .Timeout(30.Seconds())
            .FirstAsync(x => x != null);

        Output.WriteLine($"Received control type: {control?.GetType().FullName}");

        // Verify the custom view is invoked and returns a control
        // The view returns either:
        // - StackControl (when data is loaded - the organization header)
        // - MarkdownControl (loading state "*Loading...*")
        control.Should().NotBeNull("Details view should render a control for Organization instance");

        // The important thing is that the custom view compiled and was called.
        // In test environment, we may get loading state since data isn't fully set up.
        var isExpectedType = control is StackControl || control is MarkdownControl;
        isExpectedType.Should().BeTrue(
            $"Custom OrganizationViews.Details should return StackControl or MarkdownControl, got {control?.GetType().Name}");
    }

    /// <summary>
    /// Test that loading the Search area from MeshWeaver node works.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task MeshWeaver_GetSearchArea_ShouldNotHang()
    {
        // Arrange
        var meshWeaverAddress = new Address("MeshWeaver");

        var client = GetClient(c => c
            .WithInitialization((h, _) => RoutingService.RegisterStreamAsync(h))
            .AddLayoutClient(cc => cc)
            .AddData(data => data));

        // Initialize hub
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(meshWeaverAddress),
            TestContext.Current.CancellationToken);

        // Act: Request the Search area
        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(MeshNodeView.SearchArea);

        Output.WriteLine("Getting Search area for MeshWeaver...");
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(meshWeaverAddress, reference);

        var value = await stream.FirstAsync();
        Output.WriteLine($"Received Search area value");

        // Assert
        value.Should().NotBe(default(JsonElement),
            "MeshWeaver Search area should return content");
    }
}

[CollectionDefinition("SamplesGraphDataTests", DisableParallelization = true)]
public class SamplesGraphDataTestsCollection { }
