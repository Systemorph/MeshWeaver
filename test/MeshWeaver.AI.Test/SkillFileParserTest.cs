using System.Linq;
using MeshWeaver.AI;
using MeshWeaver.AI.Persistence;
using MeshWeaver.Hosting.Persistence.Parsers;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// #1984, the SECOND half. The first half made a <c>nodeType: Skill</c> markdown file TYPE correctly
/// — the catch-all <see cref="MarkdownFileParser"/> now binds front-matter keys case-insensitively,
/// so the node stopped defaulting to <c>Markdown</c>. That alone left the skill EMPTY: the node was
/// typed <c>Skill</c> but carried <see cref="MarkdownContent"/>, and <c>SkillNodeType</c> reads its
/// content as <see cref="SkillDefinition"/> — which <c>ContentAs&lt;T&gt;</c> recovers only from a
/// same-short-named type. Every skill's <see cref="SkillDefinition.Instructions"/> read null.
///
/// <para>🚨 That intermediate state was WORSE than the bug it replaced, which is why the assertion
/// this class leads with is on the CONTENT and not the node type. Before the casing fix a broken
/// skill was visible in a node listing as a Markdown page. After it, the node claims to be a Skill,
/// appears in the slash-command list, and does nothing — there is no listing, no log line, and no
/// exception that distinguishes it from a working one. A guard keyed on <c>NodeType</c> would have
/// passed the whole time.</para>
/// </summary>
public class SkillFileParserTest
{
    private const string Body =
        "Use this skill to probe the parser chain.\n\n1. Read the file.\n2. Assert the body survived.";

    private static string SkillMarkdown(string nodeTypeKey = "nodeType") => $"""
        ---
        {nodeTypeKey}: Skill
        name: /probe
        description: A probe skill used to pin the parser chain.
        icon: Beaker
        ---

        {Body}
        """;

    private static FileFormatParserRegistry WithSkillParser() =>
        new(contributedParsers: [new AgentFileParser(), new SkillFileParser()]);

    private static MeshNode? Parse(FileFormatParserRegistry registry, string content) =>
        registry.TryParse(".md", "/data/Hosting/Skill/probe.md", content, "Hosting/Skill/probe.md");

    [Fact]
    public void SkillMarkdown_BecomesASkillDefinition_WithTheBodyAsInstructions()
    {
        var node = Parse(WithSkillParser(), SkillMarkdown());

        node.Should().NotBeNull();
        node!.NodeType.Should().Be(SkillNodeType.NodeType);

        // THE assertion of this issue: the skill reads NON-EMPTY. Read it the way SkillNodeType
        // does (ContentAs), not with a cast — a cast would pass for reasons unrelated to the fix.
        var definition = node.ContentAs<SkillDefinition>(new System.Text.Json.JsonSerializerOptions(
            System.Text.Json.JsonSerializerDefaults.Web));
        definition.Should().NotBeNull();
        definition!.Instructions.Should().NotBeNullOrWhiteSpace(
            "the markdown body IS the skill's procedure — a null here is the silent empty skill");
        definition.Instructions.Should().Contain("Assert the body survived");
    }

    [Fact]
    public void WithoutTheContributedParser_TheSkillIsTypedButEMPTY()
    {
        // The regression stated as an executable fact, and the reason this test class exists. The
        // built-in chain produces a node (no throw, no null) that even carries the right NodeType —
        // and whose content is MarkdownContent, so every read of the skill's procedure is null.
        var node = Parse(new FileFormatParserRegistry(), SkillMarkdown());

        node.Should().NotBeNull("MarkdownFileParser accepts every .md — that is the trap");
        node!.NodeType.Should().Be(SkillNodeType.NodeType,
            "the front-matter casing fix already lands the node type — which is exactly why "
            + "asserting on NodeType proves nothing about whether the skill works");
        node.ContentAs<SkillDefinition>(new System.Text.Json.JsonSerializerOptions(
                System.Text.Json.JsonSerializerDefaults.Web))
            ?.Instructions
            .Should().BeNull("without the skill parser the body lands in MarkdownContent instead");
    }

    [Theory]
    [InlineData("nodeType")]
    [InlineData("NodeType")]
    public void BothFrontMatterCasings_ProduceTheSameSkill(string nodeTypeKey)
    {
        // Both spellings are LIVE, not hypothetical. Authors write `nodeType:`; MarkdownFileParser's
        // own Serialize (which writes any Skill node it still owns) emits `NodeType:` — so a reader
        // that binds only one of them refuses the file it just wrote, and the skill degrades a
        // second time. This is the same both-casings pin #1984 asked for on the Markdown parser.
        var node = Parse(WithSkillParser(), SkillMarkdown(nodeTypeKey));

        node.Should().NotBeNull();
        node!.NodeType.Should().Be(SkillNodeType.NodeType);
        node.Content.Should().BeOfType<SkillDefinition>();
        ((SkillDefinition)node.Content!).Instructions.Should().Contain("Assert the body survived");
    }

    [Fact]
    public void FrontMatterMetadataAndPathDerivedIdentity_Survive()
    {
        var node = Parse(WithSkillParser(), SkillMarkdown());

        node.Should().NotBeNull();
        node!.Id.Should().Be("probe", "the file name is the slash word");
        node.Namespace.Should().Be("Hosting/Skill",
            "a plugin ships its skills in its OWN partition — SkillMarkdown's platform-Skill "
            + "default must not survive the file-parser path");
        node.Name.Should().Be("/probe");
        node.Description.Should().Be("A probe skill used to pin the parser chain.");
        node.Icon.Should().Be("Beaker");
    }

    [Fact]
    public void APlainMarkdownPage_IsNotClaimed()
    {
        // The parser sits in the shared .md chain, so declining is as load-bearing as claiming:
        // front matter alone must never make a page a Skill.
        const string page = """
            ---
            name: Release notes
            description: Not a skill.
            ---

            # Release notes
            """;

        var node = Parse(WithSkillParser(), page);

        node.Should().NotBeNull();
        node!.NodeType.Should().Be("Markdown");
        node.Content.Should().BeOfType<MarkdownContent>();
    }

    [Fact]
    public void MalformedFrontMatter_FallsThroughInsteadOfThrowing()
    {
        // These files are parsed during mesh startup: an uncaught throw takes the host down (the
        // 2026-08-07 incident). An unquoted ':' inside a value is the usual author mistake.
        const string broken = """
            ---
            nodeType: Skill
            description: Broken: an unquoted colon
              and a dangling continuation
            ---

            body
            """;

        var registry = WithSkillParser();
        MeshNode? parsed = null;
        Action act = () =>
            parsed = registry.TryParse(".md", "/data/Skill/broken.md", broken, "Skill/broken.md");

        act.Should().NotThrow();
        parsed.Should().NotBeNull("the catch-all Markdown parser still renders the page");
    }

    [Fact]
    public void PriorityOrder_PutsBothContributedParsersAheadOfTheCatchAll()
    {
        var parsers = WithSkillParser().GetParsers(".md").ToList();

        parsers.Should().HaveCountGreaterThan(2);
        parsers.IndexOf(parsers.OfType<SkillFileParser>().Single())
            .Should().BeLessThan(parsers.IndexOf(parsers.OfType<MarkdownFileParser>().Single()),
                "priority order is the contract: a parser that recognises a specific front matter "
                + "must be tried before the one that accepts every .md");
    }

    [Fact]
    public void SkillNode_RoundTripsBackToItsFile()
    {
        var registry = WithSkillParser();
        var node = Parse(registry, SkillMarkdown())!;

        var serializer = registry.GetSerializerFor(node);
        serializer.Should().BeOfType<SkillFileParser>(
            "the skill parser must win the WRITE side too, or a sync-back rewrites the skill as a "
            + "plain markdown page and drops its action/harness/autoMount frontmatter");

        var reparsed = Parse(registry, serializer!.Serialize(node));
        reparsed!.NodeType.Should().Be(SkillNodeType.NodeType);
        ((SkillDefinition)reparsed.Content!).Instructions.Should().Contain("Assert the body survived");
    }

    [Fact]
    public void ASkillNodeStillCarryingMarkdownContent_IsRefusedByTheWriteSide()
    {
        // The un-retyped node — a Skill node whose content the old chain filled with MarkdownContent.
        // SkillDefinition has no required members, so deserializing that payload into it SUCCEEDS and
        // yields an all-null definition; claiming the write would emit a skill file with front matter
        // and NO BODY, clobbering the source. Declining hands it to the Markdown parser, which is
        // exactly what happens today — no regression, and no data loss.
        var stale = new MeshNode("probe", "Hosting/Skill")
        {
            NodeType = SkillNodeType.NodeType,
            Content = new MarkdownContent { Content = Body },
        };

        new SkillFileParser().CanSerialize(stale).Should().BeFalse();
        WithSkillParser().GetSerializerFor(stale).Should().BeOfType<MarkdownFileParser>();
    }

    /// <summary>
    /// Content this parser cannot interpret must DECLINE, never throw.
    /// <see cref="FileFormatParserRegistry.GetSerializerFor"/> calls <c>CanSerialize</c> with no
    /// catch around it, so a throw would take down a sync-back for the whole node rather than
    /// handing the file to the next parser — a strictly worse outcome than the one this parser
    /// exists to prevent. The shapes below are the ones that reach it from a real mesh: a
    /// discriminator that is not a string at all (Copilot review, #2284), and no discriminator.
    /// </summary>
    [Theory]
    [InlineData("""{"$type": 42, "instructions": "x"}""")]
    [InlineData("""{"$type": {"nested": true}}""")]
    [InlineData("""{"$type": null}""")]
    [InlineData("""{"instructions": "no discriminator at all"}""")]
    public void AnUninterpretableContentPayload_Declines_ItNeverThrows(string json)
    {
        foreach (object content in new object[]
                 {
                     System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json),
                     System.Text.Json.Nodes.JsonNode.Parse(json)!,
                 })
        {
            var node = new MeshNode("probe", "Hosting/Skill")
            {
                NodeType = SkillNodeType.NodeType,
                Content = content,
            };

            Action probe = () => new SkillFileParser().CanSerialize(node);

            probe.Should().NotThrow(
                $"a {content.GetType().Name} whose $type cannot be read as a string must decline");
            new SkillFileParser().CanSerialize(node).Should().BeFalse();
        }
    }
}
