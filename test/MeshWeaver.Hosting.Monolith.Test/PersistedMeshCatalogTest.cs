using System;
using System.Linq;
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
/// Integration tests for PersistedMeshCatalog path resolution.
/// Tests verify:
/// 1. ResolvePath finds persisted nodes (not in config)
/// 2. ResolvePath walks UP hierarchy to find best match
/// 3. Load nodes via GetDataRequest
/// 4. Load nodes via LayoutArea "_Nodes"
/// 5. Modify via DataChangeRequest → assert IPersistenceService updated
/// </summary>
public class PersistedMeshCatalogTest : MonolithMeshTestBase
{
    private IPersistenceService Persistence => ServiceProvider.GetRequiredService<IPersistenceService>();
    private IMeshCatalog MeshCatalog => ServiceProvider.GetRequiredService<IMeshCatalog>();

    public PersistedMeshCatalogTest(ITestOutputHelper output) : base(output)
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

    #region ResolvePath Tests

    /// <summary>
    /// Test 1: ResolvePath finds persisted node that is NOT in configuration.
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
    /// Test 2: ResolvePath walks UP hierarchy to find best match when path doesn't exist.
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
    /// Test 3: ResolvePath returns exact match when full path exists.
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
    /// Test 4: ResolvePath with remainder returns correct prefix and remainder.
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
    /// Test 5: ResolvePath returns null when no match found in persistence or config.
    /// </summary>
    [Fact]
    public void ResolvePath_ReturnsNull_WhenNoMatchFound()
    {
        // Act: resolve path that doesn't exist anywhere
        var resolution = MeshCatalog.ResolvePath("nonexistent/path/here");

        // Assert
        resolution.Should().BeNull();
    }

    #endregion

    #region LayoutArea Tests

    /// <summary>
    /// Test 8: Load nodes via LayoutArea "_Nodes" returns DataGrid.
    /// </summary>
    [Fact]
    public async Task LoadNodes_ViaLayoutArea_ReturnsDataGrid()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var graphAddress = new Address("graph");

        // Act - get the _Nodes layout area
        var reference = new LayoutAreaReference(MeshCatalogView.NodesArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(graphAddress, reference);

        var control = await stream
            .GetControlStream(reference.Area!)
            .Where(c => c is StackControl)
            .Timeout(10.Seconds())
            .FirstAsync();

        // Assert
        control.Should().NotBeNull("_Nodes layout area should return a control");
        control.Should().BeOfType<StackControl>();
        var stack = (StackControl)control;
        stack.Areas.Should().NotBeEmpty("Stack should have areas for header and DataGrid");
    }

    #endregion

    #region CRUD Tests - Assert Persistence Updated

    /// <summary>
    /// Test 9: Update via DataChangeRequest - assert IPersistenceService was updated.
    /// </summary>
    [Fact]
    public async Task Update_ViaDataChangeRequest_AssertPersistenceUpdated()
    {
        var client = GetClient();
        var graphAddress = new Address("graph");

        // Act - update org1 via DataChangeRequest
        var updatedOrg = new MeshNode("graph/org1")
        {
            Name = "Modified Org 1",
            NodeType = "org",
            Description = "Modified description"
        };
        client.Post(new DataChangeRequest { Updates = [updatedOrg] }, o => o.WithTarget(graphAddress));
        await Task.Delay(500);

        // Assert - verify IPersistenceService was updated (only assertion, not loading)
        var persisted = await Persistence.GetNodeAsync("graph/org1");
        persisted.Should().NotBeNull();
        persisted!.Name.Should().Be("Modified Org 1");
        persisted.Description.Should().Be("Modified description");
    }

    /// <summary>
    /// Test 10: Create via DataChangeRequest - assert IPersistenceService received new node.
    /// </summary>
    [Fact]
    public async Task Create_ViaDataChangeRequest_AssertPersistenceReceived()
    {
        var client = GetClient();
        var graphAddress = new Address("graph");

        // Act - create new org via DataChangeRequest
        var newOrg = new MeshNode("graph/org3") { Name = "Organization 3", NodeType = "org", Description = "Third org" };
        client.Post(new DataChangeRequest { Creations = [newOrg] }, o => o.WithTarget(graphAddress));
        await Task.Delay(500);

        // Assert - verify IPersistenceService received the new node
        var persistedOrg = await Persistence.GetNodeAsync("graph/org3");
        persistedOrg.Should().NotBeNull("new org should be persisted");
        persistedOrg!.Name.Should().Be("Organization 3");
    }

    /// <summary>
    /// Test 11: Delete via DataChangeRequest - assert IPersistenceService removed node.
    /// </summary>
    [Fact]
    public async Task Delete_ViaDataChangeRequest_AssertPersistenceRemoved()
    {
        var client = GetClient();
        var graphAddress = new Address("graph");

        // Act - delete org2 via DataChangeRequest
        var nodeToDelete = new MeshNode("graph/org2");
        client.Post(new DataChangeRequest { Deletions = [nodeToDelete] }, o => o.WithTarget(graphAddress));
        await Task.Delay(500);

        // Assert - verify IPersistenceService removed node
        var persisted = await Persistence.GetNodeAsync("graph/org2");
        persisted.Should().BeNull("org2 should be deleted from persistence");
    }

    #endregion
}
