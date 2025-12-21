using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
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

    private static void SetupTestConfiguration(string testDataDirectory)
    {
        // Create _config directories
        var configDir = Path.Combine(testDataDirectory, "_config");
        Directory.CreateDirectory(Path.Combine(configDir, "dataModels"));
        Directory.CreateDirectory(Path.Combine(configDir, "nodeTypes"));
        Directory.CreateDirectory(Path.Combine(configDir, "hubFeatures"));
        Directory.CreateDirectory(Path.Combine(configDir, "contentCollections"));

        // Create a simple Story data model
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

        // Create Organization data model
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

        // Create Project data model
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
        WriteJsonConfig(configDir, "dataModels", "org.json", orgDataModel);
        WriteJsonConfig(configDir, "dataModels", "project.json", projectDataModel);
        WriteJsonConfig(configDir, "dataModels", "graph.json", graphDataModel);

        // Create node types
        var storyNodeType = new NodeTypeConfig { NodeType = "story", DataModelId = "story", DisplayName = "Story" };
        var orgNodeType = new NodeTypeConfig { NodeType = "org", DataModelId = "org", DisplayName = "Organization" };
        var projectNodeType = new NodeTypeConfig { NodeType = "project", DataModelId = "project", DisplayName = "Project" };
        var graphNodeType = new NodeTypeConfig { NodeType = "graph", DataModelId = "graph", DisplayName = "Graph" };

        WriteJsonConfig(configDir, "nodeTypes", "story.json", storyNodeType);
        WriteJsonConfig(configDir, "nodeTypes", "org.json", orgNodeType);
        WriteJsonConfig(configDir, "nodeTypes", "project.json", projectNodeType);
        WriteJsonConfig(configDir, "nodeTypes", "graph.json", graphNodeType);

        // Create hub feature
        var hubFeature = new HubFeatureConfig
        {
            Id = "graph",
            EnableMeshNavigation = true,
            EnableDynamicNodeTypeAreas = true
        };
        WriteJsonConfig(configDir, "hubFeatures", "graph.json", hubFeature);
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

        // Create in-memory persistence and pre-seed with test data
        var persistence = new InMemoryPersistenceService();

        // Pre-seed the hierarchy: graph -> org -> project -> story
        persistence.SaveNodeAsync(new MeshNode("graph") { Name = "Graph", NodeType = "graph" }).GetAwaiter().GetResult();
        persistence.SaveNodeAsync(new MeshNode("graph/org1") { Name = "Organization 1", NodeType = "org", Description = "First org" }).GetAwaiter().GetResult();
        persistence.SaveNodeAsync(new MeshNode("graph/org2") { Name = "Organization 2", NodeType = "org", Description = "Second org" }).GetAwaiter().GetResult();
        persistence.SaveNodeAsync(new MeshNode("graph/org1/proj1") { Name = "Project 1", NodeType = "project" }).GetAwaiter().GetResult();
        persistence.SaveNodeAsync(new MeshNode("graph/org1/proj2") { Name = "Project 2", NodeType = "project" }).GetAwaiter().GetResult();
        persistence.SaveNodeAsync(new MeshNode("graph/org1/proj1/story1") { Name = "Story 1", NodeType = "story" }).GetAwaiter().GetResult();
        persistence.SaveNodeAsync(new MeshNode("graph/org1/proj1/story2") { Name = "Story 2", NodeType = "story" }).GetAwaiter().GetResult();

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
        var orgAddress = new Address("graph/org1");

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
        var projAddress = new Address("graph/org1/proj1");

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

    #region DataChangeRequest Tests

    [Fact(Timeout = 90000, Skip = "Requires DataChangeRequest handler infrastructure for MeshNode type")]
    public async Task CreateNode_ViaDataChangeRequest_PersistsToCorrectPartition()
    {
        var client = GetClient();
        var graphAddress = new Address("graph");

        // Initialize graph hub
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(graphAddress),
            new CancellationTokenSource(10.Seconds()).Token);

        // Act - create new org via DataChangeRequest
        var newOrg = new MeshNode("graph/org3") { Name = "Organization 3", NodeType = "org", Description = "Third org" };
        client.Post(new DataChangeRequest { Creations = [newOrg] }, o => o.WithTarget(graphAddress));
        await Task.Delay(500);

        // Assert - verify IPersistenceService has the new node
        var persistedOrg = await Persistence.GetNodeAsync("graph/org3");
        persistedOrg.Should().NotBeNull("new org should be persisted");
        persistedOrg!.Name.Should().Be("Organization 3");
        persistedOrg.Description.Should().Be("Third org");
        persistedOrg.NodeType.Should().Be("org");

        // Verify it appears in graph's children
        var children = await Persistence.GetChildrenAsync("graph").ToListAsync(TestContext.Current.CancellationToken);
        children.Should().Contain(n => n.Prefix == "graph/org3");
    }

    [Fact(Timeout = 90000, Skip = "Requires DataChangeRequest handler infrastructure for MeshNode type")]
    public async Task UpdateNode_ViaDataChangeRequest_UpdatesPersistence()
    {
        var client = GetClient();
        var graphAddress = new Address("graph");

        // Initialize graph hub
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(graphAddress),
            new CancellationTokenSource(10.Seconds()).Token);

        // Verify initial state
        var initial = await Persistence.GetNodeAsync("graph/org1");
        initial!.Name.Should().Be("Organization 1");
        initial.Description.Should().Be("First org");

        // Act - update org1 via DataChangeRequest
        var updatedOrg = new MeshNode("graph/org1")
        {
            Name = "Updated Org 1",
            NodeType = "org",
            Description = "Updated description"
        };
        client.Post(new DataChangeRequest { Updates = [updatedOrg] }, o => o.WithTarget(graphAddress));
        await Task.Delay(500);

        // Assert - verify IPersistenceService was updated
        var persisted = await Persistence.GetNodeAsync("graph/org1");
        persisted.Should().NotBeNull();
        persisted!.Name.Should().Be("Updated Org 1");
        persisted.Description.Should().Be("Updated description");
    }

    [Fact(Timeout = 90000, Skip = "Requires DataChangeRequest handler infrastructure for MeshNode type")]
    public async Task DeleteNode_ViaDataChangeRequest_RemovesFromPersistenceRecursively()
    {
        var client = GetClient();
        var graphAddress = new Address("graph");

        // Initialize graph hub
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(graphAddress),
            new CancellationTokenSource(10.Seconds()).Token);

        // Verify initial state - org1 and its children exist
        (await Persistence.GetNodeAsync("graph/org1")).Should().NotBeNull();
        (await Persistence.GetNodeAsync("graph/org1/proj1")).Should().NotBeNull();
        (await Persistence.GetNodeAsync("graph/org1/proj1/story1")).Should().NotBeNull();

        // Act - delete org1 via DataChangeRequest (should delete recursively)
        var nodeToDelete = new MeshNode("graph/org1");
        client.Post(new DataChangeRequest { Deletions = [nodeToDelete] }, o => o.WithTarget(graphAddress));
        await Task.Delay(500);

        // Assert - verify IPersistenceService removed node and all descendants
        (await Persistence.GetNodeAsync("graph/org1")).Should().BeNull("org1 should be deleted");
        (await Persistence.GetNodeAsync("graph/org1/proj1")).Should().BeNull("proj1 should be deleted recursively");
        (await Persistence.GetNodeAsync("graph/org1/proj1/story1")).Should().BeNull("story1 should be deleted recursively");

        // org2 should still exist
        (await Persistence.GetNodeAsync("graph/org2")).Should().NotBeNull("org2 should remain");
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

    #region Details Layout Area Tests

    [Fact(Timeout = 90000, Skip = "Requires layout area registration infrastructure")]
    public async Task GraphHub_DetailsLayoutArea_ReturnsStackControl()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var graphAddress = new Address("graph");

        // Initialize graph hub
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(graphAddress),
            new CancellationTokenSource(10.Seconds()).Token);

        // Act - get the Details layout area (returns StackControl with header and content)
        var reference = new LayoutAreaReference(MeshNodeView.DetailsArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(graphAddress, reference);

        var control = await stream
            .GetControlStream(reference.Area!)
            .Where(c => c is StackControl)
            .Timeout(10.Seconds())
            .FirstAsync();

        // Assert
        control.Should().NotBeNull("Details layout area should return a control");
        control.Should().BeOfType<StackControl>();
        var stack = (StackControl)control;
        // Should have at least header and content areas
        stack.Areas.Should().HaveCountGreaterThanOrEqualTo(2, "Should have header and content areas");
    }

    [Fact(Timeout = 90000, Skip = "Requires layout area registration infrastructure")]
    public async Task OrgHub_DetailsLayoutArea_ReturnsStackControl()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var orgAddress = new Address("graph/org1");

        // Initialize org hub
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(orgAddress),
            new CancellationTokenSource(10.Seconds()).Token);

        // Act - get the Details layout area
        var reference = new LayoutAreaReference(MeshNodeView.DetailsArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(orgAddress, reference);

        var control = await stream
            .GetControlStream(reference.Area!)
            .Where(c => c is StackControl)
            .Timeout(10.Seconds())
            .FirstAsync();

        // Assert
        control.Should().NotBeNull("Details layout area should return a control from org hub");
        control.Should().BeOfType<StackControl>();
    }

    #endregion

    #region Dynamic Type Tests

    [Fact(Timeout = 30000, Skip = "Integration test - requires kernel initialization")]
    public async Task DynamicType_CompiledFromJson_CanBeUsedWithMeshNode()
    {
        var client = GetClient();
        var storyAddress = new Address("graph/org1/proj1/story1");

        // Initialize story hub
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(storyAddress),
            TestContext.Current.CancellationToken);

        // Get the compiled Story type
        var storyType = TypeCompiler.GetCompiledType("story");
        storyType.Should().NotBeNull("Story type should be compiled from JSON config");
        storyType!.Name.Should().Be("Story");

        // Create an instance of the dynamic type
        var story = Activator.CreateInstance(storyType);
        story.Should().NotBeNull();

        // Set properties via reflection
        var titleProp = storyType.GetProperty("Title");
        titleProp.Should().NotBeNull();
        titleProp!.SetValue(story, "Dynamic Story Title");

        var idProp = storyType.GetProperty("Id");
        idProp!.SetValue(story, "dynamic-story-1");

        // Verify the values
        titleProp.GetValue(story).Should().Be("Dynamic Story Title");
        idProp.GetValue(story).Should().Be("dynamic-story-1");
    }

    #endregion

    #region Additional DetailsLayoutArea Tests

    /// <summary>
    /// Project hub's Details layout area returns StackControl.
    /// </summary>
    [Fact(Timeout = 90000, Skip = "Requires layout area registration infrastructure")]
    public async Task ProjectHub_DetailsLayoutArea_ReturnsStackControl()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var projAddress = new Address("graph/org1/proj1");

        // Initialize project hub
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(projAddress),
            TestContext.Current.CancellationToken);

        // Act - get the Details layout area
        var reference = new LayoutAreaReference(MeshNodeView.DetailsArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(projAddress, reference);

        var control = await stream
            .GetControlStream(reference.Area!)
            .Where(c => c is StackControl)
            .Timeout(10.Seconds())
            .FirstAsync();

        // Assert
        control.Should().NotBeNull("Details layout area should return a control from project hub");
        control.Should().BeOfType<StackControl>();
        var stack = (StackControl)control;
        stack.Areas.Should().HaveCountGreaterThanOrEqualTo(2, "Should have header and content areas");
    }

    /// <summary>
    /// Story hub's Details layout area returns StackControl.
    /// </summary>
    [Fact(Timeout = 90000, Skip = "Requires layout area registration infrastructure")]
    public async Task StoryHub_DetailsLayoutArea_ReturnsStackControl()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var storyAddress = new Address("graph/org1/proj1/story1");

        // Initialize story hub
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(storyAddress),
            TestContext.Current.CancellationToken);

        // Act - get the Details layout area
        var reference = new LayoutAreaReference(MeshNodeView.DetailsArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(storyAddress, reference);

        var control = await stream
            .GetControlStream(reference.Area!)
            .Where(c => c is StackControl)
            .Timeout(10.Seconds())
            .FirstAsync();

        // Assert
        control.Should().NotBeNull("Details layout area should return a control from story hub");
        control.Should().BeOfType<StackControl>();
        var stack = (StackControl)control;
        stack.Areas.Should().HaveCountGreaterThanOrEqualTo(2, "Should have header and content areas");
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
        await Persistence.SaveNodeAsync(new MeshNode("graph/movetest") { Name = "Move Test", NodeType = "org" });

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
        await Persistence.SaveNodeAsync(new MeshNode("graph/parent") { Name = "Parent", NodeType = "org" });
        await Persistence.SaveNodeAsync(new MeshNode("graph/parent/child1") { Name = "Child 1", NodeType = "project" });
        await Persistence.SaveNodeAsync(new MeshNode("graph/parent/child2") { Name = "Child 2", NodeType = "project" });
        await Persistence.SaveNodeAsync(new MeshNode("graph/parent/child1/grandchild") { Name = "Grandchild", NodeType = "story" });

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
        await Persistence.SaveNodeAsync(new MeshNode("graph/commented") { Name = "Commented Node", NodeType = "org" });
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
        await Persistence.SaveNodeAsync(new MeshNode("graph/source") { Name = "Source", NodeType = "org" });
        await Persistence.SaveNodeAsync(new MeshNode("graph/target") { Name = "Target", NodeType = "org" });

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
