using System;
using System.Threading.Tasks;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Persistence.Parsers;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Content.Test;

/// <summary>
/// Unit tests for MarkdownFileParser - parsing and serialization of markdown files with YAML front matter.
/// </summary>
public class MarkdownFileParserTest
{
    private readonly MarkdownFileParser _parser = new();

    #region Parse Tests

    [Fact(Timeout = 20000)]
    public void Parse_WithFullYamlFrontMatter_ExtractsAllProperties()
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
        var node = _parser.Parse("/test/article.md", content, "test/article.md");

        // Assert
        node.Should().NotBeNull();
        node!.Id.Should().Be("article");
        node.Namespace.Should().Be("test");
        node.NodeType.Should().Be("Article");
        node.Name.Should().Be("My Article");
        node.Category.Should().Be("Documentation");
        node.Icon.Should().Be("BookOpen");
        node.State.Should().Be(MeshNodeState.Transient);
        // The YAML Description/Abstract is surfaced on the node's Description column so
        // catalog/TOC cards and Postgres search show a real one-line summary.
        node.Description.Should().Be("A detailed article");

        var mdContent = node.Content.Should().BeOfType<MarkdownContent>().Subject;
        mdContent.Authors.Should().BeEquivalentTo(new[] { "John Doe", "Jane Smith" }, System.Text.Json.JsonSerializerOptions.Default);
        mdContent.Tags.Should().BeEquivalentTo(new[] { "tutorial", "beginner" }, System.Text.Json.JsonSerializerOptions.Default);
        mdContent.Thumbnail.Should().Be("/images/thumb.png");
        mdContent.Abstract.Should().Be("A detailed article"); // Mapped from Description
        mdContent.Content.Should().Contain("# My Article");
    }

    [Fact(Timeout = 20000)]
    public void Parse_ExcludeFromContext_RoundTrips()
    {
        // A marketing/landing page ships chrome-less: `ExcludeFromContext: [header]`
        // maps 1:1 onto MeshNode.ExcludeFromContext (the ONE visibility mechanism —
        // no parallel HideHeader flag) and must survive the serialize round-trip.
        var content = """
            ---
            Name: Landing
            ExcludeFromContext:
              - header
            ---

            Full-bleed hero starts here.
            """;

        var node = _parser.Parse("/test/landing.md", content, "test/landing.md");

        node.Should().NotBeNull();
        node!.ExcludeFromContext.Should().BeEquivalentTo(new[] { "header" }, System.Text.Json.JsonSerializerOptions.Default);
        node.IsExcludedFromContext(MeshNodeVisibility.HeaderContext).Should().BeTrue();

        var serialized = _parser.Serialize(node);
        serialized.Should().Contain("ExcludeFromContext:");
        serialized.Should().Contain("header");

        // And a node without the opt-out never emits the key.
        var plain = _parser.Parse("/test/plain.md", "Just text.", "test/plain.md");
        plain!.ExcludeFromContext.Should().BeNull();
        _parser.Serialize(plain).Should().NotContain("ExcludeFromContext");
    }

    [Fact(Timeout = 20000)]
    public void Parse_WithMinimalYaml_UsesDefaults()
    {
        // Arrange
        var content = """
            ---
            Name: Simple Doc
            ---

            Content here.
            """;

        // Act
        var node = _parser.Parse("/docs/simple.md", content, "docs/simple.md");

        // Assert
        node.Should().NotBeNull();
        node!.Name.Should().Be("Simple Doc");
        node.NodeType.Should().Be("Markdown"); // Default
        node.Icon.Should().Be("Document"); // Default
        node.State.Should().Be(MeshNodeState.Active); // Default
    }

    [Fact(Timeout = 20000)]
    public void Parse_WithoutYaml_UsesIdAsName()
    {
        // Arrange
        var content = """
            # Plain Markdown

            No YAML here.
            """;

        // Act
        var node = _parser.Parse("/docs/plain.md", content, "docs/plain.md");

        // Assert
        node.Should().NotBeNull();
        node!.Id.Should().Be("plain");
        node.Name.Should().Be("plain"); // Defaults to Id
        node.NodeType.Should().Be("Markdown");
    }

    [Fact(Timeout = 20000)]
    public void Parse_WithLegacyArticleProperties_MapsCorrectly()
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
        var node = _parser.Parse("/articles/legacy.md", content, "articles/legacy.md");

        // Assert
        node.Should().NotBeNull();
        node!.Name.Should().Be("Legacy Article"); // Mapped from Title

        var mdContent = node.Content.Should().BeOfType<MarkdownContent>().Subject;
        mdContent.Abstract.Should().Be("This is the legacy abstract"); // Mapped from Abstract
    }

    [Fact(Timeout = 20000)]
    public void Parse_DerivesPathFromRelativePath()
    {
        // Arrange
        var content = "# Test";

        // Act
        var node = _parser.Parse("/root/folder/subfolder/doc.md", content, "folder/subfolder/doc.md");

        // Assert
        node.Should().NotBeNull();
        node!.Id.Should().Be("doc");
        node.Namespace.Should().Be("folder/subfolder");
        node.Path.Should().Be("folder/subfolder/doc");
    }

    [Fact(Timeout = 20000)]
    public void Parse_RelativeIconPath_ResolvesToAbsoluteUrl()
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
        var node = _parser.Parse("/root/Cornerstone/doc.md", content, "Cornerstone/doc.md");

        // Assert
        node.Should().NotBeNull();
        node!.Icon.Should().Be("/static/storage/content/Cornerstone/icons/custom.svg");
    }

    [Fact(Timeout = 20000)]
    public void Parse_AbsoluteIconPath_PassesThroughUnchanged()
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
        var node = _parser.Parse("/root/ns/doc.md", content, "ns/doc.md");

        // Assert
        node.Should().NotBeNull();
        node!.Icon.Should().Be("/static/storage/content/path/icon.svg");
    }

    [Fact(Timeout = 20000)]
    public void Parse_HttpUrlIcon_PassesThroughUnchanged()
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
        var node = _parser.Parse("/root/ns/doc.md", content, "ns/doc.md");

        // Assert
        node.Should().NotBeNull();
        node!.Icon.Should().Be("https://example.com/icon.svg");
    }

    #endregion

    #region Serialize Tests

    [Fact(Timeout = 20000)]
    public void Serialize_WithAllProperties_WritesCompleteYaml()
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
        var result = _parser.Serialize(node);

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

    [Fact(Timeout = 20000)]
    public void Serialize_OmitsDefaultValues()
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
        var result = _parser.Serialize(node);

        // Assert - should not have YAML block since all values are defaults
        result.Should().NotContain("NodeType:");
        result.Should().NotContain("Name:");
        result.Should().NotContain("Icon:");
        result.Should().NotContain("State:");
        result.Should().Contain("# Content");
    }

    [Fact(Timeout = 20000)]
    public void Serialize_WithStringContent_WritesDirectly()
    {
        // Arrange
        var node = new MeshNode("doc", "test")
        {
            Name = "String Content Doc",
            Content = "# Direct String\n\nThis is string content."
        };

        // Act
        var result = _parser.Serialize(node);

        // Assert
        result.Should().Contain("Name: String Content Doc");
        result.Should().Contain("# Direct String");
        result.Should().Contain("This is string content.");
    }

    [Fact(Timeout = 20000)]
    public void Serialize_WithNullContent_WritesOnlyYaml()
    {
        // Arrange
        var node = new MeshNode("doc", "test")
        {
            Name = "No Content Doc",
            Category = "Empty",
            Content = null
        };

        // Act
        var result = _parser.Serialize(node);

        // Assert
        result.Should().Contain("Name: No Content Doc");
        result.Should().Contain("Category: Empty");
        // Should end after YAML block
    }

    [Fact(Timeout = 20000)]
    public void Serialize_ResolvedAbsoluteIconPath_IsOmittedFromYaml()
    {
        // Arrange - Icon starts with /static/storage/content/ (was resolved from relative path)
        var node = new MeshNode("doc", "ns")
        {
            Name = "Test Node",
            Icon = "/static/storage/content/ns/icons/custom.svg",
            Content = new MarkdownContent { Content = "# Content" }
        };

        // Act
        var result = _parser.Serialize(node);

        // Assert - The serialization guard should strip resolved paths
        result.Should().NotContain("Icon:");
        result.Should().Contain("Name: Test Node");
    }

    #endregion

    #region Round-Trip Tests

    [Fact(Timeout = 20000)]
    public void RoundTrip_RelativeIcon_ResolvesCorrectlyAfterReParse()
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
        var node = _parser.Parse("/root/Cornerstone/doc.md", originalContent, "Cornerstone/doc.md");
        node!.Icon.Should().Be("/static/storage/content/Cornerstone/icons/custom.svg");

        var serialized = _parser.Serialize(node);

        // The resolved absolute path should NOT appear in serialized YAML
        serialized.Should().NotContain("/static/storage/content/");

        // Re-parse — Thumbnail is still in YAML and should resolve again
        var reparsed = _parser.Parse("/root/Cornerstone/doc.md", serialized, "Cornerstone/doc.md");

        // Assert — icon resolves to the same absolute URL (no double-resolution)
        reparsed.Should().NotBeNull();
        reparsed!.Icon.Should().Be("/static/storage/content/Cornerstone/icons/custom.svg");
    }

    [Fact(Timeout = 20000)]
    public void RoundTrip_PreservesAllData()
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
        var node = _parser.Parse("/tutorials/complete.md", originalContent, "tutorials/complete.md");
        var serialized = _parser.Serialize(node!);

        // Re-parse to verify
        var reparsed = _parser.Parse("/tutorials/complete.md", serialized, "tutorials/complete.md");

        // Assert
        reparsed.Should().NotBeNull();
        reparsed!.NodeType.Should().Be("Tutorial");
        reparsed.Name.Should().Be("Complete Tutorial");
        reparsed.Category.Should().Be("Learning");
        reparsed.Icon.Should().Be("School");

        var mdContent = reparsed.Content.Should().BeOfType<MarkdownContent>().Subject;
        mdContent.Authors.Should().BeEquivalentTo(new[] { "Teacher One" }, System.Text.Json.JsonSerializerOptions.Default);
        mdContent.Tags.Should().BeEquivalentTo(new[] { "advanced" }, System.Text.Json.JsonSerializerOptions.Default);
        mdContent.Abstract.Should().Be("A comprehensive tutorial");
        mdContent.Content.Should().Contain("# Tutorial Title");
        mdContent.Content.Should().Contain("Step 1: Do this.");
    }

    #endregion

    #region Slide + Order Frontmatter (Notes / Background / Order)

    [Fact(Timeout = 20000)]
    public void RoundTrip_SlideNode_PreservesNotesBackgroundAndOrder()
    {
        // Arrange - a typed Slide node as the mesh holds it (framework SlideContent)
        var node = new MeshNode("S01", "deck")
        {
            NodeType = "Slide",
            Name = "Opening",
            Order = 1,
            Content = new SlideContent
            {
                Content = "# Opening\n\nWelcome to the deck.",
                Notes = "Speak slowly.\nPause after the title.",
                Background = "linear-gradient(135deg, #667eea 0%, #764ba2 100%)"
            }
        };

        // Act - serialize to canonical .md, then parse back
        var serialized = _parser.Serialize(node);
        var reparsed = _parser.Parse("/root/deck/S01.md", serialized, "deck/S01.md");

        // Assert - the .md carries everything in frontmatter + body
        serialized.Should().Contain("NodeType: Slide");
        serialized.Should().Contain("Order: 1");
        serialized.Should().Contain("# Opening");

        // ... and a git reimport no longer downgrades Slide → Markdown
        reparsed.Should().NotBeNull();
        reparsed!.NodeType.Should().Be("Slide");
        reparsed.Name.Should().Be("Opening");
        reparsed.Order.Should().Be(1);
        var slide = reparsed.Content.Should().BeOfType<SlideContent>().Subject;
        slide.Content.Should().Be("# Opening\n\nWelcome to the deck.");
        slide.Notes.Should().Be("Speak slowly.\nPause after the title.");
        slide.Background.Should().Be("linear-gradient(135deg, #667eea 0%, #764ba2 100%)");
    }

    [Fact(Timeout = 20000)]
    public void Serialize_SlideNode_WithJsonElementContent_EmitsNotesBackgroundAndBody()
    {
        // Arrange - the untyped wire form a hub without the typed registry resolves
        var json = """
            {"$type":"SlideContent","content":"# Body slide","notes":"Presenter note.","background":"#123456"}
            """;
        var node = new MeshNode("S02", "deck")
        {
            NodeType = "Slide",
            Name = "S02",
            Order = 2,
            Content = System.Text.Json.JsonDocument.Parse(json).RootElement
        };

        // Act
        var serialized = _parser.Serialize(node);
        var reparsed = _parser.Parse("/root/deck/S02.md", serialized, "deck/S02.md");

        // Assert
        serialized.Should().Contain("NodeType: Slide");
        serialized.Should().Contain("Order: 2");
        serialized.Should().Contain("Presenter note.");
        serialized.Should().Contain("# Body slide");

        reparsed!.NodeType.Should().Be("Slide");
        reparsed.Order.Should().Be(2);
        var slide = reparsed.Content.Should().BeOfType<SlideContent>().Subject;
        slide.Content.Should().Be("# Body slide");
        slide.Notes.Should().Be("Presenter note.");
        slide.Background.Should().Be("#123456");
    }

    [Fact(Timeout = 20000)]
    public void Parse_NamespacedSlideNode_BuildsSlideContent_AndRoundTrips()
    {
        // Arrange - a plugin-namespaced slide file exactly as the education repo ships
        // it (NodeType Slides/Slide). Matching only the bare "Slide" constant degraded
        // these to MarkdownContent, and the next mesh→repo export dropped their
        // Notes/Background frontmatter from git.
        var raw = """
            ---
            NodeType: Slides/Slide
            Name: After a while
            Order: 4
            Notes: Pause here. This is the payoff of the daily pages.
            Background: linear-gradient(135deg,#0b1d3a,#0655bf)
            ---

            # After a while
            """;

        // Act
        var parsed = _parser.Parse("/root/deck/S04.md", raw, "deck/S04.md");
        var serialized = _parser.Serialize(parsed!);

        // Assert - the namespaced type keeps its typed SlideContent ...
        parsed!.NodeType.Should().Be("Slides/Slide");
        var slide = parsed.Content.Should().BeOfType<SlideContent>().Subject;
        slide.Notes.Should().Be("Pause here. This is the payoff of the daily pages.");
        slide.Background.Should().Be("linear-gradient(135deg,#0b1d3a,#0655bf)");

        // ... and the export re-emits the frontmatter instead of destroying it
        serialized.Should().Contain("NodeType: Slides/Slide");
        serialized.Should().Contain("Notes: Pause here. This is the payoff of the daily pages.");
        serialized.Should().Contain("Background: linear-gradient(135deg,#0b1d3a,#0655bf)");
    }

    [Fact(Timeout = 20000)]
    public void Serialize_NamespacedSlideNode_WithJsonElementContent_EmitsNotesAndBackground()
    {
        // Arrange - untyped wire form of a namespaced slide (hub without typed registry)
        var json = """
            {"$type":"SlideContent","content":"# Body","notes":"A note.","background":"#0b1d3a"}
            """;
        var node = new MeshNode("S05", "deck")
        {
            NodeType = "Slides/Slide",
            Name = "S05",
            Content = System.Text.Json.JsonDocument.Parse(json).RootElement
        };

        // Act
        var serialized = _parser.Serialize(node);

        // Assert
        serialized.Should().Contain("NodeType: Slides/Slide");
        serialized.Should().Contain("Notes: A note.");
        serialized.Should().Contain("Background:").And.Contain("#0b1d3a");
        serialized.Should().Contain("# Body");
    }

    [Fact(Timeout = 20000)]
    public void RoundTrip_OrderedMarkdownNode_PreservesOrder()
    {
        // Arrange - Order applies to ANY node type (lesson/module ordering)
        var node = new MeshNode("L05", "course")
        {
            Name = "Lesson Five",
            Order = 5,
            Content = new MarkdownContent { Content = "# Lesson 5" }
        };

        // Act
        var serialized = _parser.Serialize(node);
        var reparsed = _parser.Parse("/root/course/L05.md", serialized, "course/L05.md");

        // Assert - Order round-trips; content stays MarkdownContent
        serialized.Should().Contain("Order: 5");
        reparsed!.Order.Should().Be(5);
        reparsed.NodeType.Should().Be("Markdown");
        reparsed.Content.Should().BeOfType<MarkdownContent>();
    }

    [Fact(Timeout = 20000)]
    public void Serialize_NodeWithoutNewKeys_EmissionIsByteIdenticalToLegacyFormat()
    {
        // Arrange - a node carrying none of Order/Notes/Background. Its emission is
        // pinned byte-for-byte: the new frontmatter keys are OmitNull and declared
        // AFTER the legacy keys, so existing git mirrors see zero diff churn.
        var node = new MeshNode("doc", "test")
        {
            NodeType = "Article",
            Name = "My Doc",
            Category = "Docs",
            Content = new MarkdownContent
            {
                Content = "# Body\n\nText.",
                Abstract = "Summary"
            }
        };

        var nl = Environment.NewLine;
        var expected =
            "---" + nl +
            "NodeType: Article" + nl +
            "Name: My Doc" + nl +
            "Category: Docs" + nl +
            "Abstract: Summary" + nl +
            "---" + nl +
            nl +
            "# Body\n\nText.";

        // Act
        var serialized = _parser.Serialize(node);

        // Assert
        serialized.Should().Be(expected);
    }

    [Fact(Timeout = 20000)]
    public void Parse_LegacyFileWithoutNewKeys_ParsesUnchanged()
    {
        // Arrange - a pre-existing mirror file that never carried the new keys
        var content = """
            ---
            NodeType: Article
            Name: Legacy File
            ---

            # Legacy

            Body.
            """;

        // Act
        var node = _parser.Parse("/test/legacy.md", content, "test/legacy.md");

        // Assert - no Order, regular MarkdownContent, nothing invented
        node.Should().NotBeNull();
        node!.NodeType.Should().Be("Article");
        node.Order.Should().BeNull();
        node.Content.Should().BeOfType<MarkdownContent>();
    }

    [Fact(Timeout = 20000)]
    public void Parse_LegacySlideFileWithoutNotesOrBackground_ReconstructsSlideContent()
    {
        // Arrange - a Slide exported before the frontmatter carried Notes/Background
        var content = """
            ---
            NodeType: Slide
            Name: Old Slide
            ---

            # Old slide body
            """;

        // Act
        var node = _parser.Parse("/deck/old.md", content, "deck/old.md");

        // Assert - Slide is reconstructed typed (no downgrade to Markdown), extras null
        node.Should().NotBeNull();
        node!.NodeType.Should().Be("Slide");
        var slide = node.Content.Should().BeOfType<SlideContent>().Subject;
        slide.Content.Should().Contain("# Old slide body");
        slide.Notes.Should().BeNull();
        slide.Background.Should().BeNull();
    }

    [Fact(Timeout = 20000)]
    public void Parse_NonSlideFileWithStrayNotes_IgnoresThem()
    {
        // Arrange - Notes/Background only mean something on a Slide; on any other
        // node type they are ignored (MarkdownContent has no such fields, and
        // inventing an untyped content to carry them would downgrade the node).
        var content = """
            ---
            Name: Not A Slide
            Notes: stray presenter notes
            Background: red
            ---

            Body.
            """;

        // Act
        var node = _parser.Parse("/test/plain.md", content, "test/plain.md");
        var reserialized = _parser.Serialize(node!);

        // Assert
        node!.Content.Should().BeOfType<MarkdownContent>();
        reserialized.Should().NotContain("Notes:");
        reserialized.Should().NotContain("Background:");
    }

    #endregion

    #region CanSerialize Tests

    [Fact(Timeout = 20000)]
    public void CanSerialize_WithMarkdownNodeType_ReturnsTrue()
    {
        var node = new MeshNode("doc") { NodeType = "Markdown" };
        _parser.CanSerialize(node).Should().BeTrue();
    }

    [Fact(Timeout = 20000)]
    public void CanSerialize_WithMarkdownContent_ReturnsTrue()
    {
        var node = new MeshNode("doc") { Content = new MarkdownContent { Content = "test" } };
        _parser.CanSerialize(node).Should().BeTrue();
    }

    [Fact(Timeout = 20000)]
    public void CanSerialize_WithStringContent_ReturnsTrue()
    {
        var node = new MeshNode("doc") { Content = "# Markdown string" };
        _parser.CanSerialize(node).Should().BeTrue();
    }

    [Fact(Timeout = 20000)]
    public void CanSerialize_WithTypedSlideContent_ReturnsTrue()
    {
        // Typed SlideContent serializes as canonical .md (frontmatter carries
        // Notes/Background) — not as a .json fallback.
        var node = new MeshNode("S01") { NodeType = "Slide", Content = new SlideContent { Content = "# Slide" } };
        _parser.CanSerialize(node).Should().BeTrue();
    }

    [Fact(Timeout = 20000)]
    public void CanSerialize_WithOtherNodeType_ReturnsFalse()
    {
        var node = new MeshNode("doc") { NodeType = "Space", Content = new { Id = "test" } };
        _parser.CanSerialize(node).Should().BeFalse();
    }

    #endregion

    #region Edge Cases

    [Fact(Timeout = 20000)]
    public void Parse_WithMalformedYaml_UsesDefaults()
    {
        // Arrange
        var content = """
            ---
            Name: [invalid yaml
            ---

            Content.
            """;

        // Act
        var node = _parser.Parse("/test/malformed.md", content, "test/malformed.md");

        // Assert - Should not throw, uses defaults
        node.Should().NotBeNull();
        node!.Id.Should().Be("malformed");
        node.Name.Should().Be("malformed"); // Falls back to Id
    }

    [Fact(Timeout = 20000)]
    public void Parse_WithEmptyContent_ReturnsEmptyMarkdownContent()
    {
        // Arrange
        var content = "";

        // Act
        var node = _parser.Parse("/test/empty.md", content, "test/empty.md");

        // Assert
        node.Should().NotBeNull();
        var mdContent = node!.Content.Should().BeOfType<MarkdownContent>().Subject;
        mdContent.Content.Should().BeEmpty();
    }

    [Fact(Timeout = 20000)]
    public void Serialize_WithSpecialCharacters_EscapesCorrectly()
    {
        // Arrange
        var node = new MeshNode("doc", "test")
        {
            Name = "Doc with: colons",
            Content = new MarkdownContent { Content = "# Content" }
        };

        // Act
        var result = _parser.Serialize(node);

        // Parse back to verify
        var reparsed = _parser.Parse("/test/doc.md", result, "test/doc.md");

        // Assert
        reparsed!.Name.Should().Be("Doc with: colons");
    }

    #endregion
}
