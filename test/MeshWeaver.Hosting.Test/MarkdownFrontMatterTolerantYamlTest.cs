using System.Text.Json;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Persistence.Parsers;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// <see cref="MarkdownFileParser"/> reads and writes front matter with the platform's tolerant YAML
/// reader (<c>Doc/Architecture/TolerantYaml</c>): the legacy emission stays byte-identical, multi-line
/// values round-trip, and front matter the previous reader refused outright now binds.
/// </summary>
public class MarkdownFrontMatterTolerantYamlTest
{
    private static readonly MarkdownFileParser Parser = new();

    private static MarkdownContent? ContentOf(MeshNode? node) =>
        node.ContentAs<MarkdownContent>(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [Fact]
    public void Serialize_LegacyKeys_AreByteIdentical()
    {
        var node = new MeshNode("doc", "test")
        {
            NodeType = "Article",
            Name = "My Doc",
            Category = "Docs",
            Order = 3,
            Content = new MarkdownContent
            {
                Content = "# Body\n\nText.",
                Abstract = "Summary",
                Tags = ["a", "b"],
            }
        };
        var nl = Environment.NewLine;
        Assert.Equal(
            "---" + nl + "NodeType: Article" + nl + "Name: My Doc" + nl + "Category: Docs" + nl
            + "Tags:" + nl + "- a" + nl + "- b" + nl + "Abstract: Summary" + nl + "Order: 3" + nl
            + "---" + nl + nl + "# Body\n\nText.",
            Parser.Serialize(node));
    }

    [Fact]
    public void Serialize_ThenParse_RoundTripsAwkwardValues()
    {
        var node = new MeshNode("S01", "deck")
        {
            NodeType = "Slide",
            Name = "Doc with: colons # and a hash",
            Order = 1,
            Version = 4,
            Content = new SlideContent
            {
                Content = "# Opening",
                Notes = "Speak slowly.\nPause after the title.",
                Background = "linear-gradient(135deg, #667eea 0%, #764ba2 100%)",
            }
        };
        var serialized = Parser.Serialize(node);
        Assert.Contains("Notes: |-", serialized);

        var back = Parser.Parse("/root/deck/S01.md", serialized, "deck/S01.md")!;
        Assert.Equal("Doc with: colons # and a hash", back.Name);
        Assert.Equal(1, back.Order);
        Assert.Equal(4, back.Version);
        var slide = Assert.IsType<SlideContent>(back.Content);
        Assert.Equal("Speak slowly.\nPause after the title.", slide.Notes);
        Assert.Equal("linear-gradient(135deg, #667eea 0%, #764ba2 100%)", slide.Background);
        Assert.Equal("# Opening", slide.Content);
    }

    /// <summary>
    /// #7352: a node's authorship (<see cref="MeshNode.CreatedBy"/> / <see cref="MeshNode.CreatedDate"/>)
    /// survives a write and a read of its <c>.md</c> file to the exact instant — the move resume
    /// (#6310) and the create rollback both recognise a node by these two values.
    /// </summary>
    [Fact]
    public void Serialize_ThenParse_RoundTripsAuthorship()
    {
        var created = new DateTimeOffset(2026, 10, 30, 9, 15, 42, TimeSpan.Zero).AddTicks(1234560);
        var node = new MeshNode("note", "team")
        {
            CreatedBy = "alice@example.com",
            CreatedDate = created,
            Content = new MarkdownContent { Content = "# Note" }
        };

        var back = Parser.Parse("/root/team/note.md", Parser.Serialize(node), "team/note.md")!;

        Assert.Equal("alice@example.com", back.CreatedBy);
        Assert.Equal(created, back.CreatedDate);
        Assert.Equal(created.Offset, back.CreatedDate.Offset);
    }

    /// <summary>
    /// #7352, the authored-tree half: a file that carries no authorship reads back unattributed (it
    /// is never invented), and writing such a node emits no authorship keys — so an authored tree
    /// is not churned by a save. A hand-mangled date degrades to unset instead of failing the file.
    /// </summary>
    [Fact]
    public void Authorship_IsNeitherInventedOnRead_NorEmittedWhenUnset()
    {
        var authored = Parser.Parse("/root/team/page.md", "---\nName: Page\n---\n\n# Page", "team/page.md")!;
        Assert.Null(authored.CreatedBy);
        Assert.Equal(default, authored.CreatedDate);

        var nl = Environment.NewLine;
        Assert.Equal("---" + nl + "Name: Page" + nl + "---" + nl + nl + "# Page", Parser.Serialize(authored));

        var mangled = Parser.Parse("/root/team/odd.md", "---\nName: Odd\nCreatedDate: last tuesday\n---\n\n# Odd", "team/odd.md")!;
        Assert.Equal("Odd", mangled.Name);
        Assert.Equal(default, mangled.CreatedDate);
    }

    /// <summary>
    /// Review of #7180: when Notes is the LAST emitted key its literal block ends the front matter,
    /// so a trim of the whole block cut an authored trailing space off its last line.
    /// </summary>
    [Fact]
    public void Serialize_ThenParse_KeepsATrailingSpaceOnTheLastLiteralLine()
    {
        var node = new MeshNode("S02", "deck")
        {
            NodeType = "Slide",
            Name = "Two",
            Content = new SlideContent { Content = "# Two", Notes = "first\nsecond " }
        };
        var back = Parser.Parse("/root/deck/S02.md", Parser.Serialize(node), "deck/S02.md")!;
        Assert.Equal("first\nsecond ", Assert.IsType<SlideContent>(back.Content).Notes);
    }

    [Fact]
    public void Parse_BindsEitherCasing_AndTheLegacyAliases()
    {
        const string file = """
            ---
            nodeType: Edu/Lesson
            TITLE: From the alias
            description: From the other alias
            ORDER: "7"
            tags: [one, 2]
            ---

            Body
            """;
        var node = Parser.Parse("/r/Edu/L.md", file, "Edu/L.md")!;
        Assert.Equal("Edu/Lesson", node.NodeType);
        Assert.Equal("From the alias", node.Name);
        Assert.Equal("From the other alias", node.Description);
        Assert.Equal(7, node.Order); // a quoted number binds to an int
        Assert.Equal(["one", "2"], ContentOf(node)!.Tags!); // an unquoted number binds to a string
    }

    [Fact]
    public void Parse_AValueWithColonSpace_IsReadAsText_NotRescued()
    {
        const string file = """
            ---
            Name: Pipeline
            Description: new deal: fund reporting pilot, 45k
            Category: Crm
            ---

            Body
            """;
        var node = Parser.Parse("/r/Crm/P.md", file, "Crm/P.md")!;
        Assert.Equal("Pipeline", node.Name);
        Assert.Equal("Crm", node.Category);
        // The regex rescue never recovered Description; the tolerant reader reads it.
        Assert.Equal("new deal: fund reporting pilot, 45k", node.Description);
    }

    [Fact]
    public void Parse_UnreadableYaml_StillFallsBackToTheRescue()
    {
        const string file = """
            ---
            NodeType: Agent
            Name: [invalid yaml
            Category: Kept
            ---

            Body
            """;
        var node = Parser.Parse("/r/Pkg/x.md", file, "Pkg/x.md")!;
        // Unreadable (an unterminated flow sequence): the rescue recovers the single-line fields and
        // rejects the structured one, so the name falls back to the id.
        Assert.Equal("Agent", node.NodeType);
        Assert.Equal("Kept", node.Category);
        Assert.Equal("x", node.Name);
    }
}
