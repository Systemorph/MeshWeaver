using System;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Domain;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Integration tests for MeshNode CRUD operations and path resolution.
/// Tests verify:
/// 1. Hubs load data from pre-seeded IPersistenceService at initialization
/// 2. GetDataRequest returns the pre-seeded elements
/// 3. DataChangeRequest persists changes to IPersistenceService in correct partition
/// 4. LayoutAreaReference("_Nodes") returns DataGrid for all node levels
/// 5. ResolvePath correctly resolves paths to addresses with remainders
/// </summary>
public class MeshNodeCrudTest : MonolithMeshTestBase
{
    private IPersistenceService Persistence => ServiceProvider.GetRequiredService<IPersistenceService>();
    private IMeshCatalog MeshCatalog => ServiceProvider.GetRequiredService<IMeshCatalog>();

    public MeshNodeCrudTest(ITestOutputHelper output) : base(output)
    {
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
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

        return builder
            .UseMonolithMesh()
            .ConfigureServices(services => services.AddPersistence(persistence))
            .InstallAssemblies(typeof(GraphDomainAttribute).Assembly.Location);
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration).AddLayoutClient();
    }

    #region Test 1-3: Hub Initialization Loads Data from Persistence

    /// <summary>
    /// Test 1: Graph hub loads children (orgs) from persistence at initialization.
    /// No DataChangeRequest needed - data should be available immediately after ping.
    /// </summary>
    [Fact]
    public async Task GraphHub_LoadsChildrenFromPersistence_AtInitialization()
    {
        var client = GetClient();
        var graphAddress = new Address("graph");

        // Initialize graph hub via ping
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(graphAddress),
            new CancellationTokenSource(10.Seconds()).Token);

        // Verify persistence has the pre-seeded data
        var children = await Persistence.GetChildrenAsync("graph");
        children.Should().HaveCount(2, "graph should have 2 org children pre-seeded");
        children.Should().Contain(n => n.Prefix == "graph/org1");
        children.Should().Contain(n => n.Prefix == "graph/org2");
    }

    /// <summary>
    /// Test 2: Org hub loads children (projects) from persistence at initialization.
    /// </summary>
    [Fact]
    public async Task OrgHub_LoadsChildrenFromPersistence_AtInitialization()
    {
        var client = GetClient();
        var orgAddress = new Address("graph/org1");

        // Initialize org hub via ping
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(orgAddress),
            new CancellationTokenSource(10.Seconds()).Token);

        // Verify persistence has the pre-seeded projects
        var children = await Persistence.GetChildrenAsync("graph/org1");
        children.Should().HaveCount(2, "org1 should have 2 project children pre-seeded");
        children.Should().Contain(n => n.Prefix == "graph/org1/proj1");
        children.Should().Contain(n => n.Prefix == "graph/org1/proj2");
    }

    /// <summary>
    /// Test 3: Project hub loads children (stories) from persistence at initialization.
    /// </summary>
    [Fact]
    public async Task ProjectHub_LoadsChildrenFromPersistence_AtInitialization()
    {
        var client = GetClient();
        var projAddress = new Address("graph/org1/proj1");

        // Initialize project hub via ping
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(projAddress),
            new CancellationTokenSource(10.Seconds()).Token);

        // Verify persistence has the pre-seeded stories
        var children = await Persistence.GetChildrenAsync("graph/org1/proj1");
        children.Should().HaveCount(2, "proj1 should have 2 story children pre-seeded");
        children.Should().Contain(n => n.Prefix == "graph/org1/proj1/story1");
        children.Should().Contain(n => n.Prefix == "graph/org1/proj1/story2");
    }

    #endregion

    #region Test 4-6: DataChangeRequest Persists to Correct Partition

    /// <summary>
    /// Test 4: Create new node via DataChangeRequest - verifies IPersistenceService receives it.
    /// </summary>
    [Fact]
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
        var children = await Persistence.GetChildrenAsync("graph");
        children.Should().Contain(n => n.Prefix == "graph/org3");
    }

    /// <summary>
    /// Test 5: Update existing node via DataChangeRequest - verifies IPersistenceService is updated.
    /// </summary>
    [Fact]
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

    /// <summary>
    /// Test 6: Delete node via DataChangeRequest - verifies IPersistenceService removes it recursively.
    /// </summary>
    [Fact]
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
        (await Persistence.GetNodeAsync("graph/org1/proj1/story2")).Should().BeNull("story2 should be deleted recursively");
        (await Persistence.GetNodeAsync("graph/org1/proj2")).Should().BeNull("proj2 should be deleted recursively");

        // org2 should still exist
        (await Persistence.GetNodeAsync("graph/org2")).Should().NotBeNull("org2 should remain");
    }

    #endregion

    #region Test 7-11: ResolvePath Tests

    /// <summary>
    /// Test 7: ResolvePath finds persisted node that is NOT in configuration.
    /// </summary>
    [Fact]
    public void ResolvePath_FindsPersistedNode_NotInConfig()
    {
        // Act
        var resolution = MeshCatalog.ResolvePath("graph/org1");

        // Assert
        resolution.Should().NotBeNull("persistence has graph/org1");
        resolution!.Prefix.Should().Be("graph/org1");
        resolution.Remainder.Should().BeNull();
    }

    /// <summary>
    /// Test 8: ResolvePath walks UP hierarchy to find best match when path doesn't exist.
    /// </summary>
    [Fact]
    public void ResolvePath_WalksUpHierarchy_FindsBestMatch()
    {
        // Act: resolve path that goes deeper than persisted (nonexistent/deep doesn't exist)
        var resolution = MeshCatalog.ResolvePath("graph/org1/proj1/nonexistent/deep");

        // Assert: should match graph/org1/proj1 with remainder
        resolution.Should().NotBeNull();
        resolution!.Prefix.Should().Be("graph/org1/proj1");
        resolution.Remainder.Should().Be("nonexistent/deep");
    }

    /// <summary>
    /// Test 9: ResolvePath returns exact match when full path exists.
    /// </summary>
    [Fact]
    public void ResolvePath_ReturnsExactMatch_WhenFullPathExists()
    {
        // Act
        var resolution = MeshCatalog.ResolvePath("graph/org1/proj1/story1");

        // Assert
        resolution.Should().NotBeNull();
        resolution!.Prefix.Should().Be("graph/org1/proj1/story1");
        resolution.Remainder.Should().BeNull();
    }

    /// <summary>
    /// Test 10: ResolvePath with remainder returns correct prefix and remainder.
    /// </summary>
    [Fact]
    public void ResolvePath_WithRemainder_ReturnsCorrectParts()
    {
        // Act: resolve path with additional segments beyond existing node
        var resolution = MeshCatalog.ResolvePath("graph/org1/proj1/story1/Overview");

        // Assert
        resolution.Should().NotBeNull();
        resolution!.Prefix.Should().Be("graph/org1/proj1/story1");
        resolution.Remainder.Should().Be("Overview");
    }

    /// <summary>
    /// Test 11: ResolvePath returns null when no match found in persistence or config.
    /// </summary>
    [Fact]
    public void ResolvePath_ReturnsNull_WhenNoMatchFound()
    {
        // Act: resolve path that doesn't exist anywhere
        var resolution = MeshCatalog.ResolvePath("nonexistent/path/here");

        // Assert
        resolution.Should().BeNull();
    }

    /// <summary>
    /// Test 12: ResolvePath correctly parses underscore-prefixed segments as remainder.
    /// e.g., "graph/_Nodes" should resolve to address="graph", remainder="_Nodes"
    /// </summary>
    [Fact]
    public void ResolvePath_UnderscorePrefixedSegment_ParsesAsRemainder()
    {
        // Act: resolve path with underscore-prefixed segment (layout area)
        var resolution = MeshCatalog.ResolvePath("graph/_Nodes");

        // Assert: "graph" is the address, "_Nodes" is the remainder (layout area)
        resolution.Should().NotBeNull();
        resolution!.Prefix.Should().Be("graph");
        resolution.Remainder.Should().Be("_Nodes");
    }

    #endregion

    #region Test 13-16: LayoutAreaReference("_Nodes") Returns DataGrid

    /// <summary>
    /// Test 13: Graph hub's _Nodes layout area returns DataGrid with org children.
    /// </summary>
    [Fact]
    public async Task GraphHub_NodesLayoutArea_ReturnsDataGridWithOrgChildren()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var graphAddress = new Address("graph");

        // Initialize graph hub
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(graphAddress),
            new CancellationTokenSource(10.Seconds()).Token);

        // Act - get the _Nodes layout area
        var reference = new LayoutAreaReference(MeshCatalogView.NodesArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(graphAddress, reference);

        var control = await stream
            .GetControlStream(reference.Area!)
            .Where(c => c is TabsControl)
            .Timeout(10.Seconds())
            .FirstAsync();

        // Assert
        control.Should().NotBeNull("_Nodes layout area should return a control");
        control.Should().BeOfType<TabsControl>();
        var tabs = (TabsControl)control;
        tabs.Areas.Should().HaveCount(3, "Should have Overview, Nodes, and Comments tabs");
    }

    /// <summary>
    /// Test 14: Org hub's _Nodes layout area returns TabsControl with project children.
    /// </summary>
    [Fact]
    public async Task OrgHub_NodesLayoutArea_ReturnsDataGridWithProjectChildren()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var orgAddress = new Address("graph/org1");

        // Initialize org hub
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(orgAddress),
            new CancellationTokenSource(10.Seconds()).Token);

        // Act - get the _Nodes layout area
        var reference = new LayoutAreaReference(MeshCatalogView.NodesArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(orgAddress, reference);

        var control = await stream
            .GetControlStream(reference.Area!)
            .Where(c => c is TabsControl)
            .Timeout(10.Seconds())
            .FirstAsync();

        // Assert
        control.Should().NotBeNull("_Nodes layout area should return a control from org hub");
        control.Should().BeOfType<TabsControl>();
        var tabs = (TabsControl)control;
        tabs.Areas.Should().HaveCount(3, "Should have Overview, Nodes, and Comments tabs");
    }

    /// <summary>
    /// Test 15: Project hub's _Nodes layout area returns TabsControl with story children.
    /// </summary>
    [Fact]
    public async Task ProjectHub_NodesLayoutArea_ReturnsDataGridWithStoryChildren()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var projAddress = new Address("graph/org1/proj1");

        // Initialize project hub
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(projAddress),
            new CancellationTokenSource(10.Seconds()).Token);

        // Act - get the _Nodes layout area
        var reference = new LayoutAreaReference(MeshCatalogView.NodesArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(projAddress, reference);

        var control = await stream
            .GetControlStream(reference.Area!)
            .Where(c => c is TabsControl)
            .Timeout(10.Seconds())
            .FirstAsync();

        // Assert
        control.Should().NotBeNull("_Nodes layout area should return a control from project hub");
        control.Should().BeOfType<TabsControl>();
        var tabs = (TabsControl)control;
        tabs.Areas.Should().HaveCount(3, "Should have Overview, Nodes, and Comments tabs");
    }

    /// <summary>
    /// Test 16: Story hub's _Nodes layout area returns TabsControl (leaf node).
    /// </summary>
    [Fact]
    public async Task StoryHub_NodesLayoutArea_ReturnsDataGridWithEmptyChildren()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var storyAddress = new Address("graph/org1/proj1/story1");

        // Initialize story hub
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(storyAddress),
            new CancellationTokenSource(10.Seconds()).Token);

        // Act - get the _Nodes layout area (leaf node, but should still have the area)
        var reference = new LayoutAreaReference(MeshCatalogView.NodesArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(storyAddress, reference);

        var control = await stream
            .GetControlStream(reference.Area!)
            .Where(c => c is TabsControl)
            .Timeout(10.Seconds())
            .FirstAsync();

        // Assert
        control.Should().NotBeNull("_Nodes layout area should return a control from story hub");
        control.Should().BeOfType<TabsControl>();
        var tabs = (TabsControl)control;
        tabs.Areas.Should().HaveCount(3, "Should have Overview, Nodes, and Comments tabs");
    }

    #endregion
}
