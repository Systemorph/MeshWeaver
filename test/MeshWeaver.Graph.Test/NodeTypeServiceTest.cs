using System;
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
    public async Task GetNodeTypeNodeAsync_ReturnsNodeByPath()
    {
        // Arrange
        var storyNode = MeshNode.FromPath("Type/story") with
        {
            Name = "Story",
            NodeType = "NodeType",
            Content = new NodeTypeDefinition { Id = "story", Namespace = "Type", DisplayName = "Story" }
        };
        await _persistence.SaveNodeAsync(storyNode, TestContext.Current.CancellationToken);

        // Act - pass the full path
        var result = await _service.GetNodeTypeNodeAsync("Type/story", "", TestContext.Current.CancellationToken);

        // Assert
        result.Should().NotBeNull();
        result!.Path.Should().Be("Type/story");
    }

    [Fact]
    public async Task GetNodeTypeNodeAsync_ReturnsNull_WhenNotFound()
    {
        // Act
        var result = await _service.GetNodeTypeNodeAsync("Type/nonexistent", "", TestContext.Current.CancellationToken);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetNodeTypeNodeAsync_ThrowsWhenNotNodeType()
    {
        // Arrange - a node that is not a NodeType
        var regularNode = MeshNode.FromPath("graph/org1") with
        {
            Name = "Org1",
            NodeType = "Organization"
        };
        await _persistence.SaveNodeAsync(regularNode, TestContext.Current.CancellationToken);

        // Act & Assert
        var act = () => _service.GetNodeTypeNodeAsync("graph/org1", "", TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not a NodeType*");
    }

    #endregion

    #region GetCodeFileAsync Tests

    [Fact]
    public async Task GetCodeFileAsync_ReturnsCodeFile_FromPartition()
    {
        // Arrange
        var storyNode = MeshNode.FromPath("Type/story") with
        {
            Name = "Story",
            NodeType = "NodeType",
            Content = new NodeTypeDefinition { Id = "story", Namespace = "Type", DisplayName = "Story" }
        };
        await _persistence.SaveNodeAsync(storyNode, TestContext.Current.CancellationToken);

        var codeFile = new CodeFile
        {
            Code = "public record Story { [Key] public string Id { get; init; } }"
        };
        await _persistence.SavePartitionObjectsAsync("Type/story", null, [codeFile], TestContext.Current.CancellationToken);

        // Act - pass full path
        var result = await _service.GetCodeFileAsync("Type/story", "", TestContext.Current.CancellationToken);

        // Assert
        result.Should().NotBeNull();
        result!.Code.Should().Contain("record Story");
    }

    [Fact]
    public async Task GetCodeFileAsync_ReturnsNull_WhenNoCodeFile()
    {
        // Arrange - NodeType without CodeFile
        var storyNode = MeshNode.FromPath("Type/story") with
        {
            Name = "Story",
            NodeType = "NodeType",
            Content = new NodeTypeDefinition { Id = "story", Namespace = "Type", DisplayName = "Story" }
        };
        await _persistence.SaveNodeAsync(storyNode, TestContext.Current.CancellationToken);

        // Act - pass full path
        var result = await _service.GetCodeFileAsync("Type/story", "", TestContext.Current.CancellationToken);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region GetDependencyCodeAsync Tests

    [Fact]
    public async Task GetDependencyCodeAsync_ReturnsCombinedCode_FromDependencies()
    {
        // Arrange
        var personNode = MeshNode.FromPath("Type/person") with
        {
            Name = "Person",
            NodeType = "NodeType",
            Content = new NodeTypeDefinition { Id = "person", Namespace = "Type", DisplayName = "Person" }
        };
        await _persistence.SaveNodeAsync(personNode, TestContext.Current.CancellationToken);

        var personCode = new CodeFile { Code = "public record Person { public string Name { get; init; } }" };
        await _persistence.SavePartitionObjectsAsync("Type/person", null, [personCode], TestContext.Current.CancellationToken);

        var orgNode = MeshNode.FromPath("Type/organization") with
        {
            Name = "Organization",
            NodeType = "NodeType",
            Content = new NodeTypeDefinition { Id = "organization", Namespace = "Type", DisplayName = "Organization" }
        };
        await _persistence.SaveNodeAsync(orgNode, TestContext.Current.CancellationToken);

        var orgCode = new CodeFile { Code = "public record Organization { public string Title { get; init; } }" };
        await _persistence.SavePartitionObjectsAsync("Type/organization", null, [orgCode], TestContext.Current.CancellationToken);

        // Act
        var result = await _service.GetDependencyCodeAsync(["Type/person", "Type/organization"], TestContext.Current.CancellationToken);

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
