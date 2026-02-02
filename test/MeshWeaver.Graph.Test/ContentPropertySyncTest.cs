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
/// Tests for bidirectional property synchronization between content types and MeshNode.
/// Verifies that:
/// 1. Updating content syncs Name/Description to MeshNode (content -> MeshNode)
/// 2. Updating MeshNode.Content syncs back to content stream (MeshNode -> content)
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
            Description = "Initial Description",
            NodeType = nodeType,
            Content = content
        };
        await _persistence.SaveNodeAsync(node, JsonOptions);
    }

    [HubFact]
    public async Task ContentUpdate_SyncsNameToMeshNode_ViaConvention()
    {
        // Arrange - Create initial node with content matching the host address
        var hubPath = GetHubPath();
        var initialTodo = new TodoItem { Id = "1", Title = "Original Title", Description = "Original Desc" };
        await SetupInitialNode(hubPath, initialTodo);

        var host = GetHost();
        var workspace = host.GetWorkspace();

        // Wait for initial data load
        var todoStream = workspace.GetStream<TodoItem>();
        todoStream.Should().NotBeNull();

        var initialContent = await todoStream!
            .Where(items => items?.Any() == true)
            .Timeout(5.Seconds())
            .FirstAsync();
        initialContent.Should().NotBeNull();

        // Act - Update the content
        var updatedTodo = initialTodo with { Title = "Updated Title", Description = "Updated Desc" };
        workspace.RequestChange(DataChangeRequest.Update([updatedTodo]), null, null);

        // Wait for sync to complete
        await Task.Delay(500);

        // Assert - MeshNode should have synced Name and Description
        var meshNode = await _persistence.GetNodeAsync(hubPath, JsonOptions);
        meshNode.Should().NotBeNull();
        meshNode!.Name.Should().Be("Updated Title", "MeshNode.Name should sync from TodoItem.Title");
        meshNode.Description.Should().Be("Updated Desc", "MeshNode.Description should sync from TodoItem.Description");
    }

    [HubFact]
    public async Task ContentUpdate_SyncsNameToMeshNode_ViaAttribute()
    {
        // Arrange - Use content type with explicit attribute mapping
        var hubPath = GetHubPath("attr");
        var initialContent = new AttributeMappedItem { Id = "1", DisplayTitle = "Original", Notes = "Original Notes" };
        await SetupInitialNode(hubPath, initialContent, "mapped");

        // Create a new host that uses AttributeMappedItem
        var host = Mesh.GetHostedHub(new Address(HostType, "attr"), c => c
            .AddMeshDataSource(ds => ds.WithContentType<AttributeMappedItem>()));

        var workspace = host.GetWorkspace();

        // Wait for initial data load
        var contentStream = workspace.GetStream<AttributeMappedItem>();
        contentStream.Should().NotBeNull();

        var initialItem = await contentStream!
            .Where(items => items?.Any() == true)
            .Timeout(5.Seconds())
            .FirstAsync();
        initialItem.Should().NotBeNull();

        // Act - Update the content
        var updatedContent = initialContent with { DisplayTitle = "Attribute Title", Notes = "Attribute Notes" };
        workspace.RequestChange(DataChangeRequest.Update([updatedContent]), null, null);

        // Wait for sync to complete
        await Task.Delay(500);

        // Assert - MeshNode should have synced via attribute mapping
        var meshNode = await _persistence.GetNodeAsync(hubPath, JsonOptions);
        meshNode.Should().NotBeNull();
        meshNode!.Name.Should().Be("Attribute Title", "MeshNode.Name should sync from [MeshNodeProperty(\"Name\")] property");
        meshNode.Description.Should().Be("Attribute Notes", "MeshNode.Description should sync from [MeshNodeProperty(\"Description\")] property");
    }

    [HubFact]
    public async Task ContentUpdate_SyncsNameToMeshNode_ViaINamed()
    {
        // Arrange - Use content type that implements INamed
        var hubPath = GetHubPath("named");
        var initialContent = new NamedItem { Id = "1", FirstName = "John", LastName = "Doe" };
        await SetupInitialNode(hubPath, initialContent, "named");

        var host = Mesh.GetHostedHub(new Address(HostType, "named"), c => c
            .AddMeshDataSource(ds => ds.WithContentType<NamedItem>()));

        var workspace = host.GetWorkspace();

        // Wait for initial data load
        var contentStream = workspace.GetStream<NamedItem>();
        var initialItem = await contentStream!
            .Where(items => items?.Any() == true)
            .Timeout(5.Seconds())
            .FirstAsync();

        // Act - Update the content
        var updatedContent = initialContent with { FirstName = "Jane", LastName = "Smith" };
        workspace.RequestChange(DataChangeRequest.Update([updatedContent]), null, null);

        // Wait for sync to complete
        await Task.Delay(500);

        // Assert - MeshNode.Name should sync from INamed.DisplayName
        var meshNode = await _persistence.GetNodeAsync(hubPath, JsonOptions);
        meshNode.Should().NotBeNull();
        meshNode!.Name.Should().Be("Jane Smith", "MeshNode.Name should sync from INamed.DisplayName");
    }

    [HubFact]
    public async Task MeshNodeUpdate_SyncsContentToContentStream()
    {
        // Arrange - Create initial node with content
        var hubPath = GetHubPath();
        var initialTodo = new TodoItem { Id = "1", Title = "Original Title", Description = "Original Desc" };
        await SetupInitialNode(hubPath, initialTodo);

        var host = GetHost();
        var workspace = host.GetWorkspace();

        // Wait for initial data load
        var todoStream = workspace.GetStream<TodoItem>();
        todoStream.Should().NotBeNull();

        var initialContent = await todoStream!
            .Where(items => items?.Any() == true)
            .Timeout(5.Seconds())
            .FirstAsync();
        initialContent.Should().NotBeNull();

        // Act - Update MeshNode with new content (simulating external update)
        var updatedTodo = new TodoItem { Id = "1", Title = "MeshNode Updated", Description = "From MeshNode" };
        var meshNode = await _persistence.GetNodeAsync(hubPath, JsonOptions);
        var updatedNode = meshNode! with { Content = updatedTodo };

        workspace.RequestChange(DataChangeRequest.Update([updatedNode]), null, null);

        // Assert - Content stream should receive the update
        var updatedContent = await todoStream
            .SelectMany(items => items ?? [])
            .Where(item => item.Title == "MeshNode Updated")
            .Timeout(3.Seconds())
            .FirstAsync();

        updatedContent.Should().NotBeNull();
        updatedContent.Title.Should().Be("MeshNode Updated");
        updatedContent.Description.Should().Be("From MeshNode");
    }

    [HubFact]
    public async Task ContentUpdate_PreservesExistingMeshNodeValues_WhenContentHasNoMapping()
    {
        // Arrange - Content type with no mappable properties
        var hubPath = GetHubPath("minimal");
        var initialContent = new MinimalItem { Id = "1", Data = "some data" };

        var node = MeshNode.FromPath(hubPath) with
        {
            Name = "Manually Set Name",
            Description = "Manually Set Description",
            NodeType = "minimal",
            Content = initialContent
        };
        await _persistence.SaveNodeAsync(node, JsonOptions);

        var host = Mesh.GetHostedHub(new Address(HostType, "minimal"), c => c
            .AddMeshDataSource(ds => ds.WithContentType<MinimalItem>()));

        var workspace = host.GetWorkspace();

        // Wait for initial data load
        var contentStream = workspace.GetStream<MinimalItem>();
        await contentStream!
            .Where(items => items?.Any() == true)
            .Timeout(5.Seconds())
            .FirstAsync();

        // Act - Update the content
        var updatedContent = initialContent with { Data = "updated data" };
        workspace.RequestChange(DataChangeRequest.Update([updatedContent]), null, null);

        // Wait for sync
        await Task.Delay(500);

        // Assert - MeshNode should preserve manually set Name/Description
        var meshNode = await _persistence.GetNodeAsync(hubPath, JsonOptions);
        meshNode.Should().NotBeNull();
        meshNode!.Name.Should().Be("Manually Set Name", "MeshNode.Name should be preserved when content has no mapping");
        meshNode.Description.Should().Be("Manually Set Description", "MeshNode.Description should be preserved when content has no mapping");
    }

    [HubFact]
    public async Task AttributeMapping_TakesPriorityOverConvention()
    {
        // Arrange - Content type with both attribute and conventional properties
        var hubPath = GetHubPath("mixed");
        var initialContent = new MixedMappingItem
        {
            Id = "1",
            Title = "Convention Title",  // Would map by convention
            CustomName = "Attribute Name" // Maps via attribute
        };
        await SetupInitialNode(hubPath, initialContent, "mixed");

        var host = Mesh.GetHostedHub(new Address(HostType, "mixed"), c => c
            .AddMeshDataSource(ds => ds.WithContentType<MixedMappingItem>()));

        var workspace = host.GetWorkspace();

        // Wait for initial data load
        var contentStream = workspace.GetStream<MixedMappingItem>();
        await contentStream!
            .Where(items => items?.Any() == true)
            .Timeout(5.Seconds())
            .FirstAsync();

        // Act - Update the content
        var updatedContent = initialContent with
        {
            Title = "Updated Convention",
            CustomName = "Updated Attribute"
        };
        workspace.RequestChange(DataChangeRequest.Update([updatedContent]), null, null);

        // Wait for sync
        await Task.Delay(500);

        // Assert - Attribute should take priority over convention
        var meshNode = await _persistence.GetNodeAsync(hubPath, JsonOptions);
        meshNode.Should().NotBeNull();
        meshNode!.Name.Should().Be("Updated Attribute", "Attribute mapping should take priority over convention");
    }

    [HubFact]
    public async Task ContentUpdate_SyncsAllFourProperties_ViaAttribute()
    {
        // Arrange - Content type with all four MeshNode property mappings
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
        var contentStream = workspace.GetStream<FullMappingItem>();
        await contentStream!
            .Where(items => items?.Any() == true)
            .Timeout(5.Seconds())
            .FirstAsync();

        // Act - Update all properties
        var updatedContent = initialContent with
        {
            DisplayName = "Updated Name",
            Summary = "Updated Description",
            IconName = "Star",
            Group = "Premium"
        };
        workspace.RequestChange(DataChangeRequest.Update([updatedContent]), null, null);

        // Wait for sync
        await Task.Delay(500);

        // Assert - All four MeshNode properties should be synced
        var meshNode = await _persistence.GetNodeAsync(hubPath, JsonOptions);
        meshNode.Should().NotBeNull();
        meshNode!.Name.Should().Be("Updated Name", "MeshNode.Name should sync from [MeshNodeProperty(\"Name\")]");
        meshNode.Description.Should().Be("Updated Description", "MeshNode.Description should sync from [MeshNodeProperty(\"Description\")]");
        meshNode.Icon.Should().Be("Star", "MeshNode.Icon should sync from [MeshNodeProperty(\"Icon\")]");
        meshNode.Category.Should().Be("Premium", "MeshNode.Category should sync from [MeshNodeProperty(\"Category\")]");
    }

    [HubFact]
    public async Task ContentUpdate_SyncsAllFourProperties_ViaConvention()
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
        var contentStream = workspace.GetStream<ConventionalFullItem>();
        await contentStream!
            .Where(items => items?.Any() == true)
            .Timeout(5.Seconds())
            .FirstAsync();

        // Act - Update all properties
        var updatedContent = initialContent with
        {
            Name = "Convention Name",
            Description = "Convention Description",
            Icon = "Folder",
            Category = "Archive"
        };
        workspace.RequestChange(DataChangeRequest.Update([updatedContent]), null, null);

        // Wait for sync
        await Task.Delay(500);

        // Assert - All four MeshNode properties should be synced via convention
        var meshNode = await _persistence.GetNodeAsync(hubPath, JsonOptions);
        meshNode.Should().NotBeNull();
        meshNode!.Name.Should().Be("Convention Name", "MeshNode.Name should sync from Name property by convention");
        meshNode.Description.Should().Be("Convention Description", "MeshNode.Description should sync from Description property by convention");
        meshNode.Icon.Should().Be("Folder", "MeshNode.Icon should sync from Icon property by convention");
        meshNode.Category.Should().Be("Archive", "MeshNode.Category should sync from Category property by convention");
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

    [MeshNodeProperty("Description")]
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

    [MeshNodeProperty("Description")]
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
