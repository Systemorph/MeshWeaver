using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Dedicated tests for MeshNodeTypeSource functionality.
/// Tests persistence, loading, and synchronization of MeshNode data.
/// With the simplified architecture, content ONLY lives inside MeshNode.Content.
/// </summary>
public class MeshNodeTypeSourceTest(ITestOutputHelper output) : HubTestBase(output)
{
    private InMemoryStorageAdapter _persistence = null!;
    private static readonly JsonSerializerOptions JsonOptions = new();

    protected override MessageHubConfiguration ConfigureMesh(MessageHubConfiguration conf)
    {
        _persistence = new InMemoryStorageAdapter();

        return conf
            .WithServices(services => services.AddInMemoryPersistence(_persistence))
            .WithRoutes(forward => forward
                .RouteAddressToHostedHub(HostType, c => c)
                .RouteAddressToHostedHub(ClientType, ConfigureClient));
    }

    private IMessageHub GetHostWithHandler(string hostId, Func<MessageHubConfiguration, MessageHubConfiguration> config)
        => Mesh.GetHostedHub(new Address(HostType, hostId), config);

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .AddMeshDataSource(ds => ds.WithContentType<TestContent>());
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration);

    private string GetHubPath(string hostId = "1") => $"{HostType}/{hostId}";

    /// <summary>
    /// Re-reads the persisted node by polling the one-shot <c>Read</c> snapshot until
    /// <paramref name="predicate"/> holds — covers the debounced persistence flush without a fixed delay.
    /// </summary>
    private MeshNode? WaitForPersisted(string hubPath, Func<MeshNode?, bool> predicate)
        => Observable.Interval(50.Milliseconds()).StartWith(0L)
            .SelectMany(_ => _persistence.Read(hubPath, JsonOptions))
            .Should().Within(5.Seconds()).Match(predicate);

    [HubFact]
    public void MeshNodeTypeSource_LoadsNodeFromPersistence()
    {
        // Arrange - Save a node to persistence
        var hubPath = GetHubPath("load-test");
        var content = new TestContent { Id = "1", Title = "Test Item", Notes = "Test notes" };
        var node = MeshNode.FromPath(hubPath) with
        {
            Name = "Test Node",
            Icon = "Star",
            Category = "Testing",
            NodeType = "test",
            Content = content
        };
        _persistence.SaveNode(node, JsonOptions).Should().Emit();

        // Act - Start a hub for that path
        var host = GetHostWithHandler("load-test", c => c
            .AddMeshDataSource(ds => ds.WithContentType<TestContent>()));

        var workspace = host.GetWorkspace();

        // Assert - MeshNode should be loaded with correct properties
        var meshNodeStream = workspace.GetStream<MeshNode>();
        var loadedNodes = meshNodeStream!
            .Should().Within(5.Seconds()).Match(nodes => nodes?.Any() == true);

        loadedNodes.Should().NotBeNull();
        var loadedNode = loadedNodes!.FirstOrDefault();
        loadedNode.Should().NotBeNull();
        loadedNode!.Name.Should().Be("Test Node");
        loadedNode.Icon.Should().Be("Star");
        loadedNode.Category.Should().Be("Testing");
        loadedNode.Content.Should().NotBeNull();
    }

    [HubFact]
    public void MeshNodeTypeSource_PersistsChanges()
    {
        // Arrange
        var hubPath = GetHubPath("persist-test");
        var content = new TestContent { Id = "1", Title = "Original", Notes = "Original notes" };
        var node = MeshNode.FromPath(hubPath) with
        {
            Name = "Original Name",
            NodeType = "test",
            Content = content
        };
        _persistence.SaveNode(node, JsonOptions).Should().Emit();

        var host = GetHostWithHandler("persist-test", c => c
            .AddMeshDataSource(ds => ds.WithContentType<TestContent>()));

        var workspace = host.GetWorkspace();

        // Wait for initial load
        var meshNodeStream = workspace.GetStream<MeshNode>();
        meshNodeStream!
            .Should().Within(5.Seconds()).Match(nodes => nodes?.Any() == true);

        // Act - Update the node via workspace
        var updatedNode = node with
        {
            Name = "Updated Name"
        };
        workspace.RequestChange(DataChangeRequest.Update([updatedNode]), null, null);

        // Wait for the update to be reflected in the stream (reactive query)
        meshNodeStream!
            .Should().Within(5.Seconds()).Match(nodes => nodes?.FirstOrDefault()?.Name == "Updated Name");

        // Assert - Changes should be persisted
        // (poll the debounced persistence flush instead of a fixed delay)
        var persistedNode = WaitForPersisted(hubPath, n => n?.Name == "Updated Name");
        persistedNode.Should().NotBeNull();
        persistedNode!.Name.Should().Be("Updated Name");
    }

    [HubFact]
    public void MeshNodeTypeSource_UpdatesContentInMeshNode()
    {
        // Arrange
        var hubPath = GetHubPath("sync-test");
        var content = new TestContent { Id = "1", Title = "Initial Title", Notes = "Initial notes" };
        var node = MeshNode.FromPath(hubPath) with
        {
            Name = "Node Name",
            NodeType = "test",
            Content = content
        };
        _persistence.SaveNode(node, JsonOptions).Should().Emit();

        var host = GetHostWithHandler("sync-test", c => c
            .AddMeshDataSource(ds => ds.WithContentType<TestContent>()));

        var workspace = host.GetWorkspace();

        // Wait for initial load - content is inside MeshNode.Content
        var nodeStream = workspace.GetStream<MeshNode>();
        var nodes = nodeStream!
            .Should().Within(5.Seconds()).Match(items => items?.Any() == true);

        // Act - Update MeshNode with new content
        var currentNode = nodes!.First();
        var updatedContent = new TestContent { Id = "1", Title = "Synced Title", Notes = "Synced notes" };
        var updatedNode = currentNode with
        {
            Content = updatedContent,
            Name = "Synced Title"
        };
        workspace.RequestChange(DataChangeRequest.Update([updatedNode]), null, null);

        // Wait for the update to be reflected in the stream (reactive query)
        nodeStream!
            .Should().Within(5.Seconds()).Match(items => items?.FirstOrDefault()?.Name == "Synced Title");

        // Assert - MeshNode properties should be persisted
        // (poll the debounced persistence flush instead of a fixed delay)
        var persistedNode = WaitForPersisted(hubPath, n => n?.Name == "Synced Title");
        persistedNode.Should().NotBeNull();
        persistedNode!.Name.Should().Be("Synced Title");
    }

    [HubFact]
    public void MeshNodeTypeSource_HandlesTransientState()
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
        _persistence.SaveNode(node, JsonOptions).Should().Emit();

        var host = GetHostWithHandler("transient-test", c => c
            .AddMeshDataSource(ds => ds.WithContentType<TestContent>()));

        var workspace = host.GetWorkspace();

        // Act - Load and verify state
        var meshNodeStream = workspace.GetStream<MeshNode>();
        var loadedNodes = meshNodeStream!
            .Should().Within(5.Seconds()).Match(nodes => nodes?.Any() == true);

        // Assert
        var loadedNode = loadedNodes!.First();
        loadedNode.State.Should().Be(MeshNodeState.Transient);
    }

    [HubFact]
    public void MeshNodeTypeSource_PreservesNodeTypeOnUpdate()
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
        _persistence.SaveNode(node, JsonOptions).Should().Emit();

        var host = GetHostWithHandler("nodetype-test", c => c
            .AddMeshDataSource(ds => ds.WithContentType<TestContent>()));

        var workspace = host.GetWorkspace();

        // Wait for initial load - content is inside MeshNode
        var nodeStream = workspace.GetStream<MeshNode>();
        var nodes = nodeStream!
            .Should().Within(5.Seconds()).Match(items => items?.Any() == true);

        // Act - Update MeshNode
        var currentNode = nodes!.First();
        var updatedContent = new TestContent { Id = "1", Title = "Updated Title", Notes = "Notes" };
        var updatedNode = currentNode with { Content = updatedContent, Name = "Updated Title" };
        workspace.RequestChange(DataChangeRequest.Update([updatedNode]), null, null);

        // Wait for the update to be reflected in the stream (reactive query)
        nodeStream!
            .Should().Within(5.Seconds()).Match(items => items?.FirstOrDefault()?.Name == "Updated Title");

        // Assert - NodeType should be preserved
        // (poll the debounced persistence flush instead of a fixed delay)
        var persistedNode = WaitForPersisted(hubPath, n => n?.NodeType == "ACME/Project/Todo" && n?.Name == "Updated Title");
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

    public string Notes { get; init; } = "";
}
