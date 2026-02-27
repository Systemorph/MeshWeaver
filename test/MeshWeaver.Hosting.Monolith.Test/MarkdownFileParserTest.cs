using System;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Hosting.Persistence.Parsers;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Unit tests for MarkdownFileParser - parsing and serialization of markdown files with YAML front matter.
/// </summary>
public class MarkdownFileParserTest
{
    private readonly MarkdownFileParser _parser = new();

    #region Parse Tests

    [Fact(Timeout = 10000)]
    public async Task Parse_WithFullYamlFrontMatter_ExtractsAllProperties()
    {
        // Arrange
        var content = """
            ---
            NodeType: Article
            Name: My Article
            Category: Documentation
            Description: A detailed article
            Icon: BookOpen
            State: Transient
            Authors:
              - John Doe
              - Jane Smith
            Tags:
              - tutorial
              - beginner
            Thumbnail: /images/thumb.png
            ---

            # My Article

            This is the content.
            """;

        // Act
        var node = await _parser.ParseAsync("/test/article.md", content, "test/article.md");

        // Assert
        node.Should().NotBeNull();
        node!.Id.Should().Be("article");
        node.Namespace.Should().Be("test");
        node.NodeType.Should().Be("Article");
        node.Name.Should().Be("My Article");
        node.Category.Should().Be("Documentation");
        node.Icon.Should().Be("BookOpen");
        node.State.Should().Be(MeshNodeState.Transient);

        var mdContent = node.Content.Should().BeOfType<MarkdownContent>().Subject;
        mdContent.Authors.Should().BeEquivalentTo(["John Doe", "Jane Smith"]);
        mdContent.Tags.Should().BeEquivalentTo(["tutorial", "beginner"]);
        mdContent.Thumbnail.Should().Be("/images/thumb.png");
        mdContent.Abstract.Should().Be("A detailed article"); // Mapped from Description
        mdContent.Content.Should().Contain("# My Article");
    }

    [Fact(Timeout = 10000)]
    public async Task Parse_WithMinimalYaml_UsesDefaults()
    {
        // Arrange
        var content = """
            ---
            Name: Simple Doc
            ---

            Content here.
            """;

        // Act
        var node = await _parser.ParseAsync("/docs/simple.md", content, "docs/simple.md");

        // Assert
        node.Should().NotBeNull();
        node!.Name.Should().Be("Simple Doc");
        node.NodeType.Should().Be("Markdown"); // Default
        node.Icon.Should().Be("Document"); // Default
        node.State.Should().Be(MeshNodeState.Active); // Default
    }

    [Fact(Timeout = 10000)]
    public async Task Parse_WithoutYaml_UsesIdAsName()
    {
        // Arrange
        var content = """
            # Plain Markdown

            No YAML here.
            """;

        // Act
        var node = await _parser.ParseAsync("/docs/plain.md", content, "docs/plain.md");

        // Assert
        node.Should().NotBeNull();
        node!.Id.Should().Be("plain");
        node.Name.Should().Be("plain"); // Defaults to Id
        node.NodeType.Should().Be("Markdown");
    }

    [Fact(Timeout = 10000)]
    public async Task Parse_WithLegacyArticleProperties_MapsCorrectly()
    {
        // Arrange - Uses legacy Title/Abstract instead of Name/Description
        var content = """
            ---
            Title: Legacy Article
            Abstract: This is the legacy abstract
            ---

            Content.
            """;

        // Act
        var node = await _parser.ParseAsync("/articles/legacy.md", content, "articles/legacy.md");

        // Assert
        node.Should().NotBeNull();
        node!.Name.Should().Be("Legacy Article"); // Mapped from Title

        var mdContent = node.Content.Should().BeOfType<MarkdownContent>().Subject;
        mdContent.Abstract.Should().Be("This is the legacy abstract"); // Mapped from Abstract
    }

    [Fact(Timeout = 10000)]
    public async Task Parse_DerivesPathFromRelativePath()
    {
        // Arrange
        var content = "# Test";

        // Act
        var node = await _parser.ParseAsync("/root/folder/subfolder/doc.md", content, "folder/subfolder/doc.md");

        // Assert
        node.Should().NotBeNull();
        node!.Id.Should().Be("doc");
        node.Namespace.Should().Be("folder/subfolder");
        node.Path.Should().Be("folder/subfolder/doc");
    }

    [Fact(Timeout = 10000)]
    public async Task Parse_RelativeIconPath_ResolvesToAbsoluteUrl()
    {
        // Arrange
        var content = """
            ---
            Name: Test Node
            Icon: icons/custom.svg
            ---

            Content.
            """;

        // Act
        var node = await _parser.ParseAsync("/root/ACME/Insurance/doc.md", content, "ACME/Insurance/doc.md");

        // Assert
        node.Should().NotBeNull();
        node!.Icon.Should().Be("/static/storage/content/ACME/Insurance/icons/custom.svg");
    }

    [Fact(Timeout = 10000)]
    public async Task Parse_AbsoluteIconPath_PassesThroughUnchanged()
    {
        // Arrange
        var content = """
            ---
            Name: Test Node
            Icon: /static/storage/content/path/icon.svg
            ---

            Content.
            """;

        // Act
        var node = await _parser.ParseAsync("/root/ns/doc.md", content, "ns/doc.md");

        // Assert
        node.Should().NotBeNull();
        node!.Icon.Should().Be("/static/storage/content/path/icon.svg");
    }

    [Fact(Timeout = 10000)]
    public async Task Parse_HttpUrlIcon_PassesThroughUnchanged()
    {
        // Arrange
        var content = """
            ---
            Name: Test Node
            Icon: https://example.com/icon.svg
            ---

            Content.
            """;

        // Act
        var node = await _parser.ParseAsync("/root/ns/doc.md", content, "ns/doc.md");

        // Assert
        node.Should().NotBeNull();
        node!.Icon.Should().Be("https://example.com/icon.svg");
    }

    #endregion

    #region Serialize Tests

    [Fact(Timeout = 10000)]
    public async Task Serialize_WithAllProperties_WritesCompleteYaml()
    {
        // Arrange
        var node = new MeshNode("doc", "test")
        {
            NodeType = "Article",
            Name = "Test Article",
            Category = "Docs",
            Icon = "BookOpen",
            State = MeshNodeState.Transient,
            Content = new MarkdownContent
            {
                Content = "# Hello World\n\nContent here.",
                Authors = ["Author 1", "Author 2"],
                Tags = ["tag1", "tag2"],
                Thumbnail = "/thumb.png",
                Abstract = "A brief summary"
            }
        };

        // Act
        var result = await _parser.SerializeAsync(node);

        // Assert
        result.Should().Contain("---");
        result.Should().Contain("NodeType: Article");
        result.Should().Contain("Name: Test Article");
        result.Should().Contain("Category: Docs");
        result.Should().Contain("Icon: BookOpen");
        result.Should().Contain("State: Transient");
        result.Should().Contain("Author 1");
        result.Should().Contain("Author 2");
        result.Should().Contain("tag1");
        result.Should().Contain("tag2");
        result.Should().Contain("Thumbnail: /thumb.png");
        result.Should().Contain("Abstract: A brief summary");
        result.Should().Contain("# Hello World");
        result.Should().Contain("Content here.");
    }

    [Fact(Timeout = 10000)]
    public async Task Serialize_OmitsDefaultValues()
    {
        // Arrange - Node with default values that should be omitted
        var node = new MeshNode("doc", "test")
        {
            NodeType = "Markdown", // Default - should be omitted
            Name = "doc", // Same as Id - should be omitted
            Icon = "Document", // Default - should be omitted
            State = MeshNodeState.Active, // Default - should be omitted
            Content = new MarkdownContent { Content = "# Content" }
        };

        // Act
        var result = await _parser.SerializeAsync(node);

        // Assert - should not have YAML block since all values are defaults
        result.Should().NotContain("NodeType:");
        result.Should().NotContain("Name:");
        result.Should().NotContain("Icon:");
        result.Should().NotContain("State:");
        result.Should().Contain("# Content");
    }

    [Fact(Timeout = 10000)]
    public async Task Serialize_WithStringContent_WritesDirectly()
    {
        // Arrange
        var node = new MeshNode("doc", "test")
        {
            Name = "String Content Doc",
            Content = "# Direct String\n\nThis is string content."
        };

        // Act
        var result = await _parser.SerializeAsync(node);

        // Assert
        result.Should().Contain("Name: String Content Doc");
        result.Should().Contain("# Direct String");
        result.Should().Contain("This is string content.");
    }

    [Fact(Timeout = 10000)]
    public async Task Serialize_WithNullContent_WritesOnlyYaml()
    {
        // Arrange
        var node = new MeshNode("doc", "test")
        {
            Name = "No Content Doc",
            Category = "Empty",
            Content = null
        };

        // Act
        var result = await _parser.SerializeAsync(node);

        // Assert
        result.Should().Contain("Name: No Content Doc");
        result.Should().Contain("Category: Empty");
        // Should end after YAML block
    }

    [Fact(Timeout = 10000)]
    public async Task Serialize_ResolvedAbsoluteIconPath_IsOmittedFromYaml()
    {
        // Arrange - Icon starts with /static/storage/content/ (was resolved from relative path)
        var node = new MeshNode("doc", "ns")
        {
            Name = "Test Node",
            Icon = "/static/storage/content/ns/icons/custom.svg",
            Content = new MarkdownContent { Content = "# Content" }
        };

        // Act
        var result = await _parser.SerializeAsync(node);

        // Assert - The serialization guard should strip resolved paths
        result.Should().NotContain("Icon:");
        result.Should().Contain("Name: Test Node");
    }

    #endregion

    #region Round-Trip Tests

    [Fact(Timeout = 10000)]
    public async Task RoundTrip_RelativeIcon_ResolvesCorrectlyAfterReParse()
    {
        // Arrange - Markdown with a relative icon path
        var originalContent = """
            ---
            Name: Icon Test
            Thumbnail: icons/custom.svg
            ---

            # Icon Round-Trip Test
            """;

        // Act - Parse (resolves relative icon), serialize, re-parse
        var node = await _parser.ParseAsync("/root/ACME/Insurance/doc.md", originalContent, "ACME/Insurance/doc.md");
        node!.Icon.Should().Be("/static/storage/content/ACME/Insurance/icons/custom.svg");

        var serialized = await _parser.SerializeAsync(node);

        // The resolved absolute path should NOT appear in serialized YAML
        serialized.Should().NotContain("/static/storage/content/");

        // Re-parse — Thumbnail is still in YAML and should resolve again
        var reparsed = await _parser.ParseAsync("/root/ACME/Insurance/doc.md", serialized, "ACME/Insurance/doc.md");

        // Assert — icon resolves to the same absolute URL (no double-resolution)
        reparsed.Should().NotBeNull();
        reparsed!.Icon.Should().Be("/static/storage/content/ACME/Insurance/icons/custom.svg");
    }

    [Fact(Timeout = 10000)]
    public async Task RoundTrip_PreservesAllData()
    {
        // Arrange
        var originalContent = """
            ---
            NodeType: Tutorial
            Name: Complete Tutorial
            Category: Learning
            Description: A comprehensive tutorial
            Abstract: A comprehensive tutorial
            Icon: School
            Authors:
              - Teacher One
            Tags:
              - advanced
            ---

            # Tutorial Title

            Step 1: Do this.
            Step 2: Do that.
            """;

        // Act - Parse then serialize
        var node = await _parser.ParseAsync("/tutorials/complete.md", originalContent, "tutorials/complete.md");
        var serialized = await _parser.SerializeAsync(node!);

        // Re-parse to verify
        var reparsed = await _parser.ParseAsync("/tutorials/complete.md", serialized, "tutorials/complete.md");

        // Assert
        reparsed.Should().NotBeNull();
        reparsed!.NodeType.Should().Be("Tutorial");
        reparsed.Name.Should().Be("Complete Tutorial");
        reparsed.Category.Should().Be("Learning");
        reparsed.Icon.Should().Be("School");

        var mdContent = reparsed.Content.Should().BeOfType<MarkdownContent>().Subject;
        mdContent.Authors.Should().BeEquivalentTo(["Teacher One"]);
        mdContent.Tags.Should().BeEquivalentTo(["advanced"]);
        mdContent.Abstract.Should().Be("A comprehensive tutorial");
        mdContent.Content.Should().Contain("# Tutorial Title");
        mdContent.Content.Should().Contain("Step 1: Do this.");
    }

    #endregion

    #region CanSerialize Tests

    [Fact(Timeout = 10000)]
    public void CanSerialize_WithMarkdownNodeType_ReturnsTrue()
    {
        var node = new MeshNode("doc") { NodeType = "Markdown" };
        _parser.CanSerialize(node).Should().BeTrue();
    }

    [Fact(Timeout = 10000)]
    public void CanSerialize_WithMarkdownContent_ReturnsTrue()
    {
        var node = new MeshNode("doc") { Content = new MarkdownContent { Content = "test" } };
        _parser.CanSerialize(node).Should().BeTrue();
    }

    [Fact(Timeout = 10000)]
    public void CanSerialize_WithStringContent_ReturnsTrue()
    {
        var node = new MeshNode("doc") { Content = "# Markdown string" };
        _parser.CanSerialize(node).Should().BeTrue();
    }

    [Fact(Timeout = 10000)]
    public void CanSerialize_WithOtherNodeType_ReturnsFalse()
    {
        var node = new MeshNode("doc") { NodeType = "Organization", Content = new { Id = "test" } };
        _parser.CanSerialize(node).Should().BeFalse();
    }

    #endregion

    #region Edge Cases

    [Fact(Timeout = 10000)]
    public async Task Parse_WithMalformedYaml_UsesDefaults()
    {
        // Arrange
        var content = """
            ---
            Name: [invalid yaml
            ---

            Content.
            """;

        // Act
        var node = await _parser.ParseAsync("/test/malformed.md", content, "test/malformed.md");

        // Assert - Should not throw, uses defaults
        node.Should().NotBeNull();
        node!.Id.Should().Be("malformed");
        node.Name.Should().Be("malformed"); // Falls back to Id
    }

    [Fact(Timeout = 10000)]
    public async Task Parse_WithEmptyContent_ReturnsEmptyMarkdownContent()
    {
        // Arrange
        var content = "";

        // Act
        var node = await _parser.ParseAsync("/test/empty.md", content, "test/empty.md");

        // Assert
        node.Should().NotBeNull();
        var mdContent = node!.Content.Should().BeOfType<MarkdownContent>().Subject;
        mdContent.Content.Should().BeEmpty();
    }

    [Fact(Timeout = 10000)]
    public async Task Serialize_WithSpecialCharacters_EscapesCorrectly()
    {
        // Arrange
        var node = new MeshNode("doc", "test")
        {
            Name = "Doc with: colons",
            Content = new MarkdownContent { Content = "# Content" }
        };

        // Act
        var result = await _parser.SerializeAsync(node);

        // Parse back to verify
        var reparsed = await _parser.ParseAsync("/test/doc.md", result, "test/doc.md");

        // Assert
        reparsed!.Name.Should().Be("Doc with: colons");
    }

    #endregion
}
