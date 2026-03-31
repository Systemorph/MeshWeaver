using FluentAssertions;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// Tests for UcrPrefixResolver which handles Unified Content Reference (UCR) prefix parsing.
/// UCR prefixes (content:, data:, schema:, model:, menu:) map to special areas ($Content, $Data, $Schema, $Model, $Menu).
/// </summary>
public class UcrPrefixResolverTest
{
    [Theory]
    [InlineData("content:logo.svg", "$Content", "logo.svg")]
    [InlineData("content:path/to/file.png", "$Content", "path/to/file.png")]
    [InlineData("content:", "$Content", null)]
    [InlineData("data:entityId", "$Data", "entityId")]
    [InlineData("data:collection/entityId", "$Data", "collection/entityId")]
    [InlineData("data:", "$Data", null)]
    [InlineData("schema:Person", "$Schema", "Person")]
    [InlineData("schema:", "$Schema", null)]
    [InlineData("model:MyModel", "$Model", "MyModel")]
    [InlineData("model:", "$Model", null)]
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

    [Theory]
    [InlineData("Content:logo.svg", "$Content", "logo.svg")]
    [InlineData("CONTENT:logo.svg", "$Content", "logo.svg")]
    [InlineData("Data:test", "$Data", "test")]
    [InlineData("DATA:", "$Data", null)]
    [InlineData("Schema:Type", "$Schema", "Type")]
    [InlineData("SCHEMA:", "$Schema", null)]
    [InlineData("Model:Test", "$Model", "Test")]
    [InlineData("MODEL:", "$Model", null)]
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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Details")]
    [InlineData("Catalog")]
    [InlineData("Settings")]
    [InlineData("unknown:path")]
    [InlineData("file:path")]
    [InlineData("http://example.com")]
    [InlineData("area/subarea")]
    public void TryResolve_WithInvalidPrefix_ReturnsFalse(string? input)
    {
        // Act
        var result = UcrPrefixResolver.TryResolve(input, out var area, out var remainingPath);

        // Assert
        result.Should().BeFalse();
        area.Should().BeNull();
        remainingPath.Should().BeNull();
    }

    [Theory]
    [InlineData("content:logo.svg", "$Content", "logo.svg")]
    [InlineData("data:", "$Data", null)]
    [InlineData("schema:Person", "$Schema", "Person")]
    [InlineData("model:MyModel", "$Model", "MyModel")]
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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Details")]
    [InlineData("unknown:path")]
    public void ResolveToLayoutAreaReference_WithInvalidPrefix_ReturnsNull(string? input)
    {
        // Act
        var result = UcrPrefixResolver.ResolveToLayoutAreaReference(input);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
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

    [Fact]
    public void PrefixToAreaMap_IsCaseInsensitive()
    {
        // Assert - verify case-insensitive lookup works
        UcrPrefixResolver.PrefixToAreaMap.Should().ContainKey("CONTENT");
        UcrPrefixResolver.PrefixToAreaMap.Should().ContainKey("Content");
        UcrPrefixResolver.PrefixToAreaMap.Should().ContainKey("DATA");
        UcrPrefixResolver.PrefixToAreaMap.Should().ContainKey("Data");
    }

    [Theory]
    [InlineData("content:deep/nested/path/to/file.md", "$Content", "deep/nested/path/to/file.md")]
    [InlineData("data:collection/nested/entity", "$Data", "collection/nested/entity")]
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

    [Theory]
    [InlineData("content:file with spaces.txt", "$Content", "file with spaces.txt")]
    [InlineData("data:entity-with-dashes", "$Data", "entity-with-dashes")]
    [InlineData("schema:Type_With_Underscores", "$Schema", "Type_With_Underscores")]
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
