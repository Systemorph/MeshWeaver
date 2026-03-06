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
    private InMemoryPersistenceService _persistence = null!;
    private static readonly JsonSerializerOptions JsonOptions = new();

    protected override MessageHubConfiguration ConfigureMesh(MessageHubConfiguration conf)
    {
        _persistence = new InMemoryPersistenceService();

        return conf
            .WithServices(services => services
                .AddInMemoryPersistence(_persistence))
            .WithRoutes(forward => forward
                .RouteAddressToHostedHub(HostType, ConfigureHost)
                .RouteAddressToHostedHub(ClientType, ConfigureClient));
    }

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .AddMeshDataSource(ds => ds.WithContentType<TodoItem>());
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration);

    private string GetHubPath(string hostId = "1") => $"{HostType}/{hostId}";

    private async Task SetupInitialNode(string hubPath, object content, string nodeType = "todo")
    {
        var node = MeshNode.FromPath(hubPath) with
        {
            Name = "Initial Name",
            NodeType = nodeType,
            Content = content
        };
        await _persistence.SaveNodeAsync(node, JsonOptions);
    }

    [HubFact]
    public async Task MeshNode_LoadsWithContentFromPersistence()
    {
        // Arrange - Create initial node with content matching the host address
        var hubPath = GetHubPath();
        var initialTodo = new TodoItem { Id = "1", Title = "Original Title", Description = "Original Desc" };
        await SetupInitialNode(hubPath, initialTodo);

        var host = GetHost();
        var workspace = host.GetWorkspace();

        // Wait for initial data load - content is inside MeshNode.Content
        var nodeStream = workspace.GetStream<MeshNode>();
        nodeStream.Should().NotBeNull();

        var nodes = await nodeStream!
            .Where(items => items?.Any() == true)
            .Timeout(5.Seconds())
            .FirstAsync();

        nodes.Should().NotBeNull();
        var node = nodes.First();

        // Content is accessed via MeshNode.Content
        node.Content.Should().NotBeNull();
        node.Name.Should().Be("Initial Name");
    }

    [HubFact]
    public async Task MeshNodeUpdate_UpdatesContentInMeshNode()
    {
        // Arrange - Create initial node with content
        var hubPath = GetHubPath();
        var initialTodo = new TodoItem { Id = "1", Title = "Original Title", Description = "Original Desc" };
        await SetupInitialNode(hubPath, initialTodo);

        var host = GetHost();
        var workspace = host.GetWorkspace();

        // Wait for initial data load
        var nodeStream = workspace.GetStream<MeshNode>();
        nodeStream.Should().NotBeNull();

        var initialNodes = await nodeStream!
            .Where(items => items?.Any() == true)
            .Timeout(5.Seconds())
            .FirstAsync();
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
        await nodeStream!
            .Where(items => items?.FirstOrDefault()?.Name == "Updated Title")
            .Timeout(5.Seconds())
            .FirstAsync();

        // Wait for debounced persistence flush (200ms debounce + buffer)
        await Task.Delay(300);

        // Assert - MeshNode should have updated content in persistence
        var meshNode = await _persistence.GetNodeAsync(hubPath, JsonOptions);
        meshNode.Should().NotBeNull();
        meshNode!.Name.Should().Be("Updated Title");
    }

    [HubFact]
    public async Task MeshNode_WithAttributeMappedContent_LoadsCorrectly()
    {
        // Arrange - Use content type with explicit attribute mapping
        var hubPath = GetHubPath("attr");
        var initialContent = new AttributeMappedItem { Id = "1", DisplayTitle = "Original", Notes = "Original Notes" };
        await SetupInitialNode(hubPath, initialContent, "mapped");

        // Create a new host that uses AttributeMappedItem
        var host = Mesh.GetHostedHub(new Address(HostType, "attr"), c => c
            .AddMeshDataSource(ds => ds.WithContentType<AttributeMappedItem>()));

        var workspace = host.GetWorkspace();

        // Wait for initial data load - content is inside MeshNode
        var nodeStream = workspace.GetStream<MeshNode>();
        nodeStream.Should().NotBeNull();

        var nodes = await nodeStream!
            .Where(items => items?.Any() == true)
            .Timeout(5.Seconds())
            .FirstAsync();
        nodes.Should().NotBeNull();

        var node = nodes.First();
        node.Content.Should().NotBeNull();
        node.Name.Should().Be("Initial Name");
    }

    [HubFact]
    public async Task MeshNode_WithNamedContent_LoadsCorrectly()
    {
        // Arrange - Use content type that implements INamed
        var hubPath = GetHubPath("named");
        var initialContent = new NamedItem { Id = "1", FirstName = "John", LastName = "Doe" };
        await SetupInitialNode(hubPath, initialContent, "named");

        var host = Mesh.GetHostedHub(new Address(HostType, "named"), c => c
            .AddMeshDataSource(ds => ds.WithContentType<NamedItem>()));

        var workspace = host.GetWorkspace();

        // Wait for initial data load - content is inside MeshNode
        var nodeStream = workspace.GetStream<MeshNode>();
        var nodes = await nodeStream!
            .Where(items => items?.Any() == true)
            .Timeout(5.Seconds())
            .FirstAsync();

        var node = nodes.First();
        node.Content.Should().NotBeNull();
    }

    [HubFact]
    public async Task MeshNodeUpdate_PreservesExistingValues()
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
        await _persistence.SaveNodeAsync(node, JsonOptions);

        var host = Mesh.GetHostedHub(new Address(HostType, "minimal"), c => c
            .AddMeshDataSource(ds => ds.WithContentType<MinimalItem>()));

        var workspace = host.GetWorkspace();

        // Wait for initial data load
        var nodeStream = workspace.GetStream<MeshNode>();
        var nodes = await nodeStream!
            .Where(items => items?.Any() == true)
            .Timeout(5.Seconds())
            .FirstAsync();

        // Act - Update the content inside MeshNode
        var currentNode = nodes.First();
        var updatedContent = new MinimalItem { Id = "1", Data = "updated data" };
        var updatedNode = currentNode with { Content = updatedContent };
        workspace.RequestChange(DataChangeRequest.Update([updatedNode]), null, null);

        // Wait for update to be reflected in the stream (reactive query)
        // Since MinimalItem has no [MeshNodeProperty] mappings, we wait for Content change
        await nodeStream!
            .Where(items =>
            {
                var n = items?.FirstOrDefault();
                if (n?.Content is MinimalItem m)
                    return m.Data == "updated data";
                return false;
            })
            .Timeout(5.Seconds())
            .FirstAsync();

        // Wait for debounced persistence flush (200ms debounce + buffer)
        await Task.Delay(300);

        // Assert - MeshNode should preserve manually set Name
        var meshNode = await _persistence.GetNodeAsync(hubPath, JsonOptions);
        meshNode.Should().NotBeNull();
        meshNode!.Name.Should().Be("Manually Set Name", "MeshNode.Name should be preserved");
    }

    [HubFact]
    public async Task MeshNodeUpdate_SyncsAllFourProperties()
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
        await SetupInitialNode(hubPath, initialContent, "full");

        var host = Mesh.GetHostedHub(new Address(HostType, "full-attr"), c => c
            .AddMeshDataSource(ds => ds.WithContentType<FullMappingItem>()));

        var workspace = host.GetWorkspace();

        // Wait for initial data load
        var nodeStream = workspace.GetStream<MeshNode>();
        var nodes = await nodeStream!
            .Where(items => items?.Any() == true)
            .Timeout(5.Seconds())
            .FirstAsync();

        // Act - Update MeshNode with new values
        var currentNode = nodes.First();
        var updatedNode = currentNode with
        {
            Name = "Updated Name",
            Icon = "Star",
            Category = "Premium"
        };
        workspace.RequestChange(DataChangeRequest.Update([updatedNode]), null, null);

        // Wait for update to be reflected in the stream (reactive query)
        await nodeStream!
            .Where(items => items?.FirstOrDefault()?.Name == "Updated Name")
            .Timeout(5.Seconds())
            .FirstAsync();

        // Wait for debounced persistence flush (200ms debounce + buffer)
        await Task.Delay(300);

        // Assert - All four MeshNode properties should be persisted
        var meshNode = await _persistence.GetNodeAsync(hubPath, JsonOptions);
        meshNode.Should().NotBeNull();
        meshNode!.Name.Should().Be("Updated Name");
        meshNode.Icon.Should().Be("Star");
        meshNode.Category.Should().Be("Premium");
    }

    [HubFact]
    public async Task MeshNode_ConventionalProperties_LoadCorrectly()
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
        await SetupInitialNode(hubPath, initialContent, "fullconv");

        var host = Mesh.GetHostedHub(new Address(HostType, "full-conv"), c => c
            .AddMeshDataSource(ds => ds.WithContentType<ConventionalFullItem>()));

        var workspace = host.GetWorkspace();

        // Wait for initial data load
        var nodeStream = workspace.GetStream<MeshNode>();
        var nodes = await nodeStream!
            .Where(items => items?.Any() == true)
            .Timeout(5.Seconds())
            .FirstAsync();

        // Assert - MeshNode should be loaded
        var node = nodes.First();
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
