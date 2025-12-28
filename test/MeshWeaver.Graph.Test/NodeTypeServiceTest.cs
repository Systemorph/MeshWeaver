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

    #region GetNodeTypeNodeAsync Tests

    [Fact]
    public async Task GetNodeTypeNodeAsync_ReturnsGlobalType_WhenNoLocalOverride()
    {
        // Arrange
        var storyNode = MeshNode.FromPath("type/story") with
        {
            Name = "Story",
            NodeType = "NodeType",
            Content = new NodeTypeDefinition { Id = "story", Namespace = "Type", DisplayName = "Story" }
        };
        await _persistence.SaveNodeAsync(storyNode, TestContext.Current.CancellationToken);

        // Act
        var result = await _service.GetNodeTypeNodeAsync("story", "graph/org1/proj1", TestContext.Current.CancellationToken);

        // Assert
        result.Should().NotBeNull();
        result!.Path.Should().Be("type/story");
    }

    [Fact]
    public async Task GetNodeTypeNodeAsync_ReturnsLocalType_WhenOverrideExists()
    {
        // Arrange - global and local
        var globalStory = MeshNode.FromPath("type/story") with
        {
            Name = "Story",
            NodeType = "NodeType",
            Content = new NodeTypeDefinition { Id = "story", Namespace = "Type", DisplayName = "Story" }
        };
        await _persistence.SaveNodeAsync(globalStory, TestContext.Current.CancellationToken);

        var localStory = MeshNode.FromPath("graph/org1/story") with
        {
            Name = "Org Story",
            NodeType = "NodeType",
            Content = new NodeTypeDefinition { Id = "story", Namespace = "Type", DisplayName = "Org Story" }
        };
        await _persistence.SaveNodeAsync(localStory, TestContext.Current.CancellationToken);

        // Act
        var result = await _service.GetNodeTypeNodeAsync("story", "graph/org1/proj1", TestContext.Current.CancellationToken);

        // Assert - should get the local override at org level
        result.Should().NotBeNull();
        result!.Path.Should().Be("graph/org1/story");
    }

    [Fact]
    public async Task GetNodeTypeNodeAsync_ReturnsNull_WhenTypeNotFound()
    {
        // Act
        var result = await _service.GetNodeTypeNodeAsync("nonexistent", "graph/org1", TestContext.Current.CancellationToken);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region GetCodeFileAsync Tests

    [Fact]
    public async Task GetCodeFileAsync_ReturnsCodeFile_FromPartition()
    {
        // Arrange
        var storyNode = MeshNode.FromPath("type/story") with
        {
            Name = "Story",
            NodeType = "NodeType",
            Content = new NodeTypeDefinition { Id = "story", Namespace = "Type", DisplayName = "Story" }
        };
        await _persistence.SaveNodeAsync(storyNode, TestContext.Current.CancellationToken);

        var codeConfig = new CodeFile
        {
            Code = "public record Story { [Key] public string Id { get; init; } }"
        };
        await _persistence.SavePartitionObjectsAsync("type/story", null, [codeConfig], TestContext.Current.CancellationToken);

        // Act
        var result = await _service.GetCodeFileAsync("story", "graph/org1", TestContext.Current.CancellationToken);

        // Assert
        result.Should().NotBeNull();
        result!.Code.Should().Contain("record Story");
    }

    [Fact]
    public async Task GetCodeFileAsync_ReturnsNull_WhenNoCodeFile()
    {
        // Arrange - NodeType without CodeFile
        var storyNode = MeshNode.FromPath("type/story") with
        {
            Name = "Story",
            NodeType = "NodeType",
            Content = new NodeTypeDefinition { Id = "story", Namespace = "Type", DisplayName = "Story" }
        };
        await _persistence.SaveNodeAsync(storyNode, TestContext.Current.CancellationToken);

        // Act
        var result = await _service.GetCodeFileAsync("story", "graph/org1", TestContext.Current.CancellationToken);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region GetDependencyCodeAsync Tests

    [Fact]
    public async Task GetDependencyCodeAsync_ReturnsCombinedCode_FromDependencies()
    {
        // Arrange
        var personNode = MeshNode.FromPath("type/person") with
        {
            Name = "Person",
            NodeType = "NodeType",
            Content = new NodeTypeDefinition { Id = "person", Namespace = "Type", DisplayName = "Person" }
        };
        await _persistence.SaveNodeAsync(personNode, TestContext.Current.CancellationToken);

        var personCode = new CodeFile { Code = "public record Person { public string Name { get; init; } }" };
        await _persistence.SavePartitionObjectsAsync("type/person", null, [personCode], TestContext.Current.CancellationToken);

        var orgNode = MeshNode.FromPath("type/organization") with
        {
            Name = "Organization",
            NodeType = "NodeType",
            Content = new NodeTypeDefinition { Id = "organization", Namespace = "Type", DisplayName = "Organization" }
        };
        await _persistence.SaveNodeAsync(orgNode, TestContext.Current.CancellationToken);

        var orgCode = new CodeFile { Code = "public record Organization { public string Title { get; init; } }" };
        await _persistence.SavePartitionObjectsAsync("type/organization", null, [orgCode], TestContext.Current.CancellationToken);

        // Act
        var result = await _service.GetDependencyCodeAsync(["type/person", "type/organization"], TestContext.Current.CancellationToken);

        // Assert
        result.Should().Contain("Person");
        result.Should().Contain("Organization");
    }

    [Fact]
    public async Task GetDependencyCodeAsync_ReturnsEmptyString_WhenNoDependencies()
    {
        // Act
        var result = await _service.GetDependencyCodeAsync([], TestContext.Current.CancellationToken);

        // Assert
        result.Should().Contain("// Dependency types");
    }

    #endregion
}
