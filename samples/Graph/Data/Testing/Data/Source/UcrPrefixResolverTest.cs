// <meshweaver>
// Id: Testing/Data/UcrPrefixResolverTest
// DisplayName: Testing/Data/UcrPrefixResolverTest — migrated from xunit (convert-xunit-to-inmesh.py)
// </meshweaver>
#nullable enable
using MeshWeaver.Reactive.Assertions;
using MeshWeaver.Testing.InMesh;
using System;

/// <summary>
/// Tests for UcrPrefixResolver which handles Unified Content Reference (UCR) prefix parsing.
/// UCR prefixes (content:, data:, schema:, model:, menu:) map to special areas ($Content, $Data, $Schema, $Model, $Menu).
/// </summary>
public class UcrPrefixResolverTest
{
    [MeshTheory]
    [MeshInlineData("content:logo.svg", "$Content", "logo.svg")]
    [MeshInlineData("content:path/to/file.png", "$Content", "path/to/file.png")]
    [MeshInlineData("content:", "$Content", null)]
    [MeshInlineData("data:entityId", "$Data", "entityId")]
    [MeshInlineData("data:collection/entityId", "$Data", "collection/entityId")]
    [MeshInlineData("data:", "$Data", null)]
    [MeshInlineData("schema:Person", "$Schema", "Person")]
    [MeshInlineData("schema:", "$Schema", null)]
    [MeshInlineData("model:MyModel", "$Model", "MyModel")]
    [MeshInlineData("model:", "$Model", null)]
    public void TryResolve_WithValidPrefix_ReturnsExpectedAreaAndPath(
        string input, string expectedArea, string? expectedPath)
    {
        // Act
        var result = UcrPrefixResolver.TryResolve(input, out var area, out var remainingPath);

        // Assert
        result.Should().BeTrue();
        area.Should().Be(expectedArea);
        remainingPath.Should().Be(expectedPath);
    }

    [MeshTheory]
    [MeshInlineData("Content:logo.svg", "$Content", "logo.svg")]
    [MeshInlineData("CONTENT:logo.svg", "$Content", "logo.svg")]
    [MeshInlineData("Data:test", "$Data", "test")]
    [MeshInlineData("DATA:", "$Data", null)]
    [MeshInlineData("Schema:Type", "$Schema", "Type")]
    [MeshInlineData("SCHEMA:", "$Schema", null)]
    [MeshInlineData("Model:Test", "$Model", "Test")]
    [MeshInlineData("MODEL:", "$Model", null)]
    public void TryResolve_IsCaseInsensitive(
        string input, string expectedArea, string? expectedPath)
    {
        // Act
        var result = UcrPrefixResolver.TryResolve(input, out var area, out var remainingPath);

        // Assert
        result.Should().BeTrue();
        area.Should().Be(expectedArea);
        remainingPath.Should().Be(expectedPath);
    }

    [MeshTheory]
    [MeshInlineData(null)]
    [MeshInlineData("")]
    [MeshInlineData("Details")]
    [MeshInlineData("Catalog")]
    [MeshInlineData("Settings")]
    [MeshInlineData("unknown:path")]
    [MeshInlineData("file:path")]
    [MeshInlineData("http://example.com")]
    [MeshInlineData("area/subarea")]
    public void TryResolve_WithInvalidPrefix_ReturnsFalse(string? input)
    {
        // Act
        var result = UcrPrefixResolver.TryResolve(input, out var area, out var remainingPath);

        // Assert
        result.Should().BeFalse();
        area.Should().BeNull();
        remainingPath.Should().BeNull();
    }

    [MeshTheory]
    [MeshInlineData("content:logo.svg", "$Content", "logo.svg")]
    [MeshInlineData("data:", "$Data", null)]
    [MeshInlineData("schema:Person", "$Schema", "Person")]
    [MeshInlineData("model:MyModel", "$Model", "MyModel")]
    public void ResolveToLayoutAreaReference_WithValidPrefix_ReturnsLayoutAreaReference(
        string input, string expectedArea, string? expectedId)
    {
        // Act
        var result = UcrPrefixResolver.ResolveToLayoutAreaReference(input);

        // Assert
        result.Should().NotBeNull();
        result!.Area.Should().Be(expectedArea);
        result.Id?.ToString().Should().Be(expectedId);
    }

    [MeshTheory]
    [MeshInlineData(null)]
    [MeshInlineData("")]
    [MeshInlineData("Details")]
    [MeshInlineData("unknown:path")]
    public void ResolveToLayoutAreaReference_WithInvalidPrefix_ReturnsNull(string? input)
    {
        // Act
        var result = UcrPrefixResolver.ResolveToLayoutAreaReference(input);

        // Assert
        result.Should().BeNull();
    }

    [MeshFact]
    public void PrefixToAreaMap_ContainsExpectedMappings()
    {
        // Assert
        UcrPrefixResolver.PrefixToAreaMap.Should().ContainKey("content");
        UcrPrefixResolver.PrefixToAreaMap.Should().ContainKey("data");
        UcrPrefixResolver.PrefixToAreaMap.Should().ContainKey("schema");
        UcrPrefixResolver.PrefixToAreaMap.Should().ContainKey("model");

        UcrPrefixResolver.PrefixToAreaMap["content"].Should().Be("$Content");
        UcrPrefixResolver.PrefixToAreaMap["data"].Should().Be("$Data");
        UcrPrefixResolver.PrefixToAreaMap["schema"].Should().Be("$Schema");
        UcrPrefixResolver.PrefixToAreaMap["model"].Should().Be("$Model");
    }

    [MeshFact]
    public void PrefixToAreaMap_IsCaseInsensitive()
    {
        // Assert - verify case-insensitive lookup works
        UcrPrefixResolver.PrefixToAreaMap.Should().ContainKey("CONTENT");
        UcrPrefixResolver.PrefixToAreaMap.Should().ContainKey("Content");
        UcrPrefixResolver.PrefixToAreaMap.Should().ContainKey("DATA");
        UcrPrefixResolver.PrefixToAreaMap.Should().ContainKey("Data");
    }

    [MeshTheory]
    [MeshInlineData("content:deep/nested/path/to/file.md", "$Content", "deep/nested/path/to/file.md")]
    [MeshInlineData("data:collection/nested/entity", "$Data", "collection/nested/entity")]
    public void TryResolve_WithNestedPaths_PreservesFullPath(
        string input, string expectedArea, string expectedPath)
    {
        // Act
        var result = UcrPrefixResolver.TryResolve(input, out var area, out var remainingPath);

        // Assert
        result.Should().BeTrue();
        area.Should().Be(expectedArea);
        remainingPath.Should().Be(expectedPath);
    }

    [MeshTheory]
    [MeshInlineData("content:file with spaces.txt", "$Content", "file with spaces.txt")]
    [MeshInlineData("data:entity-with-dashes", "$Data", "entity-with-dashes")]
    [MeshInlineData("schema:Type_With_Underscores", "$Schema", "Type_With_Underscores")]
    public void TryResolve_WithSpecialCharactersInPath_PreservesPath(
        string input, string expectedArea, string expectedPath)
    {
        // Act
        var result = UcrPrefixResolver.TryResolve(input, out var area, out var remainingPath);

        // Assert
        result.Should().BeTrue();
        area.Should().Be(expectedArea);
        remainingPath.Should().Be(expectedPath);
    }
}
