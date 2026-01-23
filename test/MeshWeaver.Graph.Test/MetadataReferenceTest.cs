using FluentAssertions;
using MeshWeaver.Data;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Tests for MetadataReference which returns MeshNode with Content stripped.
/// Used by the metadata: UCR prefix (e.g., @@path/metadata:).
/// </summary>
public class MetadataReferenceTest
{
    [Fact]
    public void MetadataReference_CanBeCreated()
    {
        // Act
        var reference = new MetadataReference();

        // Assert
        reference.Should().NotBeNull();
        reference.ToString().Should().Be("metadata");
    }

    [Fact]
    public void MetadataReference_IsWorkspaceReferenceOfObject()
    {
        // Arrange
        var reference = new MetadataReference();

        // Assert - verify inheritance
        reference.Should().BeAssignableTo<WorkspaceReference<object>>();
    }

    [Fact]
    public void MeshNode_WithContent_CanCreateMetadataView()
    {
        // Arrange - create a MeshNode with Content
        var originalNode = MeshNode.FromPath("test/node") with
        {
            Name = "Test Node",
            NodeType = "generic",
            Description = "A test node with content",
            Content = new { Id = "test1", Title = "Test Content" }
        };

        // Act - create metadata view by stripping Content
        var metadataNode = originalNode with { Content = null };

        // Assert - all properties except Content should be preserved
        metadataNode.Path.Should().Be(originalNode.Path);
        metadataNode.Name.Should().Be(originalNode.Name);
        metadataNode.NodeType.Should().Be(originalNode.NodeType);
        metadataNode.Description.Should().Be(originalNode.Description);
        metadataNode.Content.Should().BeNull("Content should be stripped for metadata");
    }

    [Fact]
    public void UcrPrefixResolver_MapsMetadataToMetadataArea()
    {
        // Act
        var resolved = UcrPrefixResolver.TryResolve("metadata:", out var area, out var remainingPath);

        // Assert
        resolved.Should().BeTrue("metadata: should be a valid UCR prefix");
        area.Should().Be("$Metadata");
        remainingPath.Should().BeNull("self-reference should have no remaining path");
    }

    [Fact]
    public void UcrPrefixResolver_ReturnsLayoutAreaReferenceForMetadata()
    {
        // Act
        var layoutRef = UcrPrefixResolver.ResolveToLayoutAreaReference("metadata:");

        // Assert
        layoutRef.Should().NotBeNull();
        layoutRef!.Area.Should().Be("$Metadata");
        layoutRef.Id.Should().BeNull();
    }

    [Fact]
    public void LayoutAreaMarkdownParser_MetadataAreaNameConstant_IsCorrect()
    {
        // Assert - verify the constant matches the expected value
        LayoutAreaMarkdownParser.MetadataAreaName.Should().Be("$Metadata");
    }
}
