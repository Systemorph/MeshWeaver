using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Dedicated tests for MeshNodeTypeSource functionality.
/// Tests persistence, loading, and synchronization of MeshNode data.
/// </summary>
public class MeshNodeTypeSourceTest(ITestOutputHelper output) : HubTestBase(output)
{
    private InMemoryPersistenceService _persistence = null!;
    private static readonly JsonSerializerOptions JsonOptions = new();

    protected override MessageHubConfiguration ConfigureMesh(MessageHubConfiguration conf)
    {
        _persistence = new InMemoryPersistenceService();

        return conf
            .WithServices(services => services
                .AddSingleton<IPersistenceServiceCore>(_persistence)
                .AddSingleton<IPersistenceService>(sp =>
                    new PersistenceService(sp.GetRequiredService<IPersistenceServiceCore>(), sp.GetRequiredService<IMessageHub>())))
            .WithRoutes(forward => forward
                .RouteAddressToHostedHub(HostType, ConfigureHost)
                .RouteAddressToHostedHub(ClientType, ConfigureClient));
    }

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .AddMeshDataSource(ds => ds.WithContentType<TestContent>());
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration);

    private string GetHubPath(string hostId = "1") => $"{HostType}/{hostId}";

    [HubFact]
    public async Task MeshNodeTypeSource_LoadsNodeFromPersistence()
    {
        // Arrange - Save a node to persistence
        var hubPath = GetHubPath("load-test");
        var content = new TestContent { Id = "1", Title = "Test Item", Notes = "Test notes" };
        var node = MeshNode.FromPath(hubPath) with
        {
            Name = "Test Node",
            Description = "Test Description",
            Icon = "Star",
            Category = "Testing",
            NodeType = "test",
            Content = content
        };
        await _persistence.SaveNodeAsync(node, JsonOptions);

        // Act - Start a hub for that path
        var host = Mesh.GetHostedHub(new Address(HostType, "load-test"), c => c
            .AddMeshDataSource(ds => ds.WithContentType<TestContent>()));

        var workspace = host.GetWorkspace();

        // Assert - MeshNode should be loaded with correct properties
        var meshNodeStream = workspace.GetStream<MeshNode>();
        var loadedNodes = await meshNodeStream!
            .Where(nodes => nodes?.Any() == true)
            .Timeout(5.Seconds())
            .FirstAsync();

        loadedNodes.Should().NotBeNull();
        var loadedNode = loadedNodes.FirstOrDefault();
        loadedNode.Should().NotBeNull();
        loadedNode!.Name.Should().Be("Test Node");
        loadedNode.Description.Should().Be("Test Description");
        loadedNode.Icon.Should().Be("Star");
        loadedNode.Category.Should().Be("Testing");
    }

    [HubFact]
    public async Task MeshNodeTypeSource_PersistsChanges()
    {
        // Arrange
        var hubPath = GetHubPath("persist-test");
        var content = new TestContent { Id = "1", Title = "Original", Notes = "Original notes" };
        var node = MeshNode.FromPath(hubPath) with
        {
            Name = "Original Name",
            Description = "Original Description",
            NodeType = "test",
            Content = content
        };
        await _persistence.SaveNodeAsync(node, JsonOptions);

        var host = Mesh.GetHostedHub(new Address(HostType, "persist-test"), c => c
            .AddMeshDataSource(ds => ds.WithContentType<TestContent>()));

        var workspace = host.GetWorkspace();

        // Wait for initial load
        var meshNodeStream = workspace.GetStream<MeshNode>();
        await meshNodeStream!
            .Where(nodes => nodes?.Any() == true)
            .Timeout(5.Seconds())
            .FirstAsync();

        // Act - Update the node via workspace
        var updatedNode = node with
        {
            Name = "Updated Name",
            Description = "Updated Description"
        };
        workspace.RequestChange(DataChangeRequest.Update([updatedNode]), null, null);

        // Wait for persistence
        await Task.Delay(500);

        // Assert - Changes should be persisted
        var persistedNode = await _persistence.GetNodeAsync(hubPath, JsonOptions);
        persistedNode.Should().NotBeNull();
        persistedNode!.Name.Should().Be("Updated Name");
        persistedNode.Description.Should().Be("Updated Description");
    }

    [HubFact]
    public async Task MeshNodeTypeSource_SyncsContentToMeshNodeProperties()
    {
        // Arrange
        var hubPath = GetHubPath("sync-test");
        var content = new TestContent { Id = "1", Title = "Initial Title", Notes = "Initial notes" };
        var node = MeshNode.FromPath(hubPath) with
        {
            Name = "Node Name",
            Description = "Node Description",
            NodeType = "test",
            Content = content
        };
        await _persistence.SaveNodeAsync(node, JsonOptions);

        var host = Mesh.GetHostedHub(new Address(HostType, "sync-test"), c => c
            .AddMeshDataSource(ds => ds.WithContentType<TestContent>()));

        var workspace = host.GetWorkspace();

        // Wait for initial load
        var contentStream = workspace.GetStream<TestContent>();
        await contentStream!
            .Where(items => items?.Any() == true)
            .Timeout(5.Seconds())
            .FirstAsync();

        // Act - Update content
        var updatedContent = content with { Title = "Synced Title", Notes = "Synced notes" };
        workspace.RequestChange(DataChangeRequest.Update([updatedContent]), null, null);

        await Task.Delay(500);

        // Assert - MeshNode properties should be synced from content
        var persistedNode = await _persistence.GetNodeAsync(hubPath, JsonOptions);
        persistedNode.Should().NotBeNull();
        persistedNode!.Name.Should().Be("Synced Title", "MeshNode.Name should sync from Content.Title");
        persistedNode.Description.Should().Be("Synced notes", "MeshNode.Description should sync from Content.Notes");
    }

    [HubFact]
    public async Task MeshNodeTypeSource_HandlesTransientState()
    {
        // Arrange - Create a transient node
        var hubPath = GetHubPath("transient-test");
        var content = new TestContent { Id = "1", Title = "Transient Item", Notes = "Draft" };
        var node = MeshNode.FromPath(hubPath) with
        {
            Name = "Transient Node",
            State = MeshNodeState.Transient,
            NodeType = "test",
            Content = content
        };
        await _persistence.SaveNodeAsync(node, JsonOptions);

        var host = Mesh.GetHostedHub(new Address(HostType, "transient-test"), c => c
            .AddMeshDataSource(ds => ds.WithContentType<TestContent>()));

        var workspace = host.GetWorkspace();

        // Act - Load and verify state
        var meshNodeStream = workspace.GetStream<MeshNode>();
        var loadedNodes = await meshNodeStream!
            .Where(nodes => nodes?.Any() == true)
            .Timeout(5.Seconds())
            .FirstAsync();

        // Assert
        var loadedNode = loadedNodes.First();
        loadedNode.State.Should().Be(MeshNodeState.Transient);
    }

    [HubFact]
    public async Task MeshNodeTypeSource_PreservesNodeTypeOnContentUpdate()
    {
        // Arrange
        var hubPath = GetHubPath("nodetype-test");
        var content = new TestContent { Id = "1", Title = "Test", Notes = "Notes" };
        var node = MeshNode.FromPath(hubPath) with
        {
            Name = "Test Node",
            NodeType = "ACME/Project/Todo",
            Content = content
        };
        await _persistence.SaveNodeAsync(node, JsonOptions);

        var host = Mesh.GetHostedHub(new Address(HostType, "nodetype-test"), c => c
            .AddMeshDataSource(ds => ds.WithContentType<TestContent>()));

        var workspace = host.GetWorkspace();

        // Wait for initial load
        var contentStream = workspace.GetStream<TestContent>();
        await contentStream!
            .Where(items => items?.Any() == true)
            .Timeout(5.Seconds())
            .FirstAsync();

        // Act - Update content
        var updatedContent = content with { Title = "Updated Title" };
        workspace.RequestChange(DataChangeRequest.Update([updatedContent]), null, null);

        await Task.Delay(500);

        // Assert - NodeType should be preserved
        var persistedNode = await _persistence.GetNodeAsync(hubPath, JsonOptions);
        persistedNode.Should().NotBeNull();
        persistedNode!.NodeType.Should().Be("ACME/Project/Todo");
    }
}

/// <summary>
/// Test content type for MeshNodeTypeSource tests.
/// </summary>
public record TestContent
{
    [Key]
    public string Id { get; init; } = "";

    [MeshNodeProperty("Name")]
    public string Title { get; init; } = "";

    [MeshNodeProperty("Description")]
    public string Notes { get; init; } = "";
}
