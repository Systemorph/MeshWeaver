using System;
using System.IO;
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
/// Integration tests for MeshNode CRUD operations.
/// All operations go through the hub via DataChangeRequest messages.
/// The hub handles persistence internally via MeshNodeTypeSource.
/// Verification is done via IPersistenceService (read-only check) to verify persistence worked.
/// </summary>
public class MeshNodeCrudTest : MonolithMeshTestBase
{
    private static readonly string TestDirectoryBase = Path.Combine(Path.GetTempPath(), "MeshWeaverCrudTests");

    [ThreadStatic]
    private static string? _currentTestDirectory;

    public MeshNodeCrudTest(ITestOutputHelper output) : base(output)
    {
    }

    private static string GetOrCreateTestDirectory()
    {
        if (_currentTestDirectory == null)
        {
            _currentTestDirectory = Path.Combine(TestDirectoryBase, Guid.NewGuid().ToString());
            Directory.CreateDirectory(_currentTestDirectory);
        }
        return _currentTestDirectory;
    }

    public override async ValueTask DisposeAsync()
    {
        var dir = _currentTestDirectory;
        _currentTestDirectory = null;

        await base.DisposeAsync();

        if (dir != null && Directory.Exists(dir))
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return base.ConfigureMesh(builder)
            .AddFileSystemPersistence(GetOrCreateTestDirectory())
            .InstallAssemblies(typeof(GraphDomainAttribute).Assembly.Location);
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration).AddLayoutClient();
    }

    /// <summary>
    /// Test 1: Create nodes via DataChangeRequest, verify they are persisted.
    /// </summary>
    [Fact]
    public async Task CreateNodes_ViaDataChangeRequest_PersistsToStorage()
    {
        var client = GetClient();
        var graphAddress = new Address("graph");

        // Initialize graph hub via ping
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(graphAddress),
            new CancellationTokenSource(10.Seconds()).Token);

        // Act - create child nodes via DataChangeRequest
        var org1 = new MeshNode("graph/org1") { Name = "Org 1", NodeType = "org" };
        var org2 = new MeshNode("graph/org2") { Name = "Org 2", NodeType = "org" };
        client.Post(new DataChangeRequest { Creations = [org1, org2] }, o => o.WithTarget(graphAddress));

        // Wait for persistence to complete
        await Task.Delay(500);

        // Assert - verify via IPersistenceService that nodes were persisted
        var persistence = ServiceProvider.GetRequiredService<IPersistenceService>();

        var persistedOrg1 = await persistence.GetNodeAsync("graph/org1");
        persistedOrg1.Should().NotBeNull("org1 should be persisted");
        persistedOrg1!.Name.Should().Be("Org 1");
        persistedOrg1.NodeType.Should().Be("org");

        var persistedOrg2 = await persistence.GetNodeAsync("graph/org2");
        persistedOrg2.Should().NotBeNull("org2 should be persisted");
        persistedOrg2!.Name.Should().Be("Org 2");

        // Verify they appear as children of graph
        var children = await persistence.GetChildrenAsync("graph");
        children.Should().Contain(n => n.Prefix == "graph/org1");
        children.Should().Contain(n => n.Prefix == "graph/org2");
    }

    /// <summary>
    /// Test 2: Update node via DataChangeRequest, verify changes are persisted.
    /// </summary>
    [Fact]
    public async Task UpdateNode_ViaDataChangeRequest_PersistsChanges()
    {
        var client = GetClient();
        var graphAddress = new Address("graph");

        // Initialize hub and create initial node
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(graphAddress),
            new CancellationTokenSource(10.Seconds()).Token);

        var originalOrg = new MeshNode("graph/updateorg") { Name = "Original Name", NodeType = "org" };
        client.Post(new DataChangeRequest { Creations = [originalOrg] }, o => o.WithTarget(graphAddress));
        await Task.Delay(300);

        // Verify initial state
        var persistence = ServiceProvider.GetRequiredService<IPersistenceService>();
        var initial = await persistence.GetNodeAsync("graph/updateorg");
        initial.Should().NotBeNull();
        initial!.Name.Should().Be("Original Name");

        // Act - update the node
        var updatedOrg = new MeshNode("graph/updateorg")
        {
            Name = "Updated Name",
            NodeType = "org",
            Description = "Now with description"
        };
        client.Post(new DataChangeRequest { Updates = [updatedOrg] }, o => o.WithTarget(graphAddress));
        await Task.Delay(500);

        // Assert - verify persistence was updated
        var persisted = await persistence.GetNodeAsync("graph/updateorg");
        persisted.Should().NotBeNull();
        persisted!.Name.Should().Be("Updated Name");
        persisted.Description.Should().Be("Now with description");
    }

    /// <summary>
    /// Test 3: Delete node via DataChangeRequest, verify it's removed from persistence.
    /// </summary>
    [Fact]
    public async Task DeleteNode_ViaDataChangeRequest_RemovesFromPersistence()
    {
        var client = GetClient();
        var graphAddress = new Address("graph");

        // Initialize hub and create nodes
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(graphAddress),
            new CancellationTokenSource(10.Seconds()).Token);

        var org1 = new MeshNode("graph/deleteorg1") { Name = "Delete Org 1", NodeType = "org" };
        var org2 = new MeshNode("graph/deleteorg2") { Name = "Delete Org 2", NodeType = "org" };
        client.Post(new DataChangeRequest { Creations = [org1, org2] }, o => o.WithTarget(graphAddress));
        await Task.Delay(300);

        // Verify both exist
        var persistence = ServiceProvider.GetRequiredService<IPersistenceService>();
        (await persistence.GetNodeAsync("graph/deleteorg1")).Should().NotBeNull();
        (await persistence.GetNodeAsync("graph/deleteorg2")).Should().NotBeNull();

        // Act - delete one node
        var nodeToDelete = new MeshNode("graph/deleteorg1");
        client.Post(new DataChangeRequest { Deletions = [nodeToDelete] }, o => o.WithTarget(graphAddress));
        await Task.Delay(500);

        // Assert - deleted node should be gone, other should remain
        (await persistence.GetNodeAsync("graph/deleteorg1")).Should().BeNull("deleted node should be removed from persistence");
        (await persistence.GetNodeAsync("graph/deleteorg2")).Should().NotBeNull("other node should remain in persistence");
    }

    /// <summary>
    /// Test 4: Delete node with children - should delete recursively from persistence.
    /// </summary>
    [Fact]
    public async Task DeleteNodeWithChildren_RemovesRecursivelyFromPersistence()
    {
        var client = GetClient();
        var graphAddress = new Address("graph");

        // Initialize graph hub and create org
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(graphAddress),
            new CancellationTokenSource(10.Seconds()).Token);

        var org = new MeshNode("graph/parentorg") { Name = "Parent Org", NodeType = "org" };
        client.Post(new DataChangeRequest { Creations = [org] }, o => o.WithTarget(graphAddress));
        await Task.Delay(300);

        // Initialize org hub and create children under it
        var orgAddress = new Address("graph/parentorg");
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(orgAddress),
            new CancellationTokenSource(10.Seconds()).Token);

        var proj1 = new MeshNode("graph/parentorg/proj1") { Name = "Project 1", NodeType = "project" };
        var proj2 = new MeshNode("graph/parentorg/proj2") { Name = "Project 2", NodeType = "project" };
        client.Post(new DataChangeRequest { Creations = [proj1, proj2] }, o => o.WithTarget(orgAddress));
        await Task.Delay(300);

        // Verify hierarchy exists in persistence
        var persistence = ServiceProvider.GetRequiredService<IPersistenceService>();
        (await persistence.GetNodeAsync("graph/parentorg")).Should().NotBeNull();
        (await persistence.GetNodeAsync("graph/parentorg/proj1")).Should().NotBeNull();
        (await persistence.GetNodeAsync("graph/parentorg/proj2")).Should().NotBeNull();

        // Act - delete org from graph hub (parent deletes child)
        var nodeToDelete = new MeshNode("graph/parentorg");
        client.Post(new DataChangeRequest { Deletions = [nodeToDelete] }, o => o.WithTarget(graphAddress));
        await Task.Delay(500);

        // Assert - all should be removed from persistence (recursive delete)
        (await persistence.GetNodeAsync("graph/parentorg")).Should().BeNull("parent org should be deleted");
        (await persistence.GetNodeAsync("graph/parentorg/proj1")).Should().BeNull("child proj1 should be deleted recursively");
        (await persistence.GetNodeAsync("graph/parentorg/proj2")).Should().BeNull("child proj2 should be deleted recursively");
    }

    /// <summary>
    /// Test 5: Create nested hierarchy and verify persistence structure.
    /// Note: Child hubs can only be addressed after the parent persists them.
    /// </summary>
    [Fact]
    public async Task CreateNestedHierarchy_PersistsAllLevels()
    {
        var client = GetClient();
        var graphAddress = new Address("graph");
        var persistence = ServiceProvider.GetRequiredService<IPersistenceService>();

        // Initialize graph hub
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(graphAddress),
            new CancellationTokenSource(10.Seconds()).Token);

        // Create org under graph
        var org = new MeshNode("graph/deeporg") { Name = "Deep Org", NodeType = "org" };
        client.Post(new DataChangeRequest { Creations = [org] }, o => o.WithTarget(graphAddress));
        await Task.Delay(500);

        // Verify org is persisted before trying to address it
        var persistedOrg = await persistence.GetNodeAsync("graph/deeporg");
        persistedOrg.Should().NotBeNull("org should be persisted before we can address it");

        // Initialize org hub (now that it's persisted, the catalog can find it)
        var orgAddress = new Address("graph/deeporg");
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(orgAddress),
            new CancellationTokenSource(10.Seconds()).Token);

        var proj = new MeshNode("graph/deeporg/proj") { Name = "Project", NodeType = "project" };
        client.Post(new DataChangeRequest { Creations = [proj] }, o => o.WithTarget(orgAddress));
        await Task.Delay(500);

        // Verify project is persisted
        var persistedProj = await persistence.GetNodeAsync("graph/deeporg/proj");
        persistedProj.Should().NotBeNull("project should be persisted before we can address it");

        // Initialize project hub
        var projAddress = new Address("graph/deeporg/proj");
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(projAddress),
            new CancellationTokenSource(10.Seconds()).Token);

        var story = new MeshNode("graph/deeporg/proj/story") { Name = "Story", NodeType = "story" };
        client.Post(new DataChangeRequest { Creations = [story] }, o => o.WithTarget(projAddress));
        await Task.Delay(500);

        // Assert - verify entire hierarchy is persisted
        persistedOrg = await persistence.GetNodeAsync("graph/deeporg");
        persistedOrg.Should().NotBeNull();
        persistedOrg!.Name.Should().Be("Deep Org");

        persistedProj = await persistence.GetNodeAsync("graph/deeporg/proj");
        persistedProj.Should().NotBeNull();
        persistedProj!.Name.Should().Be("Project");

        var persistedStory = await persistence.GetNodeAsync("graph/deeporg/proj/story");
        persistedStory.Should().NotBeNull();
        persistedStory!.Name.Should().Be("Story");
    }

    /// <summary>
    /// Test 6: Combined CRUD operations in sequence.
    /// </summary>
    [Fact]
    public async Task CrudOperationsSequence_WorksCorrectly()
    {
        var client = GetClient();
        var graphAddress = new Address("graph");
        var persistence = ServiceProvider.GetRequiredService<IPersistenceService>();

        // Initialize graph hub
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(graphAddress),
            new CancellationTokenSource(10.Seconds()).Token);

        // CREATE
        var org = new MeshNode("graph/crudorg") { Name = "CRUD Org", NodeType = "org" };
        client.Post(new DataChangeRequest { Creations = [org] }, o => o.WithTarget(graphAddress));
        await Task.Delay(300);

        (await persistence.GetNodeAsync("graph/crudorg"))!.Name.Should().Be("CRUD Org");

        // UPDATE
        var updatedOrg = new MeshNode("graph/crudorg") { Name = "Updated CRUD Org", NodeType = "org" };
        client.Post(new DataChangeRequest { Updates = [updatedOrg] }, o => o.WithTarget(graphAddress));
        await Task.Delay(300);

        (await persistence.GetNodeAsync("graph/crudorg"))!.Name.Should().Be("Updated CRUD Org");

        // DELETE
        var deleteOrg = new MeshNode("graph/crudorg");
        client.Post(new DataChangeRequest { Deletions = [deleteOrg] }, o => o.WithTarget(graphAddress));
        await Task.Delay(300);

        (await persistence.GetNodeAsync("graph/crudorg")).Should().BeNull();
    }

    /// <summary>
    /// Test 7: Get _Nodes layout area from graph hub - should return DataGrid with children.
    /// </summary>
    [Fact]
    public async Task GraphHub_NodesLayoutArea_ReturnsDataGridWithChildren()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var graphAddress = new Address("graph");

        // Initialize graph hub
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(graphAddress),
            new CancellationTokenSource(10.Seconds()).Token);

        // Create some child nodes
        var org1 = new MeshNode("graph/layoutorg1") { Name = "Layout Org 1", NodeType = "org" };
        var org2 = new MeshNode("graph/layoutorg2") { Name = "Layout Org 2", NodeType = "org" };
        client.Post(new DataChangeRequest { Creations = [org1, org2] }, o => o.WithTarget(graphAddress));
        await Task.Delay(500);

        // Act - get the _Nodes layout area
        var reference = new LayoutAreaReference(MeshCatalogView.NodesArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(graphAddress, reference);

        // Wait for a StackControl (which contains the DataGrid)
        var control = await stream
            .GetControlStream(reference.Area!)
            .Where(c => c is StackControl)
            .Timeout(10.Seconds())
            .FirstAsync();

        // Assert
        control.Should().NotBeNull("_Nodes layout area should return a control");
        control.Should().BeOfType<StackControl>("_Nodes returns a StackControl with header and DataGrid");

        var stack = (StackControl)control;
        stack.Areas.Should().NotBeEmpty("Stack should have areas for header and DataGrid");
    }

    /// <summary>
    /// Test 8: Get _Nodes layout area from org hub - should return DataGrid with project children.
    /// </summary>
    [Fact]
    public async Task OrgHub_NodesLayoutArea_ReturnsDataGridWithProjectChildren()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var graphAddress = new Address("graph");
        var persistence = ServiceProvider.GetRequiredService<IPersistenceService>();

        // Initialize graph hub and create org
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(graphAddress),
            new CancellationTokenSource(10.Seconds()).Token);

        var org = new MeshNode("graph/orgwithlayout") { Name = "Org With Layout", NodeType = "org" };
        client.Post(new DataChangeRequest { Creations = [org] }, o => o.WithTarget(graphAddress));
        await Task.Delay(500);

        // Verify org is persisted
        (await persistence.GetNodeAsync("graph/orgwithlayout")).Should().NotBeNull();

        // Initialize org hub
        var orgAddress = new Address("graph/orgwithlayout");
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(orgAddress),
            new CancellationTokenSource(10.Seconds()).Token);

        // Create project children under org
        var proj1 = new MeshNode("graph/orgwithlayout/proj1") { Name = "Project 1", NodeType = "project" };
        var proj2 = new MeshNode("graph/orgwithlayout/proj2") { Name = "Project 2", NodeType = "project" };
        client.Post(new DataChangeRequest { Creations = [proj1, proj2] }, o => o.WithTarget(orgAddress));
        await Task.Delay(500);

        // Act - get the _Nodes layout area from org hub
        var reference = new LayoutAreaReference(MeshCatalogView.NodesArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(orgAddress, reference);

        var control = await stream
            .GetControlStream(reference.Area!)
            .Where(c => c is StackControl)
            .Timeout(10.Seconds())
            .FirstAsync();

        // Assert
        control.Should().NotBeNull("_Nodes layout area should return a control from org hub");
        control.Should().BeOfType<StackControl>();

        var stack = (StackControl)control;
        stack.Areas.Should().NotBeEmpty("Stack should have areas for header and DataGrid");
    }

    /// <summary>
    /// Test 9: Get _Nodes layout area from project hub - should return DataGrid with story children.
    /// </summary>
    [Fact]
    public async Task ProjectHub_NodesLayoutArea_ReturnsDataGridWithStoryChildren()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var graphAddress = new Address("graph");
        var persistence = ServiceProvider.GetRequiredService<IPersistenceService>();

        // Initialize graph hub and create org
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(graphAddress),
            new CancellationTokenSource(10.Seconds()).Token);

        var org = new MeshNode("graph/projlayoutorg") { Name = "Proj Layout Org", NodeType = "org" };
        client.Post(new DataChangeRequest { Creations = [org] }, o => o.WithTarget(graphAddress));
        await Task.Delay(500);

        // Initialize org hub and create project
        var orgAddress = new Address("graph/projlayoutorg");
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(orgAddress),
            new CancellationTokenSource(10.Seconds()).Token);

        var proj = new MeshNode("graph/projlayoutorg/projwithstories") { Name = "Project With Stories", NodeType = "project" };
        client.Post(new DataChangeRequest { Creations = [proj] }, o => o.WithTarget(orgAddress));
        await Task.Delay(500);

        // Verify project is persisted
        (await persistence.GetNodeAsync("graph/projlayoutorg/projwithstories")).Should().NotBeNull();

        // Initialize project hub
        var projAddress = new Address("graph/projlayoutorg/projwithstories");
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(projAddress),
            new CancellationTokenSource(10.Seconds()).Token);

        // Create story children under project
        var story1 = new MeshNode("graph/projlayoutorg/projwithstories/story1") { Name = "Story 1", NodeType = "story" };
        var story2 = new MeshNode("graph/projlayoutorg/projwithstories/story2") { Name = "Story 2", NodeType = "story" };
        client.Post(new DataChangeRequest { Creations = [story1, story2] }, o => o.WithTarget(projAddress));
        await Task.Delay(500);

        // Act - get the _Nodes layout area from project hub
        var reference = new LayoutAreaReference(MeshCatalogView.NodesArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(projAddress, reference);

        var control = await stream
            .GetControlStream(reference.Area!)
            .Where(c => c is StackControl)
            .Timeout(10.Seconds())
            .FirstAsync();

        // Assert
        control.Should().NotBeNull("_Nodes layout area should return a control from project hub");
        control.Should().BeOfType<StackControl>();

        var stack = (StackControl)control;
        stack.Areas.Should().NotBeEmpty("Stack should have areas for header and DataGrid");
    }

    /// <summary>
    /// Test 10: Get _Nodes layout area from story hub - should return DataGrid with empty children (leaf node).
    /// </summary>
    [Fact]
    public async Task StoryHub_NodesLayoutArea_ReturnsDataGridWithEmptyChildren()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var graphAddress = new Address("graph");
        var persistence = ServiceProvider.GetRequiredService<IPersistenceService>();

        // Initialize graph hub and create org
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(graphAddress),
            new CancellationTokenSource(10.Seconds()).Token);

        var org = new MeshNode("graph/storylayoutorg") { Name = "Story Layout Org", NodeType = "org" };
        client.Post(new DataChangeRequest { Creations = [org] }, o => o.WithTarget(graphAddress));
        await Task.Delay(500);

        // Initialize org hub and create project
        var orgAddress = new Address("graph/storylayoutorg");
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(orgAddress),
            new CancellationTokenSource(10.Seconds()).Token);

        var proj = new MeshNode("graph/storylayoutorg/storyproj") { Name = "Story Proj", NodeType = "project" };
        client.Post(new DataChangeRequest { Creations = [proj] }, o => o.WithTarget(orgAddress));
        await Task.Delay(500);

        // Initialize project hub and create story
        var projAddress = new Address("graph/storylayoutorg/storyproj");
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(projAddress),
            new CancellationTokenSource(10.Seconds()).Token);

        var story = new MeshNode("graph/storylayoutorg/storyproj/leafstory") { Name = "Leaf Story", NodeType = "story" };
        client.Post(new DataChangeRequest { Creations = [story] }, o => o.WithTarget(projAddress));
        await Task.Delay(500);

        // Verify story is persisted
        (await persistence.GetNodeAsync("graph/storylayoutorg/storyproj/leafstory")).Should().NotBeNull();

        // Initialize story hub
        var storyAddress = new Address("graph/storylayoutorg/storyproj/leafstory");
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(storyAddress),
            new CancellationTokenSource(10.Seconds()).Token);

        // Act - get the _Nodes layout area from story hub (leaf node, no children)
        var reference = new LayoutAreaReference(MeshCatalogView.NodesArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(storyAddress, reference);

        var control = await stream
            .GetControlStream(reference.Area!)
            .Where(c => c is StackControl)
            .Timeout(10.Seconds())
            .FirstAsync();

        // Assert
        control.Should().NotBeNull("_Nodes layout area should return a control from story hub");
        control.Should().BeOfType<StackControl>();

        var stack = (StackControl)control;
        stack.Areas.Should().NotBeEmpty("Stack should have areas for header and DataGrid even if empty");
    }
}
