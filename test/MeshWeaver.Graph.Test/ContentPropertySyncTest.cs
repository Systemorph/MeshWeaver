using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Tests for bidirectional property synchronization between content types and MeshNode.
/// With the simplified architecture, content ONLY lives inside MeshNode.Content.
/// There is NO separate content stream - use workspace.GetStream&lt;MeshNode&gt;() and access node.Content.
/// </summary>
public class ContentPropertySyncTest(ITestOutputHelper output) : HubTestBase(output)
{
    private InMemoryStorageAdapter _persistence = null!;
    private static readonly JsonSerializerOptions JsonOptions = new();

    protected override MessageHubConfiguration ConfigureMesh(MessageHubConfiguration conf)
    {
        _persistence = new InMemoryStorageAdapter();

        return conf
            .WithServices(services => services
                .AddInMemoryPersistence(_persistence))
            .WithRoutes(forward => forward
                .RouteAddressToHostedHub(HostType, AddUpdateHandler)
                .RouteAddressToHostedHub(ClientType, ConfigureClient));
    }

    private MessageHubConfiguration AddUpdateHandler(MessageHubConfiguration c)
        => c.WithHandler<UpdateNodeRequest>(HandleUpdateNodeRequest);

    private async Task<IMessageDelivery> HandleUpdateNodeRequest(
        IMessageHub hub, IMessageDelivery<UpdateNodeRequest> request, CancellationToken ct)
    {
        var node = request.Message.Node;
        await _persistence.SaveNode(node, JsonOptions).FirstAsync().ToTask(ct);
        hub.Post(UpdateNodeResponse.Ok(node), o => o.ResponseFor(request));
        return request.Processed();
    }

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return AddUpdateHandler(base.ConfigureHost(configuration)
            .AddMeshDataSource(ds => ds.WithContentType<TodoItem>()));
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration);

    private IMessageHub GetHostWithHandler(string hostId, Func<MessageHubConfiguration, MessageHubConfiguration> config)
        => Mesh.GetHostedHub(new Address(HostType, hostId), c => AddUpdateHandler(config(c)));

    private string GetHubPath(string hostId = "1") => $"{HostType}/{hostId}";

    private void SetupInitialNode(string hubPath, object content, string nodeType = "todo")
    {
        var node = MeshNode.FromPath(hubPath) with
        {
            Name = "Initial Name",
            NodeType = nodeType,
            Content = content
        };
        _persistence.SaveNode(node, JsonOptions).Should().Emit();
    }

    /// <summary>
    /// Re-reads the persisted node by polling the one-shot <c>Read</c> snapshot until
    /// <paramref name="predicate"/> holds — covers the debounced persistence flush without a fixed delay.
    /// </summary>
    private MeshNode? WaitForPersisted(string hubPath, Func<MeshNode?, bool> predicate)
        => Observable.Interval(50.Milliseconds()).StartWith(0L)
            .SelectMany(_ => _persistence.Read(hubPath, JsonOptions))
            .Should().Within(5.Seconds()).Match(predicate);

    [HubFact]
    public void MeshNode_LoadsWithContentFromPersistence()
    {
        // Arrange - Create initial node with content matching the host address
        var hubPath = GetHubPath();
        var initialTodo = new TodoItem { Id = "1", Title = "Original Title", Description = "Original Desc" };
        SetupInitialNode(hubPath, initialTodo);

        var host = GetHost();
        var workspace = host.GetWorkspace();

        // Wait for initial data load - content is inside MeshNode.Content
        var nodeStream = workspace.GetStream<MeshNode>()!;

        var nodes = nodeStream
            .Should().Within(5.Seconds()).Match(items => items?.Any() == true)!;

        nodes.Should().NotBeNull();
        var node = nodes!.First();

        // Content is accessed via MeshNode.Content
        node.Content.Should().NotBeNull();
        node.Name.Should().Be("Initial Name");
    }

    [HubFact]
    public void MeshNodeUpdate_UpdatesContentInMeshNode()
    {
        // Arrange - Create initial node with content
        var hubPath = GetHubPath();
        var initialTodo = new TodoItem { Id = "1", Title = "Original Title", Description = "Original Desc" };
        SetupInitialNode(hubPath, initialTodo);

        var host = GetHost();
        var workspace = host.GetWorkspace();

        // Wait for initial data load
        var nodeStream = workspace.GetStream<MeshNode>()!;

        var initialNodes = nodeStream
            .Should().Within(5.Seconds()).Match(items => items?.Any() == true)!;
        initialNodes.Should().NotBeNull();

        // Act - Update MeshNode with new content
        var node = initialNodes.First();
        var updatedTodo = new TodoItem { Id = "1", Title = "Updated Title", Description = "Updated Desc" };
        var updatedNode = node with
        {
            Content = updatedTodo,
            Name = "Updated Title"
        };
        workspace.RequestChange(DataChangeRequest.Update([updatedNode]), null, null);

        // Wait for update to be reflected in the stream (reactive query)
        nodeStream!
            .Should().Within(5.Seconds()).Match(items => items?.FirstOrDefault()?.Name == "Updated Title");

        // Assert - MeshNode should have updated content in persistence
        // (poll the debounced persistence flush instead of a fixed delay)
        var meshNode = WaitForPersisted(hubPath, n => n?.Name == "Updated Title");
        meshNode.Should().NotBeNull();
        meshNode!.Name.Should().Be("Updated Title");
    }

    [HubFact]
    public void MeshNode_WithAttributeMappedContent_LoadsCorrectly()
    {
        // Arrange - Use content type with explicit attribute mapping
        var hubPath = GetHubPath("attr");
        var initialContent = new AttributeMappedItem { Id = "1", DisplayTitle = "Original", Notes = "Original Notes" };
        SetupInitialNode(hubPath, initialContent, "mapped");

        // Create a new host that uses AttributeMappedItem
        var host = GetHostWithHandler("attr", c => c
            .AddMeshDataSource(ds => ds.WithContentType<AttributeMappedItem>()));

        var workspace = host.GetWorkspace();

        // Wait for initial data load - content is inside MeshNode
        var nodeStream = workspace.GetStream<MeshNode>()!;

        var nodes = nodeStream
            .Should().Within(5.Seconds()).Match(items => items?.Any() == true)!;
        nodes.Should().NotBeNull();

        var node = nodes!.First();
        node.Content.Should().NotBeNull();
        node.Name.Should().Be("Initial Name");
    }

    [HubFact]
    public void MeshNode_WithNamedContent_LoadsCorrectly()
    {
        // Arrange - Use content type that implements INamed
        var hubPath = GetHubPath("named");
        var initialContent = new NamedItem { Id = "1", FirstName = "John", LastName = "Doe" };
        SetupInitialNode(hubPath, initialContent, "named");

        var host = GetHostWithHandler("named", c => c
            .AddMeshDataSource(ds => ds.WithContentType<NamedItem>()));

        var workspace = host.GetWorkspace();

        // Wait for initial data load - content is inside MeshNode
        var nodeStream = workspace.GetStream<MeshNode>();
        var nodes = nodeStream!
            .Should().Within(5.Seconds()).Match(items => items?.Any() == true);

        var node = nodes!.First();
        node.Content.Should().NotBeNull();
    }

    [HubFact]
    public void MeshNodeUpdate_PreservesExistingValues()
    {
        // Arrange
        var hubPath = GetHubPath("minimal");
        var initialContent = new MinimalItem { Id = "1", Data = "some data" };

        var node = MeshNode.FromPath(hubPath) with
        {
            Name = "Manually Set Name",
            NodeType = "minimal",
            Content = initialContent
        };
        _persistence.SaveNode(node, JsonOptions).Should().Emit();

        var host = GetHostWithHandler("minimal", c => c
            .AddMeshDataSource(ds => ds.WithContentType<MinimalItem>()));

        var workspace = host.GetWorkspace();

        // Wait for initial data load
        var nodeStream = workspace.GetStream<MeshNode>();
        var nodes = nodeStream!
            .Should().Within(5.Seconds()).Match(items => items?.Any() == true);

        // Act - Update the content inside MeshNode
        var currentNode = nodes!.First();
        var updatedContent = new MinimalItem { Id = "1", Data = "updated data" };
        var updatedNode = currentNode with { Content = updatedContent };
        workspace.RequestChange(DataChangeRequest.Update([updatedNode]), null, null);

        // Wait for update to be reflected in the stream (reactive query)
        // Since MinimalItem has no [MeshNodeProperty] mappings, we wait for Content change
        nodeStream!
            .Should().Within(5.Seconds()).Match(items =>
            {
                var n = items?.FirstOrDefault();
                if (n?.Content is MinimalItem m)
                    return m.Data == "updated data";
                return false;
            });

        // Assert - MeshNode should preserve manually set Name
        // (poll the debounced persistence flush instead of a fixed delay)
        var meshNode = WaitForPersisted(hubPath, n => n?.Content is MinimalItem m && m.Data == "updated data");
        meshNode.Should().NotBeNull();
        meshNode!.Name.Should().Be("Manually Set Name", "MeshNode.Name should be preserved");
    }

    [HubFact]
    public void MeshNodeUpdate_SyncsAllFourProperties()
    {
        // Arrange - Content type with all four MeshNode property mappings via attributes
        var hubPath = GetHubPath("full-attr");
        var initialContent = new FullMappingItem
        {
            Id = "1",
            DisplayName = "Original Name",
            Summary = "Original Description",
            IconName = "Document",
            Group = "General"
        };
        SetupInitialNode(hubPath, initialContent, "full");

        var host = GetHostWithHandler("full-attr", c => c
            .AddMeshDataSource(ds => ds.WithContentType<FullMappingItem>()));

        var workspace = host.GetWorkspace();

        // Wait for initial data load
        var nodeStream = workspace.GetStream<MeshNode>();
        var nodes = nodeStream!
            .Should().Within(5.Seconds()).Match(items => items?.Any() == true);

        // Act - Update MeshNode with new values
        var currentNode = nodes!.First();
        var updatedNode = currentNode with
        {
            Name = "Updated Name",
            Icon = "Star",
            Category = "Premium"
        };
        workspace.RequestChange(DataChangeRequest.Update([updatedNode]), null, null);

        // Wait for update to be reflected in the stream (reactive query)
        nodeStream!
            .Should().Within(5.Seconds()).Match(items => items?.FirstOrDefault()?.Name == "Updated Name");

        // Assert - All four MeshNode properties should be persisted
        // (poll the debounced persistence flush instead of a fixed delay)
        var meshNode = WaitForPersisted(hubPath, n => n?.Name == "Updated Name");
        meshNode.Should().NotBeNull();
        meshNode!.Name.Should().Be("Updated Name");
        meshNode.Icon.Should().Be("Star");
        meshNode.Category.Should().Be("Premium");
    }

    [HubFact]
    public void MeshNode_ConventionalProperties_LoadCorrectly()
    {
        // Arrange - Content type with conventional property names
        var hubPath = GetHubPath("full-conv");
        var initialContent = new ConventionalFullItem
        {
            Id = "1",
            Name = "Original Name",
            Description = "Original Description",
            Icon = "Document",
            Category = "General"
        };
        SetupInitialNode(hubPath, initialContent, "fullconv");

        var host = GetHostWithHandler("full-conv", c => c
            .AddMeshDataSource(ds => ds.WithContentType<ConventionalFullItem>()));

        var workspace = host.GetWorkspace();

        // Wait for initial data load
        var nodeStream = workspace.GetStream<MeshNode>();
        var nodes = nodeStream!
            .Should().Within(5.Seconds()).Match(items => items?.Any() == true);

        // Assert - MeshNode should be loaded
        var node = nodes!.First();
        node.Should().NotBeNull();
        node.Content.Should().NotBeNull();
    }
}

/// <summary>
/// Test content type using convention-based mapping (Title -> Name, Description -> Description).
/// </summary>
public record TodoItem
{
    [Key]
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
}

/// <summary>
/// Test content type using explicit attribute mapping.
/// </summary>
public record AttributeMappedItem
{
    [Key]
    public string Id { get; init; } = "";

    [MeshNodeProperty("Name")]
    public string DisplayTitle { get; init; } = "";

    public string Notes { get; init; } = "";
}

/// <summary>
/// Test content type implementing INamed interface.
/// </summary>
public record NamedItem : INamed
{
    [Key]
    public string Id { get; init; } = "";
    public string FirstName { get; init; } = "";
    public string LastName { get; init; } = "";

    public string DisplayName => $"{FirstName} {LastName}";
}

/// <summary>
/// Test content type with no mappable properties.
/// </summary>
public record MinimalItem
{
    [Key]
    public string Id { get; init; } = "";
    public string Data { get; init; } = "";
}

/// <summary>
/// Test content type with both attribute and conventional properties.
/// </summary>
public record MixedMappingItem
{
    [Key]
    public string Id { get; init; } = "";

    public string Title { get; init; } = "";  // Convention would map this to Name

    [MeshNodeProperty("Name")]
    public string CustomName { get; init; } = "";  // Attribute should take priority
}

/// <summary>
/// Test content type with all four MeshNode property mappings via attributes.
/// </summary>
public record FullMappingItem
{
    [Key]
    public string Id { get; init; } = "";

    [MeshNodeProperty("Name")]
    public string DisplayName { get; init; } = "";

    public string Summary { get; init; } = "";

    [MeshNodeProperty("Icon")]
    public string IconName { get; init; } = "";

    [MeshNodeProperty("Category")]
    public string Group { get; init; } = "";
}

/// <summary>
/// Test content type with all four MeshNode properties via convention.
/// </summary>
public record ConventionalFullItem
{
    [Key]
    public string Id { get; init; } = "";

    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Icon { get; init; } = "";
    public string Category { get; init; } = "";
}
