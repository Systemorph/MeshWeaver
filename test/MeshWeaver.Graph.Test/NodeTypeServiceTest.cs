using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Tests for INodeTypeService which manages NodeType nodes and their partition data.
/// </summary>
public class NodeTypeServiceTest
{
    private readonly InMemoryPersistenceService _persistence;
    private readonly NodeTypeService _service;

    public NodeTypeServiceTest()
    {
        _persistence = new InMemoryPersistenceService();
        _service = new NodeTypeService(_persistence);
    }

    #region GetNodeTypeNodesAsync Tests

    [Fact]
    public async Task GetNodeTypeNodesAsync_ReturnsGlobalTypes_WhenContextPathIsEmpty()
    {
        // Arrange - create global NodeType
        var storyNode = new MeshNode("type/story")
        {
            Name = "Story",
            NodeType = "NodeType",
            Content = new NodeTypeDefinition { Id = "story", DisplayName = "Story" }
        };
        await _persistence.SaveNodeAsync(storyNode);

        // Act
        var nodes = await _service.GetNodeTypeNodesAsync("").ToListAsync();

        // Assert
        nodes.Should().Contain(n => n.Prefix == "type/story");
    }

    [Fact]
    public async Task GetNodeTypeNodesAsync_WalksUpHierarchy_ReturnsLocalOverrideOnly()
    {
        // Arrange - create global type and local type with same Id
        var globalStory = new MeshNode("type/story")
        {
            Name = "Story",
            NodeType = "NodeType",
            Content = new NodeTypeDefinition { Id = "story", DisplayName = "Story" }
        };
        await _persistence.SaveNodeAsync(globalStory);

        // Local type override at project level (same Id shadows global)
        var localStory = new MeshNode("graph/org1/proj1/story")
        {
            Name = "Project Story",
            NodeType = "NodeType",
            Content = new NodeTypeDefinition { Id = "story", DisplayName = "Project Story" }
        };
        await _persistence.SaveNodeAsync(localStory);

        // A different type at global level
        var globalOrg = new MeshNode("type/org")
        {
            Name = "Organization",
            NodeType = "NodeType",
            Content = new NodeTypeDefinition { Id = "org", DisplayName = "Organization" }
        };
        await _persistence.SaveNodeAsync(globalOrg);

        // Act - resolve from deep path
        var nodes = await _service.GetNodeTypeNodesAsync("graph/org1/proj1/task1").ToListAsync();

        // Assert - should find local story (shadows global) + global org
        nodes.Should().HaveCount(2);

        // Local story should be returned, not global
        var storyNode = nodes.FirstOrDefault(n => ((NodeTypeDefinition)n.Content!).Id == "story");
        storyNode.Should().NotBeNull();
        storyNode!.Prefix.Should().Be("graph/org1/proj1/story");
        storyNode.Name.Should().Be("Project Story"); // Local version

        // Global org should be returned
        nodes.Should().Contain(n => ((NodeTypeDefinition)n.Content!).Id == "org");
    }

    #endregion

    #region GetNodeTypeNodeAsync Tests

    [Fact]
    public async Task GetNodeTypeNodeAsync_ReturnsGlobalType_WhenNoLocalOverride()
    {
        // Arrange
        var storyNode = new MeshNode("type/story")
        {
            Name = "Story",
            NodeType = "NodeType",
            Content = new NodeTypeDefinition { Id = "story", DisplayName = "Story" }
        };
        await _persistence.SaveNodeAsync(storyNode);

        // Act
        var result = await _service.GetNodeTypeNodeAsync("story", "graph/org1/proj1");

        // Assert
        result.Should().NotBeNull();
        result!.Prefix.Should().Be("type/story");
    }

    [Fact]
    public async Task GetNodeTypeNodeAsync_ReturnsLocalType_WhenOverrideExists()
    {
        // Arrange - global and local
        var globalStory = new MeshNode("type/story")
        {
            Name = "Story",
            NodeType = "NodeType",
            Content = new NodeTypeDefinition { Id = "story", DisplayName = "Story" }
        };
        await _persistence.SaveNodeAsync(globalStory);

        var localStory = new MeshNode("graph/org1/story")
        {
            Name = "Org Story",
            NodeType = "NodeType",
            Content = new NodeTypeDefinition { Id = "story", DisplayName = "Org Story" }
        };
        await _persistence.SaveNodeAsync(localStory);

        // Act
        var result = await _service.GetNodeTypeNodeAsync("story", "graph/org1/proj1");

        // Assert - should get the local override at org level
        result.Should().NotBeNull();
        result!.Prefix.Should().Be("graph/org1/story");
    }

    [Fact]
    public async Task GetNodeTypeNodeAsync_ReturnsNull_WhenTypeNotFound()
    {
        // Act
        var result = await _service.GetNodeTypeNodeAsync("nonexistent", "graph/org1");

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region GetDataModelAsync Tests

    [Fact]
    public async Task GetDataModelAsync_ReturnsDataModel_FromPartition()
    {
        // Arrange
        var storyNode = new MeshNode("type/story")
        {
            Name = "Story",
            NodeType = "NodeType",
            Content = new NodeTypeDefinition { Id = "story", DisplayName = "Story" }
        };
        await _persistence.SaveNodeAsync(storyNode);

        var dataModel = new DataModel
        {
            Id = "story",
            DisplayName = "Story",
            TypeSource = "public record Story { [Key] public string Id { get; init; } }"
        };
        await _persistence.SavePartitionObjectsAsync("type/story", null, [dataModel]);

        // Act
        var result = await _service.GetDataModelAsync("story", "graph/org1");

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be("story");
        result.TypeSource.Should().Contain("record Story");
    }

    [Fact]
    public async Task GetDataModelAsync_ReturnsNull_WhenNoDataModel()
    {
        // Arrange - NodeType without DataModel
        var storyNode = new MeshNode("type/story")
        {
            Name = "Story",
            NodeType = "NodeType",
            Content = new NodeTypeDefinition { Id = "story", DisplayName = "Story" }
        };
        await _persistence.SaveNodeAsync(storyNode);

        // Act
        var result = await _service.GetDataModelAsync("story", "graph/org1");

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region GetLayoutAreasAsync Tests

    [Fact]
    public async Task GetLayoutAreasAsync_ReturnsLayoutAreas_FromPartition()
    {
        // Arrange
        var storyNode = new MeshNode("type/story")
        {
            Name = "Story",
            NodeType = "NodeType",
            Content = new NodeTypeDefinition { Id = "story", DisplayName = "Story" }
        };
        await _persistence.SaveNodeAsync(storyNode);

        var layout1 = new LayoutAreaConfig { Id = "story-details", Area = "Details", Title = "Details" };
        var layout2 = new LayoutAreaConfig { Id = "story-thumbnail", Area = "Thumbnail", Title = "Thumbnail" };
        await _persistence.SavePartitionObjectsAsync("type/story", "layoutAreas", [layout1, layout2]);

        // Act
        var result = await _service.GetLayoutAreasAsync("story", "graph/org1");

        // Assert
        result.Should().HaveCount(2);
        result.Should().Contain(la => la.Area == "Details");
        result.Should().Contain(la => la.Area == "Thumbnail");
    }

    [Fact]
    public async Task GetLayoutAreasAsync_ReturnsEmpty_WhenNoLayoutAreas()
    {
        // Arrange
        var storyNode = new MeshNode("type/story")
        {
            Name = "Story",
            NodeType = "NodeType",
            Content = new NodeTypeDefinition { Id = "story", DisplayName = "Story" }
        };
        await _persistence.SaveNodeAsync(storyNode);

        // Act
        var result = await _service.GetLayoutAreasAsync("story", "graph/org1");

        // Assert
        result.Should().BeEmpty();
    }

    #endregion

    #region SaveDataModelAsync Tests

    [Fact]
    public async Task SaveDataModelAsync_PersistsDataModel_ToPartition()
    {
        // Arrange
        var storyNode = new MeshNode("type/story")
        {
            Name = "Story",
            NodeType = "NodeType",
            Content = new NodeTypeDefinition { Id = "story", DisplayName = "Story" }
        };
        await _persistence.SaveNodeAsync(storyNode);

        var dataModel = new DataModel
        {
            Id = "story",
            DisplayName = "Story",
            TypeSource = "public record Story { [Key] public string Id { get; init; } }"
        };

        // Act
        await _service.SaveDataModelAsync("type/story", dataModel);

        // Assert
        var loaded = await _service.GetDataModelAsync("story", "");
        loaded.Should().NotBeNull();
        loaded!.TypeSource.Should().Contain("record Story");
    }

    #endregion

    #region SaveLayoutAreaAsync Tests

    [Fact]
    public async Task SaveLayoutAreaAsync_PersistsLayoutArea_ToPartition()
    {
        // Arrange
        var storyNode = new MeshNode("type/story")
        {
            Name = "Story",
            NodeType = "NodeType",
            Content = new NodeTypeDefinition { Id = "story", DisplayName = "Story" }
        };
        await _persistence.SaveNodeAsync(storyNode);

        var layout = new LayoutAreaConfig { Id = "story-details", Area = "Details", Title = "Details View" };

        // Act
        await _service.SaveLayoutAreaAsync("type/story", layout);

        // Assert
        var loaded = await _service.GetLayoutAreasAsync("story", "");
        loaded.Should().Contain(la => la.Area == "Details");
    }

    #endregion

    #region GetAllNodeTypeNodesAsync Tests

    [Fact]
    public async Task GetAllNodeTypeNodesAsync_ReturnsAllNodeTypes()
    {
        // Arrange
        await _persistence.SaveNodeAsync(new MeshNode("type/story") { Name = "Story", NodeType = "NodeType" });
        await _persistence.SaveNodeAsync(new MeshNode("type/org") { Name = "Org", NodeType = "NodeType" });
        await _persistence.SaveNodeAsync(new MeshNode("graph/proj1/task") { Name = "Task", NodeType = "NodeType" });
        await _persistence.SaveNodeAsync(new MeshNode("graph/org1") { Name = "Org 1", NodeType = "org" }); // Not a NodeType

        // Act
        var nodes = await _service.GetAllNodeTypeNodesAsync().ToListAsync();

        // Assert
        nodes.Should().HaveCount(3);
        nodes.Should().OnlyContain(n => n.NodeType == "NodeType");
    }

    #endregion

    #region GetAllDataModelsAsync Tests

    [Fact]
    public async Task GetAllDataModelsAsync_ReturnsAllDataModels()
    {
        // Arrange
        await _persistence.SaveNodeAsync(new MeshNode("type/story") { Name = "Story", NodeType = "NodeType" });
        await _persistence.SaveNodeAsync(new MeshNode("type/org") { Name = "Org", NodeType = "NodeType" });

        var storyModel = new DataModel { Id = "story", DisplayName = "Story", TypeSource = "public record Story { }" };
        var orgModel = new DataModel { Id = "org", DisplayName = "Organization", TypeSource = "public record Organization { }" };

        await _persistence.SavePartitionObjectsAsync("type/story", null, [storyModel]);
        await _persistence.SavePartitionObjectsAsync("type/org", null, [orgModel]);

        // Act
        var models = await _service.GetAllDataModelsAsync();

        // Assert
        models.Should().HaveCount(2);
        models.Should().Contain(m => m.Id == "story");
        models.Should().Contain(m => m.Id == "org");
    }

    #endregion

    #region GetAllLayoutAreasAsync Tests

    [Fact]
    public async Task GetAllLayoutAreasAsync_ReturnsAllLayoutAreas()
    {
        // Arrange
        await _persistence.SaveNodeAsync(new MeshNode("type/story") { Name = "Story", NodeType = "NodeType" });
        await _persistence.SaveNodeAsync(new MeshNode("type/org") { Name = "Org", NodeType = "NodeType" });

        var storyDetails = new LayoutAreaConfig { Id = "story-details", Area = "Details" };
        var orgDetails = new LayoutAreaConfig { Id = "org-details", Area = "Details" };
        var orgList = new LayoutAreaConfig { Id = "org-list", Area = "List" };

        await _persistence.SavePartitionObjectsAsync("type/story", "layoutAreas", [storyDetails]);
        await _persistence.SavePartitionObjectsAsync("type/org", "layoutAreas", [orgDetails, orgList]);

        // Act
        var layouts = await _service.GetAllLayoutAreasAsync();

        // Assert
        layouts.Should().HaveCount(3);
    }

    #endregion
}
