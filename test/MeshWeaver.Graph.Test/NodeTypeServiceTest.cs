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

    #region GetCodeConfigurationAsync Tests

    [Fact]
    public async Task GetCodeConfigurationAsync_ReturnsCodeConfiguration_FromPartition()
    {
        // Arrange
        var storyNode = new MeshNode("type/story")
        {
            Name = "Story",
            NodeType = "NodeType",
            Content = new NodeTypeDefinition { Id = "story", DisplayName = "Story" }
        };
        await _persistence.SaveNodeAsync(storyNode);

        var codeConfig = new CodeConfiguration
        {
            Code = "public record Story { [Key] public string Id { get; init; } }"
        };
        await _persistence.SavePartitionObjectsAsync("type/story", "_config", [codeConfig]);

        // Act
        var result = await _service.GetCodeConfigurationAsync("story", "graph/org1");

        // Assert
        result.Should().NotBeNull();
        result!.Code.Should().Contain("record Story");
    }

    [Fact]
    public async Task GetCodeConfigurationAsync_ReturnsNull_WhenNoCodeConfiguration()
    {
        // Arrange - NodeType without CodeConfiguration
        var storyNode = new MeshNode("type/story")
        {
            Name = "Story",
            NodeType = "NodeType",
            Content = new NodeTypeDefinition { Id = "story", DisplayName = "Story" }
        };
        await _persistence.SaveNodeAsync(storyNode);

        // Act
        var result = await _service.GetCodeConfigurationAsync("story", "graph/org1");

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region SaveCodeConfigurationAsync Tests

    [Fact]
    public async Task SaveCodeConfigurationAsync_PersistsCodeConfiguration_ToPartition()
    {
        // Arrange
        var storyNode = new MeshNode("type/story")
        {
            Name = "Story",
            NodeType = "NodeType",
            Content = new NodeTypeDefinition { Id = "story", DisplayName = "Story" }
        };
        await _persistence.SaveNodeAsync(storyNode);

        var codeConfig = new CodeConfiguration
        {
            Code = "public record Story { [Key] public string Id { get; init; } }"
        };

        // Act
        await _service.SaveCodeConfigurationAsync("type/story", codeConfig);

        // Assert
        var loaded = await _service.GetCodeConfigurationAsync("story", "");
        loaded.Should().NotBeNull();
        loaded!.Code.Should().Contain("record Story");
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

    #region GetAllCodeConfigurationsAsync Tests

    [Fact]
    public async Task GetAllCodeConfigurationsAsync_ReturnsAllCodeConfigurations()
    {
        // Arrange
        await _persistence.SaveNodeAsync(new MeshNode("type/story") { Name = "Story", NodeType = "NodeType" });
        await _persistence.SaveNodeAsync(new MeshNode("type/org") { Name = "Org", NodeType = "NodeType" });

        var storyConfig = new CodeConfiguration { Code = "public record Story { }" };
        var orgConfig = new CodeConfiguration { Code = "public record Organization { }" };

        await _persistence.SavePartitionObjectsAsync("type/story", "_config", [storyConfig]);
        await _persistence.SavePartitionObjectsAsync("type/org", "_config", [orgConfig]);

        // Act
        var configs = await _service.GetAllCodeConfigurationsAsync();

        // Assert
        configs.Should().HaveCount(2);
        configs.Should().Contain(c => c.Code!.Contains("Story"));
        configs.Should().Contain(c => c.Code!.Contains("Organization"));
    }

    #endregion
}
