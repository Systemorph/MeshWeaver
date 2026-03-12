using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Integration tests for dynamically configured Graph using JSON configuration.
/// Tests verify that the JSON-based configuration works end-to-end with real messages.
/// </summary>
/// <remarks>
/// These tests use AddGraph instead of InstallAssemblies.
/// The types (Story, Organization, Project) are compiled at runtime from JSON TypeSource.
/// </remarks>
[Collection("DynamicGraphIntegrationTests")]
public class DynamicGraphIntegrationTest : MonolithMeshTestBase
{
    // Each test instance gets its own unique directory
    private static readonly string TestDirectoryBase = Path.Combine(Path.GetTempPath(), "MeshWeaverDynamicGraphTests");
    private string? _testDirectory;

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
        await SetupTestConfigurationAsync(NodeFactory);
        await SeedHierarchyAsync(NodeFactory);
    }

    private static async Task SaveCodeAsChildNodeAsync(IMeshService nodeFactory, string nodeTypePath, CodeConfiguration codeConfig)
    {
        var codeNode = new MeshNode(codeConfig.Id ?? "code", $"{nodeTypePath}/_Source")
        {
            NodeType = "Code",
            Name = codeConfig.DisplayName ?? codeConfig.Id ?? "Code",
            Content = codeConfig
        };
        await nodeFactory.CreateNodeAsync(codeNode);
    }

    private static async Task SetupTestConfigurationAsync(IMeshService nodeFactory)
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
            Icon = "Document",
            Order = 30,
            Content = new NodeTypeDefinition
            {
                Description = "A user story or task",
                Configuration = "config => config"
            }
        };
        await nodeFactory.CreateNodeAsync(storyNode);
        await SaveCodeAsChildNodeAsync(nodeFactory, "type/story", storyCodeConfig);

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
            Icon = "Building",
            Order = 10,
            Content = new NodeTypeDefinition
            {
                Description = "An organization",
                Configuration = "config => config.WithContentType<Organization>().AddDefaultLayoutAreas()"
            }
        };
        await nodeFactory.CreateNodeAsync(orgNode);
        await SaveCodeAsChildNodeAsync(nodeFactory, "type/org", orgCodeConfig);

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
            Icon = "Folder",
            Order = 20,
            Content = new NodeTypeDefinition
            {
                Description = "A project",
                Configuration = "config => config"
            }
        };
        await nodeFactory.CreateNodeAsync(projectNode);
        await SaveCodeAsChildNodeAsync(nodeFactory, "type/project", projectCodeConfig);

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
            Icon = "Diagram",
            Order = 0,
            Content = new NodeTypeDefinition
            {
                Description = "The graph root",
                Configuration = "config => config"
            }
        };
        await nodeFactory.CreateNodeAsync(graphTypeNode);
        await SaveCodeAsChildNodeAsync(nodeFactory, "type/graph", graphCodeConfig);
    }

    private static async Task SeedHierarchyAsync(IMeshService nodeFactory)
    {
        // Pre-seed the hierarchy: graph -> org -> project -> story
        // NodeType uses full path to type definition (e.g., "type/graph", "type/org")
        await nodeFactory.CreateNodeAsync(MeshNode.FromPath("graph") with { Name = "Graph", NodeType = "type/graph" });
        await nodeFactory.CreateNodeAsync(MeshNode.FromPath("graph/org1") with { Name = "Organization 1", NodeType = "type/org" });
        await nodeFactory.CreateNodeAsync(MeshNode.FromPath("graph/org2") with { Name = "Organization 2", NodeType = "type/org" });
        await nodeFactory.CreateNodeAsync(MeshNode.FromPath("graph/org1/proj1") with { Name = "Project 1", NodeType = "type/project" });
        await nodeFactory.CreateNodeAsync(MeshNode.FromPath("graph/org1/proj2") with { Name = "Project 2", NodeType = "type/project" });
        await nodeFactory.CreateNodeAsync(MeshNode.FromPath("graph/org1/proj1/story1") with { Name = "Story 1", NodeType = "type/story" });
        await nodeFactory.CreateNodeAsync(MeshNode.FromPath("graph/org1/proj1/story2") with { Name = "Story 2", NodeType = "type/story" });
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var testDataDirectory = GetOrCreateTestDirectory();
        var cacheDirectory = Path.Combine(testDataDirectory, ".mesh-cache");

        return builder
            .UseMonolithMesh()
            .AddInMemoryPersistence()
            .ConfigureServices(services => services.Configure<CompilationCacheOptions>(o => o.CacheDirectory = cacheDirectory))
            .AddGraph();
    }


    #region Hub Initialization Tests

    [Fact(Timeout = 10000)]
    public async Task GraphHub_LoadsChildrenFromPersistence_AtInitialization()
    {
        var client = GetClient();
        var graphAddress = new Address("graph");

        // Initialize graph hub via ping
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(graphAddress),
            TestContext.Current.CancellationToken);

        // Verify IMeshService finds the pre-seeded data
        var children = await MeshQuery.QueryAsync<MeshNode>("namespace:graph", null, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);
        children.Should().HaveCount(2, "graph should have 2 org children pre-seeded");
        children.Should().Contain(n => n.Path == "graph/org1");
        children.Should().Contain(n => n.Path == "graph/org2");
    }

    [Fact(Timeout = 10000)]
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

        // Verify IMeshService finds the pre-seeded projects
        var children = await MeshQuery.QueryAsync<MeshNode>("namespace:graph/org1", null, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);
        children.Should().HaveCount(2, "org1 should have 2 project children pre-seeded");
        children.Should().Contain(n => n.Path == "graph/org1/proj1");
        children.Should().Contain(n => n.Path == "graph/org1/proj2");
    }

    [Fact(Timeout = 10000)]
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

        // Verify IMeshService finds the pre-seeded stories
        var children = await MeshQuery.QueryAsync<MeshNode>("namespace:graph/org1/proj1", null, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);
        children.Should().HaveCount(2, "proj1 should have 2 story children pre-seeded");
        children.Should().Contain(n => n.Path == "graph/org1/proj1/story1");
        children.Should().Contain(n => n.Path == "graph/org1/proj1/story2");
    }

    #endregion

    #region ResolvePath Tests

    [Fact(Timeout = 10000)]
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
        var resolution = await PathResolver.ResolvePathAsync("graph/org1");

        // Assert
        resolution.Should().NotBeNull("persistence has graph/org1");
        resolution.Prefix.Should().Be("graph/org1");
        resolution.Remainder.Should().BeNull();
    }

    [Fact(Timeout = 10000)]
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
        var resolution = await PathResolver.ResolvePathAsync("graph/org1/proj1/nonexistent/deep");

        // Assert: should match graph/org1/proj1 with remainder
        resolution.Should().NotBeNull();
        resolution.Prefix.Should().Be("graph/org1/proj1");
        resolution.Remainder.Should().Be("nonexistent/deep");
    }

    [Fact(Timeout = 10000)]
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
        var resolution = await PathResolver.ResolvePathAsync("graph/org1/proj1/story1");

        // Assert
        resolution.Should().NotBeNull();
        resolution.Prefix.Should().Be("graph/org1/proj1/story1");
        resolution.Remainder.Should().BeNull();
    }

    [Fact(Timeout = 10000)]
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
        var resolution = await PathResolver.ResolvePathAsync("graph/org1/proj1/story1/Overview");

        // Assert
        resolution.Should().NotBeNull();
        resolution.Prefix.Should().Be("graph/org1/proj1/story1");
        resolution.Remainder.Should().Be("Overview");
    }

    [Fact(Timeout = 10000)]
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
        var resolution = await PathResolver.ResolvePathAsync("nonexistent/path/here");

        // Assert
        resolution.Should().BeNull();
    }

    [Fact(Timeout = 10000)]
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
        var resolution = await PathResolver.ResolvePathAsync("graph/_Nodes");

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
        var pathResolver = Mesh.ServiceProvider.GetRequiredService<IPathResolver>();

        // Act
        var resolution = await pathResolver.ResolvePathAsync("type/graph");

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
        // Act
        var node = await MeshQuery.QueryAsync<MeshNode>("path:type/graph", ct: TestContext.Current.CancellationToken).FirstOrDefaultAsync(TestContext.Current.CancellationToken);

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
        var pathResolver = Mesh.ServiceProvider.GetRequiredService<IPathResolver>();

        // Act
        var resolution = await pathResolver.ResolvePathAsync(typePath);

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
        var graphType = await MeshQuery.QueryAsync<MeshNode>("path:type/graph", ct: TestContext.Current.CancellationToken).FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        graphType.Should().NotBeNull("type/graph should exist in persistence");
        graphType!.NodeType.Should().Be("NodeType");

        var orgType = await MeshQuery.QueryAsync<MeshNode>("path:type/org", ct: TestContext.Current.CancellationToken).FirstOrDefaultAsync(TestContext.Current.CancellationToken);
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
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("graph/movetest") with { Name = "Move Test", NodeType = "type/org" }, ct: TestContext.Current.CancellationToken);

        // Act
        var response = await Mesh.AwaitResponse<MoveNodeResponse>(new MoveNodeRequest("graph/movetest", "graph/movetest-renamed"), o => o, TestContext.Current.CancellationToken);
        var moved = response.Message.Node;

        // Assert
        moved.Should().NotBeNull();
        moved!.Path.Should().Be("graph/movetest-renamed");
        moved.Name.Should().Be("Move Test");

        var oldNode = await MeshQuery.QueryAsync<MeshNode>("path:graph/movetest", ct: TestContext.Current.CancellationToken).FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        oldNode.Should().BeNull("Original node should be deleted");

        var newNode = await MeshQuery.QueryAsync<MeshNode>("path:graph/movetest-renamed", ct: TestContext.Current.CancellationToken).FirstOrDefaultAsync(TestContext.Current.CancellationToken);
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
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("graph/parent") with { Name = "Parent", NodeType = "type/org" }, ct: TestContext.Current.CancellationToken);
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("graph/parent/child1") with { Name = "Child 1", NodeType = "type/project" }, ct: TestContext.Current.CancellationToken);
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("graph/parent/child2") with { Name = "Child 2", NodeType = "type/project" }, ct: TestContext.Current.CancellationToken);
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("graph/parent/child1/grandchild") with { Name = "Grandchild", NodeType = "type/story" }, ct: TestContext.Current.CancellationToken);

        // Act
        await Mesh.AwaitResponse<MoveNodeResponse>(new MoveNodeRequest("graph/parent", "graph/newparent"), o => o, TestContext.Current.CancellationToken);

        // Assert - old paths should not exist
        (await MeshQuery.QueryAsync<MeshNode>("path:graph/parent", ct: TestContext.Current.CancellationToken).FirstOrDefaultAsync(TestContext.Current.CancellationToken)).Should().BeNull();
        (await MeshQuery.QueryAsync<MeshNode>("path:graph/parent/child1", ct: TestContext.Current.CancellationToken).FirstOrDefaultAsync(TestContext.Current.CancellationToken)).Should().BeNull();
        (await MeshQuery.QueryAsync<MeshNode>("path:graph/parent/child2", ct: TestContext.Current.CancellationToken).FirstOrDefaultAsync(TestContext.Current.CancellationToken)).Should().BeNull();
        (await MeshQuery.QueryAsync<MeshNode>("path:graph/parent/child1/grandchild", ct: TestContext.Current.CancellationToken).FirstOrDefaultAsync(TestContext.Current.CancellationToken)).Should().BeNull();

        // Assert - new paths should exist with correct data
        var newParent = await MeshQuery.QueryAsync<MeshNode>("path:graph/newparent", ct: TestContext.Current.CancellationToken).FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        newParent.Should().NotBeNull();
        newParent!.Name.Should().Be("Parent");

        var newChild1 = await MeshQuery.QueryAsync<MeshNode>("path:graph/newparent/child1", ct: TestContext.Current.CancellationToken).FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        newChild1.Should().NotBeNull();
        newChild1!.Name.Should().Be("Child 1");

        var newChild2 = await MeshQuery.QueryAsync<MeshNode>("path:graph/newparent/child2", ct: TestContext.Current.CancellationToken).FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        newChild2.Should().NotBeNull();
        newChild2!.Name.Should().Be("Child 2");

        var newGrandchild = await MeshQuery.QueryAsync<MeshNode>("path:graph/newparent/child1/grandchild", ct: TestContext.Current.CancellationToken).FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        newGrandchild.Should().NotBeNull();
        newGrandchild!.Name.Should().Be("Grandchild");
    }

    /// <summary>
    /// Move node via MoveNodeRequest - verifies the node is moved to the new path.
    /// Note: Comment migration is handled internally by the persistence layer.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task MoveNodeAsync_MovesNodeViaRequest()
    {
        // Arrange - create node
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("graph/commented") with { Name = "Commented Node", NodeType = "type/org" }, ct: TestContext.Current.CancellationToken);

        // Act - move via MoveNodeRequest
        var response = await Mesh.AwaitResponse<MoveNodeResponse>(new MoveNodeRequest("graph/commented", "graph/commented-moved"), o => o, TestContext.Current.CancellationToken);

        // Assert - node should be at new path
        response.Message.Success.Should().BeTrue("Move should succeed");
        response.Message.Node.Should().NotBeNull();
        response.Message.Node!.Path.Should().Be("graph/commented-moved");

        var movedNode = await MeshQuery.QueryAsync<MeshNode>("path:graph/commented-moved", ct: TestContext.Current.CancellationToken).FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        movedNode.Should().NotBeNull("Node should exist at new path");

        var oldNode = await MeshQuery.QueryAsync<MeshNode>("path:graph/commented", ct: TestContext.Current.CancellationToken).FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        oldNode.Should().BeNull("Node should not remain at old path");
    }

    /// <summary>
    /// Move node throws when source doesn't exist.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task MoveNodeAsync_ThrowsWhenSourceNotFound()
    {
        // Act - move via MoveNodeRequest
        var response = await Mesh.AwaitResponse<MoveNodeResponse>(new MoveNodeRequest("graph/nonexistent", "graph/newpath"), o => o, TestContext.Current.CancellationToken);

        // Assert - should fail with source not found
        response.Message.Success.Should().BeFalse("Move should fail when source doesn't exist");
        response.Message.Error.Should().Contain("not found");
    }

    /// <summary>
    /// Move node throws when target path already exists.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task MoveNodeAsync_ThrowsWhenTargetExists()
    {
        // Arrange
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("graph/source") with { Name = "Source", NodeType = "type/org" }, ct: TestContext.Current.CancellationToken);
        await NodeFactory.CreateNodeAsync(MeshNode.FromPath("graph/target") with { Name = "Target", NodeType = "type/org" }, ct: TestContext.Current.CancellationToken);

        // Act - move via MoveNodeRequest
        var response = await Mesh.AwaitResponse<MoveNodeResponse>(new MoveNodeRequest("graph/source", "graph/target"), o => o, TestContext.Current.CancellationToken);

        // Assert - should fail because target already exists
        response.Message.Success.Should().BeFalse("Move should fail when target already exists");
        response.Message.Error.Should().Contain("already exists");
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
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.OverviewArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(orgAddress, reference);

        // Wait for the stream to emit a value (with timeout from test attribute)
        var value = await stream.Timeout(10.Seconds()).FirstAsync();

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

        // Wait for the stream to emit a value
        var value = await stream.Timeout(10.Seconds()).FirstAsync();

        // Assert
        value.Should().NotBe(default(JsonElement), "Empty area should return default view content");
    }

    /// <summary>
    /// Tests that the Organization NodeType catalog renders correctly.
    /// When navigating to type/org and requesting the Catalog area,
    /// it should render a StackControl with CatalogContent that contains either
    /// organization thumbnails or "No items found" message.
    /// </summary>
    [Fact(Timeout = 10000)]
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
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.SearchArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(typeOrgAddress, reference);

        // Wait for an emission that contains the expected search structure
        var values = await stream
            .Take(5)  // Take up to 5 emissions
            .TakeUntil(Observable.Timer(TimeSpan.FromSeconds(3)))  // Or timeout after 3s
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

        // The MeshSearchControl should have the correct namespace and scope
        var hasCorrectQuery = json.Contains("namespace:type/org");
        hasCorrectQuery.Should().BeTrue($"Search should have namespace filter in query. JSON: {json.Substring(0, Math.Min(1000, json.Length))}");
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
        var nodes = await MeshQuery.QueryAsync<MeshNode>(query, null, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);
        foreach (var node in nodes)
            Output.WriteLine($"Found: {node.Path}");

        // Assert
        nodes.Should().NotBeEmpty("Query should return organizations");
        nodes.Should().Contain(n => n.Path == "graph/org1", "Should find org1");
        nodes.Should().Contain(n => n.Path == "graph/org2", "Should find org2");
    }

    #endregion

    #region Code Node Sibling Query Tests

    /// <summary>
    /// Verifies the parent path derivation logic used by CodeLayoutAreas.Overview.
    /// For a code node at "type/story/_Source/code", the parent NodeType path should be "type/story".
    /// </summary>
    [Theory]
    [InlineData("type/story/_Source/code", "type/story")]
    [InlineData("type/org/_Source/orgCode", "type/org")]
    [InlineData("Organization/_Source/Organization", "Organization")]
    [InlineData("a/b/_Source/c", "a/b")]
    public void CodeNode_ParentPathParsing_StripsTwoSegments(string codePath, string expectedParent)
    {
        var segments = codePath.Split('/');
        var parentPath = segments.Length >= 3
            ? string.Join("/", segments.Take(segments.Length - 2))
            : codePath;

        parentPath.Should().Be(expectedParent,
            $"Stripping last 2 segments from '{codePath}' should yield the NodeType parent path");
    }

    /// <summary>
    /// Verifies that IMeshService with scope:descendants finds Code nodes that are 2 levels deep.
    /// Code nodes at "type/story/_Source/code" are NOT immediate children of "type/story" (they're
    /// grandchildren), so namespace: would miss them. scope:descendants is required.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task QueryAsync_ScopeDescendants_FindsCodeNodesUnderNodeType()
    {
        // Arrange
        var client = GetClient();
        var graphAddress = new Address("graph");

        // Initialize graph hub (required for routing)
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(graphAddress),
            TestContext.Current.CancellationToken);

        // Act: Query for Code nodes under type/story using scope:descendants
        var query = "path:type/story nodeType:Code scope:descendants";
        var nodes = await MeshQuery.QueryAsync<MeshNode>(query, null, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        foreach (var node in nodes)
            Output.WriteLine($"Found Code node: {node.Path} (NodeType={node.NodeType})");

        // Assert: should find the code node created by SaveCodeAsChildNodeAsync
        nodes.Should().NotBeEmpty("scope:descendants should find Code nodes 2 levels deep");
        nodes.Should().OnlyContain(n => n.NodeType == "Code", "All results should be Code nodes");
    }

    /// <summary>
    /// Verifies that namespace: does NOT find Code nodes (they are 2 levels deep).
    /// This confirms the bug that was fixed by switching to scope:descendants.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task QueryAsync_ScopeChildren_DoesNotFindCodeNodes()
    {
        // Arrange
        var client = GetClient();
        var graphAddress = new Address("graph");

        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(graphAddress),
            TestContext.Current.CancellationToken);

        // Act: Query for Code nodes under type/story using namespace: (1 level deep only)
        var query = "namespace:type/story nodeType:Code";
        var nodes = await MeshQuery.QueryAsync<MeshNode>(query, null, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        foreach (var node in nodes)
            Output.WriteLine($"Found with namespace: {node.Path}");

        // Assert: namespace: only checks 1 level deep — Code nodes are at depth 2 (type/story/_Source/id)
        nodes.Should().BeEmpty("namespace: only finds immediate children; Code nodes are 2 levels deep");
    }

    /// <summary>
    /// Verifies that querying for all Code nodes under each NodeType finds them.
    /// This is the same query pattern used by both CodeLayoutAreas.Overview (for siblings)
    /// and NodeTypeLayoutAreas.Overview (for code list in left menu).
    /// </summary>
    [Theory]
    [InlineData("type/story")]
    [InlineData("type/org")]
    [InlineData("type/project")]
    [InlineData("type/graph")]
    public async Task QueryAsync_EachNodeType_HasCodeDescendants(string nodeTypePath)
    {
        // Arrange
        var client = GetClient();
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(new Address("graph")),
            TestContext.Current.CancellationToken);

        // Act
        var query = $"path:{nodeTypePath} nodeType:Code scope:descendants";
        var nodes = await MeshQuery.QueryAsync<MeshNode>(query, null, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        foreach (var node in nodes)
            Output.WriteLine($"{nodeTypePath} -> Code node: {node.Path}");

        // Assert
        nodes.Should().NotBeEmpty($"{nodeTypePath} should have Code descendants (created by SaveCodeAsChildNodeAsync)");
        nodes.Should().OnlyContain(n => n.NodeType == "Code");
        nodes.Should().OnlyContain(n => n.Content is CodeConfiguration,
            "Code node Content should be CodeConfiguration");
    }

    #endregion
}

[CollectionDefinition("OrganizationsLayoutTests", DisableParallelization = true)]
public class OrganizationsLayoutTestsCollection { }

/// <summary>
/// Tests that use FileSystemPersistenceService to validate JSON deserialization with $type discriminator.
/// This replicates the exact production scenario where nodes and partition objects are read from disk.
/// </summary>
[Collection("DynamicGraphFileSystemPersistenceTests")]
public class DynamicGraphFileSystemPersistenceTest : MonolithMeshTestBase
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

    public DynamicGraphFileSystemPersistenceTest(ITestOutputHelper output) : base(output)
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
          "order": 8,
          "isPersistent": true,
          "content": {
            "$type": "NodeTypeDefinition",
            "id": "Organizations",
            "namespace": "Type",
            "displayName": "Organizations",
            "iconName": "Building",
            "description": "Catalog of organizations",
            "order": 8
          }
        }
        """;
        File.WriteAllText(Path.Combine(typeDir, "Organizations.json"), organizationsTypeJson);

        // 2. Create Type/Organizations/_Source/codeConfiguration.json - Code as child MeshNode
        var organizationsTypeDir = Path.Combine(typeDir, "Organizations");
        var codeDir = Path.Combine(organizationsTypeDir, "_Source");
        Directory.CreateDirectory(codeDir);

        var codeConfigJson = """
        {
          "id": "codeConfiguration",
          "namespace": "Type/Organizations/_Source",
          "name": "Code",
          "nodeType": "Code",
          "content": {
            "$type": "CodeConfiguration",
            "code": "public record Organizations { }"
          }
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
          "order": 10,
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

        var graphCodeDir = Path.Combine(typeGraphDir, "graph", "_Source");
        Directory.CreateDirectory(graphCodeDir);

        var graphCodeConfigJson = """
        {
          "id": "codeConfiguration",
          "namespace": "type/graph/_Source",
          "name": "Code",
          "nodeType": "Code",
          "content": {
            "$type": "CodeConfiguration",
            "code": "public record Graph { }"
          }
        }
        """;
        File.WriteAllText(Path.Combine(graphCodeDir, "codeConfiguration.json"), graphCodeConfigJson);
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
            .AddGraph();
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
        // Act - this should find Type/Organizations by reading from disk
        var nodeTypeNode = await MeshQuery.QueryAsync<MeshNode>("path:Type/Organizations", ct: TestContext.Current.CancellationToken).FirstOrDefaultAsync(TestContext.Current.CancellationToken);

        // Assert
        nodeTypeNode.Should().NotBeNull(
            "PersistenceService should find the NodeType node from disk. " +
            "If null, the Content property was likely deserialized as JsonElement instead of NodeTypeDefinition.");
        nodeTypeNode!.Path.Should().Be("Type/Organizations");
        nodeTypeNode.NodeType.Should().Be("NodeType");

        // Critical: Content must be NodeTypeDefinition, not JsonElement
        nodeTypeNode.Content.Should().BeOfType<NodeTypeDefinition>(
            "The $type discriminator in the JSON should cause Content to be deserialized as NodeTypeDefinition. " +
            "If this fails, ITypeRegistry is not properly configured for FileSystemStorageAdapter.");
    }

    /// <summary>
    /// Tests that CodeConfiguration can be loaded from child MeshNodes under the Code path.
    /// Code is now stored as regular MeshNodes with nodeType="Code" and content=CodeConfiguration.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task FileSystem_CodeConfiguration_LoadedFromChildMeshNodes()
    {
        // Act - get children of the Code path
        var codeChildren = await MeshQuery.QueryAsync<MeshNode>("namespace:Type/Organizations/_Source", ct: TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        // Assert
        codeChildren.Should().NotBeEmpty("Code path should have child MeshNodes with CodeConfiguration");
        var codeNode = codeChildren.First();
        codeNode.NodeType.Should().Be("Code");
        codeNode.Content.Should().BeOfType<CodeConfiguration>(
            "Code child node Content should be deserialized as CodeConfiguration via $type discriminator");
        var codeConfig = (CodeConfiguration)codeNode.Content!;
        codeConfig.Code.Should().NotBeNullOrEmpty(
            "CodeConfiguration.Code should contain C# source code.");
    }

    /// <summary>
    /// Tests the complete flow: node loading, type compilation, and HubConfiguration setting.
    /// This is the end-to-end test for the production scenario.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task FileSystem_Organizations_GetsHubConfiguration_FromCompiledAssembly()
    {
        // Act - get the Organizations node (triggers on-demand compilation from disk files)
        var node = await MeshQuery.QueryAsync<MeshNode>("path:Organizations", ct: TestContext.Current.CancellationToken).FirstOrDefaultAsync(TestContext.Current.CancellationToken);

        // Assert
        node.Should().NotBeNull("Organizations node should exist on disk");

        // Enrich via NodeTypeService to trigger compilation and populate HubConfiguration
        var nodeTypeService = Mesh.ServiceProvider.GetRequiredService<INodeTypeService>();
        node = await nodeTypeService.EnrichWithNodeTypeAsync(node!, TestContext.Current.CancellationToken);

        node.HubConfiguration.Should().NotBeNull(
            "Organizations node should have HubConfiguration from the compiled assembly. " +
            "If null, the on-demand compilation failed - likely because NodeTypeDefinition or CodeConfiguration " +
            "were not properly deserialized from JSON (returned as JsonElement instead).");
    }
}

[CollectionDefinition("DynamicGraphFileSystemPersistenceTests", DisableParallelization = true)]
public class DynamicGraphFileSystemPersistenceTestsCollection { }

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
/// Uses MeshWeaver/Documentation/Article as the NodeType with Code children
/// (Article.cs and ArticleLayoutAreas.cs).
/// </summary>
[Collection("SamplesGraphDataTests")]
public class SamplesGraphDataTest : MonolithMeshTestBase
{
    private readonly string _cacheDirectory;

    // Article NodeType is at MeshWeaver/Documentation/Article with 2 code files
    private const string ArticleNodeTypePath = "MeshWeaver/Documentation/Article";
    private const string ArticleCodeNamespace = "MeshWeaver/Documentation/Article/_Source";

    public SamplesGraphDataTest(ITestOutputHelper output) : base(output)
    {
        _cacheDirectory = Path.Combine(Path.GetTempPath(), "MeshWeaverSamplesTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_cacheDirectory);
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return builder
            .UseMonolithMesh()
            .AddPartitionedFileSystemPersistence(TestPaths.SamplesGraphData)
            .AddUserData()
            .AddTypeData()
            .AddMeshWeaverDocs()
            .AddSystemorph()
            .AddAcme()
            .AddVUser()
            .ConfigureServices(services => services.Configure<CompilationCacheOptions>(o => o.CacheDirectory = _cacheDirectory))
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas())
            .AddGraph();
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
    /// Test that tries to get the default layout from the Article NodeType.
    /// This test is expected to deadlock if the NodeTypeService implementation has issues.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task Article_GetDefaultLayout_ShouldNotDeadlock()
    {
        // Arrange - Article NodeType is at MeshWeaver/Documentation/Article
        var articleAddress = new Address(ArticleNodeTypePath);

        // Get a client with data services configured
        var client = GetClient(c => c.AddData(data => data));

        Output.WriteLine($"Samples data directory: {TestPaths.SamplesGraphData}");
        Output.WriteLine($"Directory exists: {Directory.Exists(TestPaths.SamplesGraphData)}");

        // Act: Request the default layout area (empty = default view)
        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(string.Empty);

        Output.WriteLine("Getting remote stream for Article NodeType...");
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(articleAddress, reference);

        Output.WriteLine("Waiting for first value from stream...");
        // Wait for the stream to emit a value - this is where deadlock would occur
        var value = await stream.Timeout(10.Seconds()).FirstAsync();

        Output.WriteLine($"Received value: {value}");

        // Assert
        value.Should().NotBe(default(JsonElement),
            "Article NodeType should return default layout area content");
    }

    /// <summary>
    /// Test that verifies the samples directory exists and has the expected structure.
    /// Article NodeType is at MeshWeaver/Documentation/Article.json with Code/ subfolder.
    /// </summary>
    [Fact]
    public void SamplesDirectory_Exists_WithExpectedStructure()
    {
        Output.WriteLine($"Checking samples directory: {TestPaths.SamplesGraphData}");

        Directory.Exists(TestPaths.SamplesGraphData).Should().BeTrue(
            $"Samples directory should exist at {TestPaths.SamplesGraphData}");

        var articleJsonPath = Path.Combine(TestPaths.SamplesGraphData, "MeshWeaver", "Documentation", "Article.json");
        File.Exists(articleJsonPath).Should().BeTrue(
            $"Article.json should exist at {articleJsonPath}");

        var codeConfigPath = Path.Combine(TestPaths.SamplesGraphData, "MeshWeaver", "Documentation", "Article", "_Source", "Article.cs");
        File.Exists(codeConfigPath).Should().BeTrue(
            $"Article/_Source/Article.cs should exist at {codeConfigPath}");
    }

    /// <summary>
    /// Test that the MeshCatalog can resolve the Article NodeType path.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task Article_CanBeResolved_FromSamples()
    {
        var pathResolver = Mesh.ServiceProvider.GetRequiredService<IPathResolver>();

        Output.WriteLine($"Resolving {ArticleNodeTypePath} path...");
        var resolution = await pathResolver.ResolvePathAsync(ArticleNodeTypePath);

        resolution.Should().NotBeNull("Article NodeType should be resolvable from samples");
        Output.WriteLine($"Resolved: Prefix={resolution.Prefix}, Remainder={resolution.Remainder}");
    }

    /// <summary>
    /// Test that GetNodeAsync works for the Article NodeType.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task Article_GetNodeAsync_FromSamples()
    {
        Output.WriteLine($"Getting node for {ArticleNodeTypePath}...");
        var node = await MeshQuery.QueryAsync<MeshNode>($"path:{ArticleNodeTypePath}", ct: TestContext.Current.CancellationToken).FirstOrDefaultAsync(TestContext.Current.CancellationToken);

        node.Should().NotBeNull("Article NodeType node should exist in samples");
        Output.WriteLine($"Node: Path={node!.Path}, NodeType={node.NodeType}, HubConfiguration={node.HubConfiguration != null}");

        node.Path.Should().Be(ArticleNodeTypePath);
        node.NodeType.Should().Be("NodeType");
    }

    /// <summary>
    /// Test that verifies the node's HubConfiguration is set for NodeType nodes.
    /// Nodes with nodeType=NodeType compile their own code to get HubConfiguration.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task Article_HubConfiguration_IsSetForNodeType()
    {
        var node = await MeshQuery.QueryAsync<MeshNode>($"path:{ArticleNodeTypePath}", ct: TestContext.Current.CancellationToken).FirstOrDefaultAsync(TestContext.Current.CancellationToken);

        node.Should().NotBeNull();
        node!.NodeType.Should().Be("NodeType");

        // Query results don't include HubConfiguration - enrichment (compilation) happens via NodeTypeService
        var nodeTypeService = Mesh.ServiceProvider.GetRequiredService<INodeTypeService>();
        node = await nodeTypeService.EnrichWithNodeTypeAsync(node, TestContext.Current.CancellationToken);

        Output.WriteLine($"Node.HubConfiguration is null: {node.HubConfiguration == null}");
        node.HubConfiguration.Should().NotBeNull(
            "NodeType nodes should have HubConfiguration from their own code");

        var hubConfig = node.HubConfiguration;
        hubConfig.Should().NotBeNull("Should be able to get HubConfiguration");
        Output.WriteLine("Successfully obtained HubConfiguration");
    }

    /// <summary>
    /// Test that sends a PingRequest to the Article NodeType hub.
    /// This triggers hub creation which may cause deadlock.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task Article_PingRequest_ShouldNotDeadlock()
    {
        var articleAddress = new Address(ArticleNodeTypePath);
        var client = GetClient();

        Output.WriteLine($"Sending PingRequest to {ArticleNodeTypePath}...");

        // This triggers hub creation - may deadlock here
        var response = await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(articleAddress),
            TestContext.Current.CancellationToken);

        Output.WriteLine($"Received response: {response}");
        response.Should().NotBeNull("Should receive ping response");
    }

    /// <summary>
    /// Test that Code node Overview returns non-empty content.
    /// Uses JsonElement and GetControlStream to verify the Overview area renders a control.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task CodeNode_Overview_ReturnsNonEmptyContent()
    {
        var codeNodeAddress = new Address($"{ArticleCodeNamespace}/Article");
        var client = GetClient(c => c
            .WithInitialization((h, _) => RoutingService.RegisterStreamAsync(h))
            .AddLayoutClient(cc => cc)
            .AddData(data => data));
        var workspace = client.GetWorkspace();

        // Request the Overview area on the Code node
        var reference = new LayoutAreaReference("Overview");

        Output.WriteLine("Getting Overview for Code node...");
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(codeNodeAddress, reference);

        // Use GetControlStream to get the Overview area control
        var control = await stream
            .GetControlStream("Overview")
            .Timeout(TimeSpan.FromSeconds(20))
            .FirstAsync(x => x != null);

        Output.WriteLine($"Received control type: {control?.GetType().FullName}");

        control.Should().NotBeNull("Code node Overview should render a control");
    }

    /// <summary>
    /// Test that CodeConfiguration can be loaded from child MeshNodes via persistence.
    /// Code is stored as child MeshNodes with nodeType="Code" under the Article/_Source path.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task Article_CodeConfiguration_LoadedFromChildMeshNodes()
    {
        Output.WriteLine($"Getting Code children for {ArticleCodeNamespace}...");
        var codeChildren = await MeshQuery.QueryAsync<MeshNode>($"namespace:{ArticleCodeNamespace}", ct: TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);
        foreach (var child in codeChildren)
        {
            Output.WriteLine($"Found code child: {child.Path}, NodeType={child.NodeType}");
        }

        codeChildren.Should().NotBeEmpty($"{ArticleCodeNamespace} should have child MeshNodes");

        foreach (var codeNode in codeChildren)
        {
            codeNode.Content.Should().BeOfType<CodeConfiguration>(
                "Code child node Content should be CodeConfiguration");
            var codeConfig = (CodeConfiguration)codeNode.Content!;
            Output.WriteLine($"CodeConfiguration.Code: {codeConfig.Code?.Substring(0, Math.Min(100, codeConfig.Code?.Length ?? 0))}...");
            codeConfig.Code.Should().NotBeNullOrEmpty("CodeConfiguration.Code should have content");
        }
    }

    /// <summary>
    /// Test that Code node Overview returns a Splitter control with code list and content pane.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task CodeNode_Overview_WithLayoutClient_ReturnsSplitter()
    {
        var codeNodeAddress = new Address($"{ArticleCodeNamespace}/Article");

        // Configure client with AddLayoutClient for proper control deserialization
        var client = GetClient(c => c
            .WithInitialization((h, _) => RoutingService.RegisterStreamAsync(h))
            .AddLayoutClient(cc => cc)
            .AddData(data => data));

        var workspace = client.GetWorkspace();

        // Request the Overview area on the Code node
        var reference = new LayoutAreaReference("Overview");
        Output.WriteLine($"Requesting layout area: {reference.Area}");

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(codeNodeAddress, reference);

        // Use GetControlStream to get the Overview area control
        Output.WriteLine("Waiting for 'Overview' control via GetControlStream...");
        var control = await stream
            .GetControlStream("Overview")
            .Timeout(30.Seconds())
            .FirstAsync(x => x != null);

        Output.WriteLine($"Control type: {control?.GetType().FullName}");

        // Verify we got a Splitter (Code node Overview now renders a Splitter with code list + content)
        control.Should().BeOfType<SplitterControl>("Code node Overview should return a Splitter control");
    }

    /// <summary>
    /// Debug test: Collect control updates for Code node Overview.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task CodeNode_Overview_DebugUpdateSequence()
    {
        var codeNodeAddress = new Address($"{ArticleCodeNamespace}/Article");

        var client = GetClient(c => c
            .WithInitialization((h, _) => RoutingService.RegisterStreamAsync(h))
            .AddLayoutClient(cc => cc)
            .AddData(data => data));

        var workspace = client.GetWorkspace();

        // Initialize hub
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(codeNodeAddress),
            TestContext.Current.CancellationToken);

        var reference = new LayoutAreaReference("Overview");
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(codeNodeAddress, reference);

        Output.WriteLine("Waiting for SplitterControl...\n");

        // Wait for the expected SplitterControl instead of collecting all updates for 20s
        var final = await stream
            .GetControlStream("Overview")
            .Where(x => x != null).Select(x => x!)
            .OfType<SplitterControl>()
            .Timeout(TimeSpan.FromSeconds(20))
            .FirstAsync();

        Output.WriteLine($"Received SplitterControl with {final.Areas.Count} panes");

        final.Should().BeOfType<SplitterControl>("Final control should be a Splitter");
    }

    /// <summary>
    /// Test using GetControlStream to get properly typed controls for Code node.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task CodeNode_Overview_GetControlStream_Test()
    {
        var codeNodeAddress = new Address($"{ArticleCodeNamespace}/Article");

        var client = GetClient(c => c
            .WithInitialization((h, _) => RoutingService.RegisterStreamAsync(h))
            .AddLayoutClient(cc => cc)
            .AddData(data => data));

        var workspace = client.GetWorkspace();

        // Initialize hub
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(codeNodeAddress),
            TestContext.Current.CancellationToken);

        var reference = new LayoutAreaReference("Overview");
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(codeNodeAddress, reference);

        Output.WriteLine("Getting control for 'Overview' area...");

        var control = await stream
            .GetControlStream("Overview")
            .Timeout(30.Seconds())
            .FirstAsync(x => x != null);

        Output.WriteLine($"Control type: {control?.GetType().FullName}");

        if (control is SplitterControl splitterControl)
        {
            Output.WriteLine($"Splitter has {splitterControl.Areas.Count} panes");
            foreach (var area in splitterControl.Areas)
            {
                Output.WriteLine($"  Area: {area.Id}");
            }
        }

        control.Should().NotBeNull("Code node Overview should have a control");
    }

    /// <summary>
    /// Test that loading the default layout area (empty string) from MeshWeaver node works.
    /// This test diagnoses eternal spinner issue when navigating to /MeshWeaver.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task MeshWeaver_GetDefaultLayoutArea_ShouldNotHang()
    {
        // Arrange
        var meshWeaverAddress = new Address("MeshWeaver");

        // Get a client with data services configured
        var client = GetClient(c => c
            .WithInitialization((h, _) => RoutingService.RegisterStreamAsync(h))
            .AddLayoutClient(cc => cc)
            .AddData(data => data));

        Output.WriteLine($"Samples data directory: {TestPaths.SamplesGraphData}");

        // First check if MeshWeaver node exists
        var node = await MeshQuery.QueryAsync<MeshNode>("path:MeshWeaver", ct: TestContext.Current.CancellationToken).FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        node.Should().NotBeNull("MeshWeaver node should exist in samples");
        Output.WriteLine($"MeshWeaver node: Path={node!.Path}, NodeType={node.NodeType}");

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
        var value = await stream.Timeout(10.Seconds()).FirstAsync();

        Output.WriteLine($"Received value: {value}");

        // Assert
        value.Should().NotBe(default(JsonElement),
            "MeshWeaver node should return default layout area content");
    }

    /// <summary>
    /// Test that the ArticleLayoutAreas.cs file exists alongside Article.cs for Article.
    /// This validates the custom view configuration structure.
    /// </summary>
    [Fact]
    public void Article_ViewsCs_Exists()
    {
        var viewsCsPath = Path.Combine(TestPaths.SamplesGraphData, "MeshWeaver", "Documentation", "Article", "_Source", "ArticleLayoutAreas.cs");
        Output.WriteLine($"Checking for ArticleLayoutAreas.cs at: {viewsCsPath}");

        File.Exists(viewsCsPath).Should().BeTrue(
            $"Article/_Source/ArticleLayoutAreas.cs should exist at {viewsCsPath} for custom view configuration");
    }

    /// <summary>
    /// Test that Article NodeType with custom AddLayout view compiles successfully.
    /// This validates the fix for missing using statements in DynamicMeshNodeAttributeGenerator.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task Article_CustomView_CompilesSuccessfully()
    {
        Output.WriteLine($"Getting {ArticleNodeTypePath} node (triggers compilation)...");
        var node = await MeshQuery.QueryAsync<MeshNode>($"path:{ArticleNodeTypePath}", ct: TestContext.Current.CancellationToken).FirstOrDefaultAsync(TestContext.Current.CancellationToken);

        node.Should().NotBeNull("Article NodeType node should exist");
        node!.NodeType.Should().Be("NodeType");

        // Enrich via NodeTypeService to trigger compilation and populate HubConfiguration
        var nodeTypeService = Mesh.ServiceProvider.GetRequiredService<INodeTypeService>();
        node = await nodeTypeService.EnrichWithNodeTypeAsync(node, TestContext.Current.CancellationToken);

        node.HubConfiguration.Should().NotBeNull(
            "Article NodeType should have HubConfiguration from compiled assembly with custom view");

        var hubConfig = node.HubConfiguration;
        hubConfig.Should().NotBeNull("HubConfiguration should be available");

        Output.WriteLine("Article custom view compiled successfully!");
    }

    /// <summary>
    /// Test that the custom view compiles and renders for a Systemorph organization instance.
    /// This tests the complete flow from views.json through compilation to rendering.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task Systemorph_OverviewView_RendersCustomView()
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
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.OverviewArea);

        Output.WriteLine("Getting Overview area for Systemorph organization...");
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(organizationAddress, reference);

        var control = await stream
            .GetControlStream(MeshNodeLayoutAreas.OverviewArea)
            .Timeout(30.Seconds())
            .FirstAsync(x => x != null);

        Output.WriteLine($"Received control type: {control?.GetType().FullName}");

        // Verify the view is invoked and returns a control
        control.Should().NotBeNull("Overview should render a control for Organization instance");
    }

    /// <summary>
    /// Verifies that QueryAsync with scope:descendants finds Code nodes under Article.
    /// Article has 2 code files: Article.cs and ArticleLayoutAreas.cs
    /// stored at MeshWeaver/Documentation/Article/_Source/Article and MeshWeaver/Documentation/Article/_Source/ArticleLayoutAreas.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task Article_QueryAsync_ScopeDescendants_FindsCodeNodes()
    {
        // Arrange
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        // Act: Query for Code nodes under Article using scope:descendants
        var query = $"path:{ArticleNodeTypePath} nodeType:{CodeNodeType.NodeType} scope:descendants";
        Output.WriteLine($"Executing query: {query}");

        var nodes = await meshQuery.QueryAsync<MeshNode>(query, null, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        foreach (var node in nodes)
            Output.WriteLine($"Found Code node: Path={node.Path}, Name={node.Name}, NodeType={node.NodeType}");

        // Assert: Article has 2 code files
        nodes.Should().HaveCount(2, "Article should have 2 Code descendants (Article.cs and ArticleLayoutAreas.cs)");
        nodes.Should().OnlyContain(n => n.NodeType == CodeNodeType.NodeType, "All results should be Code nodes");
        nodes.Should().OnlyContain(n => n.Content is CodeConfiguration, "All Code nodes should have CodeConfiguration content");
    }

    /// <summary>
    /// Verifies that the NodeType Overview area for Article renders a SplitterControl
    /// and that the left menu contains the correct number of code file entries.
    /// This diagnoses the "no code files" issue in the NodeType Overview.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task Article_NodeTypeOverview_ContainsCodeNodes()
    {
        var articleAddress = new Address(ArticleNodeTypePath);

        var client = GetClient(c => c
            .WithInitialization((h, _) => RoutingService.RegisterStreamAsync(h))
            .AddLayoutClient(cc => cc)
            .AddData(data => data));

        var workspace = client.GetWorkspace();

        // Initialize hub
        Output.WriteLine($"Sending PingRequest to {ArticleNodeTypePath} NodeType hub...");
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(articleAddress),
            TestContext.Current.CancellationToken);
        Output.WriteLine("PingRequest completed");

        // Request the NodeType Overview area (which is the default area for NodeType nodes)
        var reference = new LayoutAreaReference(NodeTypeLayoutAreas.OverviewArea);
        Output.WriteLine($"Requesting layout area: {reference.Area}");

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(articleAddress, reference);

        // Wait for a JSON update that contains code node content (instead of collecting for 30s)
        Output.WriteLine("Waiting for JSON update containing code nodes...");
        var lastJson = await stream
            .Select(x => x.Value.GetRawText())
            .Where(json => json.Contains("Article") && json.Contains("Code") && !json.Contains("No code files"))
            .Timeout(TimeSpan.FromSeconds(30))
            .FirstAsync();

        Output.WriteLine($"Received matching JSON update (first 5000 chars):\n{lastJson.Substring(0, Math.Min(5000, lastJson.Length))}");

        // The NavMenu should contain code file entries
        var hasArticleCode = lastJson.Contains("Article") && lastJson.Contains("Code");
        hasArticleCode.Should().BeTrue("Overview JSON should contain Code section references");

        // Check specifically for "No code files" to detect the bug
        var hasNoCodeFiles = lastJson.Contains("No code files");
        Output.WriteLine($"Contains 'No code files': {hasNoCodeFiles}");

        hasNoCodeFiles.Should().BeFalse(
            "NodeType Overview should NOT show 'No code files'. " +
            "Article has 2 code files (Article.cs, ArticleLayoutAreas.cs) " +
            "which should be found by scope:descendants query.");
    }

    /// <summary>
    /// Tests the ObserveQuery reactive path specifically for Code nodes under Article.
    /// This mirrors the exact query used by NodeTypeLayoutAreas.Overview.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task Article_ObserveQuery_ScopeDescendants_EmitsCodeNodes()
    {
        // Arrange
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var hubPath = ArticleNodeTypePath;

        // Act: Use ObserveQuery with the same query pattern as NodeTypeLayoutAreas.Overview
        var queryString = $"path:{hubPath} nodeType:{CodeNodeType.NodeType} scope:descendants";
        Output.WriteLine($"ObserveQuery: {queryString}");

        var request = MeshQueryRequest.FromQuery(queryString);

        var initialChange = await meshQuery.ObserveQuery<MeshNode>(request)
            .Timeout(TimeSpan.FromSeconds(20))
            .FirstAsync();

        Output.WriteLine($"ObserveQuery emitted: ChangeType={initialChange.ChangeType}, Items.Count={initialChange.Items.Count}");
        foreach (var item in initialChange.Items)
            Output.WriteLine($"  Item: Path={item.Path}, Name={item.Name}, NodeType={item.NodeType}");

        // Assert
        initialChange.ChangeType.Should().Be(QueryChangeType.Initial, "First emission should be Initial");
        initialChange.Items.Should().HaveCount(2,
            "ObserveQuery should find 2 Code nodes under Article (Article.cs and ArticleLayoutAreas.cs)");
        initialChange.Items.Should().OnlyContain(n => n.NodeType == CodeNodeType.NodeType);
    }

    /// <summary>
    /// Test that loading the Search area from MeshWeaver node works.
    /// </summary>
    [Fact(Timeout = 10000)]
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
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.SearchArea);

        Output.WriteLine("Getting Search area for MeshWeaver...");
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(meshWeaverAddress, reference);

        var value = await stream.Timeout(10.Seconds()).FirstAsync();
        Output.WriteLine($"Received Search area value");

        // Assert
        value.Should().NotBe(default(JsonElement),
            "MeshWeaver Search area should return content");
    }

    /// <summary>
    /// Test that a VUser node can be created and detected as already existing.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task PortalHub_CreateVUser_AlreadyExists_ReturnsFailure()
    {
        // Use a unique ID to avoid collisions with pre-loaded VUser partition data
        var uniqueId = $"DupTest_{Guid.NewGuid():N}"[..20];

        // Create a VUser node first
        await NodeFactory.CreateNodeAsync(new MeshNode(uniqueId, "VUser")
        {
            Name = uniqueId,
            NodeType = "VUser"
        });

        // Try to create the same VUser again
        var act = () => NodeFactory.CreateNodeAsync(new MeshNode(uniqueId, "VUser")
        {
            Name = uniqueId,
            NodeType = "VUser"
        });

        await act.Should().ThrowAsync<Exception>();
    }

    /// <summary>
    /// Test that creating a VUser node and then checking for it does not hang.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task EnsureVirtualUserNode_CheckThenCreate_NoHang()
    {
        // Use a unique ID to avoid collisions with pre-loaded VUser partition data
        var uniqueId = $"HangTest_{Guid.NewGuid():N}"[..20];
        var vUserPath = $"VUser/{uniqueId}";

        // Check that node doesn't exist first
        var existingNode = await MeshQuery.QueryAsync<MeshNode>($"path:{vUserPath}", ct: TestContext.Current.CancellationToken)
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        existingNode.Should().BeNull("VUser should not exist yet");

        // Create the VUser
        await NodeFactory.CreateNodeAsync(new MeshNode(uniqueId, "VUser")
        {
            Name = uniqueId,
            NodeType = "VUser"
        });

        // Verify it can be retrieved
        var node = await MeshQuery.QueryAsync<MeshNode>($"path:{vUserPath}", ct: TestContext.Current.CancellationToken)
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        node.Should().NotBeNull("VUser should exist after creation");
    }
}

[CollectionDefinition("SamplesGraphDataTests", DisableParallelization = true)]
public class SamplesGraphDataTestsCollection { }
