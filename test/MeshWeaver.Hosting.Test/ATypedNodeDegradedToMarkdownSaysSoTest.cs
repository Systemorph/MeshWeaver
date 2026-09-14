using System.Text.Json;
using MeshWeaver.Hosting.Persistence.Parsers;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 Pins Systemorph/MeshWeaver#4319: <b>a typed node that degrades to Markdown must SAY SO.</b>
///
/// <para><see cref="MarkdownFileParser"/> is the fallback for <c>.md</c> and accepts every file —
/// so it is what a TYPED node falls back to whenever the parser that owns its declared
/// <c>nodeType</c> does not produce a node, whether because it is not registered on this host or
/// because it REFUSED the file. The node then lands with the right <c>NodeType</c>, the right name
/// and the instructions body, and <see cref="MarkdownContent"/> where its typed configuration should
/// be. Every key that type declared is discarded. Nothing throws, nothing logs, and the node looks
/// complete in a listing.</para>
///
/// <para>The live case is <c>Crm/Agent/crm-assistant</c> on <c>memex.meshweaver.cloud</c>: measured
/// 2026-09-14, <c>content.$type</c> is <c>MarkdownContent</c> and the node has no description,
/// while the source file at <c>Systemorph/MeshWeaver.Crm</c> authors <c>displayName</c>,
/// <c>description</c>, <c>exposedInNavigator</c>, <c>contextMatchPattern</c> and <c>plugins</c>.
/// It served that way for twelve days and was found by a human noticing a blank description in the
/// agent dropdown — there was no other instrument, because the READ-side one
/// (<see cref="ContentDegradationRegistry"/>, <c>/health</c> → <c>content-types</c>) is
/// structurally blind here: the content's <c>$type</c> is <c>MarkdownContent</c>, which resolves
/// perfectly. The loss happened at IMPORT.</para>
///
/// <para>🚨 <b>And it is that file's own YAML that does it</b> — not a missing parser. Its
/// <c>description:</c> value contains <c>PG3: fund reporting pilot</c>, a <c>": "</c> inside a plain
/// scalar, which YAML does not permit;
/// <see cref="TheLiveFilesYamlIsRefused_AndTheRescueDoesNotRecoverDescription"/> reproduces the
/// refusal locally. Its sibling <c>Crm/Skill/crm</c> — read the same day — is degraded IDENTICALLY
/// (<c>MarkdownContent</c>, no description, name/category/icon/order intact) and its file carries
/// the same malformed value, so "the type's parser was absent" is not needed to explain either one
/// and does not explain both.</para>
///
/// <para>These tests are ordered defect → live mechanism → gate → positive control. The first fails
/// on unmodified <c>main</c>; the third and fourth would both flag if the detector were left
/// ungated or fired unconditionally.</para>
/// </summary>
public class ATypedNodeDegradedToMarkdownSaysSoTest
{
    /// <summary>
    /// The front matter of <c>Crm/Agent/crm-assistant.md</c> as
    /// <c>Systemorph/MeshWeaver.Crm@main</c> authors it (read 2026-09-14), trimmed of the icon SVG
    /// and the instructions body. Reproduced verbatim rather than paraphrased so the test fails for
    /// the same reason production did.
    /// </summary>
    private const string CrmAgentFile = """
        ---
        nodeType: Agent
        name: CrmAssistant
        displayName: CRM Assistant
        description: Keeps the client pipeline honest from a conversation — "Howden confirmed the workshop for 14 Sept", "new deal at PG3: fund reporting pilot, 45k", "Thomas asked not to be emailed". Reads the client, the deal and the board, makes exactly the record change the process names, and answers pipeline questions from the records rather than from memory.
        icon: <svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='currentColor'/></svg>
        category: Crm
        exposedInNavigator: true
        contextMatchPattern: address.nodeType=like=Crm/*
        plugins:
          - Mesh
        ---

        You are the **CRM Assistant**.
        """;

    /// <summary>The built-in parser set — no contributed parser, i.e. a host where the module that
    /// owns the declared type is not loaded. That is the whole condition under test.</summary>
    private static FileFormatParserRegistry BuiltIns() => new(new JsonSerializerOptions());

    private static MarkdownContent? ContentOf(MeshNode? node) =>
        node.ContentAs<MarkdownContent>(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    /// <summary>
    /// THE DEFECT. The fallback claims a file that declares <c>nodeType: Agent</c>, and the four
    /// keys carrying the agent's configuration go nowhere. The degradation itself is pre-existing
    /// and unchanged — asserted here so the record is read against the state it describes — and
    /// the <see cref="MarkdownContent.UnboundFrontMatter"/> assertion is the one that fails on
    /// unmodified <c>main</c>, where the property does not exist.
    /// </summary>
    [Fact]
    public void AgentFileWithNoAgentParser_NamesEveryKeyItDiscarded()
    {
        var node = BuiltIns().TryParse(".md", "Crm/Agent/crm-assistant.md", CrmAgentFile, "Crm/Agent/crm-assistant.md");

        Assert.NotNull(node);
        // The degradation, as the live node shows it: the declared type survives on the NODE, the
        // typed content does not.
        Assert.Equal("Agent", node!.NodeType);
        var content = ContentOf(node);
        Assert.NotNull(content);

        // The keys MarkdownFrontMatter binds still arrive — which is exactly why the node looks
        // complete in a listing and why nobody saw this for twelve days.
        Assert.Equal("CrmAssistant", node.Name);
        Assert.Equal("Crm", node.Category);

        // 🚨 THE FIX. The four keys that carried the AgentConfiguration are named on the node.
        Assert.NotNull(content!.UnboundFrontMatter);
        Assert.Equal(
            new[] { "displayName", "exposedInNavigator", "contextMatchPattern", "plugins" },
            content.UnboundFrontMatter!);
    }

    /// <summary>
    /// 🚨 THE SECOND HALF OF THE LIVE CASE, measured rather than assumed: this file's YAML is
    /// REFUSED by the deserializer, and the defensive extractor that rescues the parse does not
    /// rescue <c>description</c>.
    ///
    /// <para>The `description:` value contains <c>PG3: fund reporting pilot</c> — a <c>": "</c>
    /// inside a plain scalar, which YAML does not permit. <c>Parse</c> catches that and falls back
    /// to its regex recovery, which recovers <c>NodeType</c>, <c>Name</c>/<c>Title</c>,
    /// <c>Category</c>, <c>Icon</c>/<c>Thumbnail</c>, <c>State</c> and <c>Order</c> — and NOT
    /// <c>Description</c>/<c>Abstract</c>. That is why the live node shows the right name, icon and
    /// category with a blank description: not one missing parser, but a REFUSED front matter whose
    /// rescue is partial.</para>
    ///
    /// <para>Measured on the live mesh the same day, BOTH of that space's typed markdown nodes show
    /// exactly this signature — <c>Crm/Agent/crm-assistant</c> and <c>Crm/Skill/crm</c> are each
    /// <c>MarkdownContent</c> with no description and with name, category, icon and order intact.
    /// Only <see cref="MarkdownFileParser"/> has a regex rescue, and it only runs when the
    /// deserializer failed, so that signature IS the fingerprint of this path.</para>
    ///
    /// <para>This test pins what IS, not what should be. If the recovery list is ever extended to
    /// <c>Description</c>, this test tells the author exactly which behaviour they changed.</para>
    /// </summary>
    [Fact]
    public void TheLiveFilesYamlIsRefused_AndTheRescueDoesNotRecoverDescription()
    {
        var node = BuiltIns().TryParse(".md", "Crm/Agent/crm-assistant.md", CrmAgentFile, "Crm/Agent/crm-assistant.md");

        Assert.NotNull(node);
        // Recovered by the defensive extractor — which is why the node looks right in a listing.
        Assert.Equal("Agent", node!.NodeType);
        Assert.Equal("CrmAssistant", node.Name);
        Assert.Equal("Crm", node.Category);
        // NOT recovered: Description/Abstract is absent from the extractor's field list.
        Assert.Null(node.Description);
        Assert.Null(ContentOf(node)!.Abstract);
    }

    /// <summary>
    /// THE GATE, proven to bite. An ordinary documentation page carries an unbound key
    /// (<c>Published</c> — 20 of this repo's <c>samples/Graph/Data</c> pages do) and declares NO
    /// <c>nodeType</c>. It has no type-specific configuration to lose, so it must record nothing.
    ///
    /// <para>This is not a control that cannot fail: the file's front matter holds a key the
    /// parser does not bind, so a detector without the <c>nodeType</c> gate flags it. Removing the
    /// gate in <c>UnboundFrontMatterKeys</c> turns this test red.</para>
    /// </summary>
    [Fact]
    public void UntypedPageWithAStrayKey_RecordsNothing()
    {
        const string page = """
            ---
            Name: Architecture
            Published: 2026-01-01
            ---

            # Architecture
            """;

        var node = BuiltIns().TryParse(".md", "Northwind/Documentation/Architecture.md", page, "Northwind/Documentation/Architecture.md");

        Assert.NotNull(node);
        Assert.Null(ContentOf(node)!.UnboundFrontMatter);
    }

    /// <summary>
    /// 🚨 THE DETECTOR MUST NOT SEE LESS THAN THE LOSS (Copilot review on #4330). The keys are read
    /// with the SAME deserializer that discarded them, not with a key grammar of this parser's own —
    /// so a QUOTED key, a DOTTED key and a non-ASCII one are all named.
    ///
    /// <para>Measured by reverting the reader to the regex alone and watching this test fail: it
    /// then produced <c>["ré-sumé", "plugins"]</c> — the QUOTED key and the DOTTED key silently
    /// dropped, so the record would have named two of the four keys the node actually lost. A
    /// record that under-reports is worse than one that admits it knows nothing.</para>
    ///
    /// <para>🚨 The non-ASCII key survived the regex only because .NET's <c>\w</c> is
    /// Unicode-aware — coverage by accident, not by design, which is exactly why the reader may not
    /// be a hand-written grammar. The set of spellings YamlDotNet accepts is YamlDotNet's to
    /// define.</para>
    ///
    /// <para>This YAML is VALID, which is the point: it takes the dictionary path — the one the
    /// live case, whose YAML is refused, never exercises.</para>
    /// </summary>
    [Fact]
    public void KeysTheNarrowGrammarWouldMiss_AreStillNamed()
    {
        const string file = """
            ---
            nodeType: Agent
            name: Oddly keyed
            "displayName": Quoted key
            my.dotted.key: dotted
            ré-sumé: non-ascii
            plugins:
              - Mesh
            ---

            Body.
            """;

        var node = BuiltIns().TryParse(".md", "Pkg/Agent/odd.md", file, "Pkg/Agent/odd.md");

        Assert.NotNull(node);
        var unbound = ContentOf(node)!.UnboundFrontMatter;
        Assert.NotNull(unbound);
        Assert.Equal(
            new[] { "displayName", "my.dotted.key", "ré-sumé", "plugins" },
            unbound!);
    }

    /// <summary>
    /// The second half of the gate: a file that DOES declare a type but whose every key this parser
    /// binds is not a degradation either. Without this, "declared a nodeType" alone would be enough
    /// to stamp the record, and it would say nothing about whether anything was lost.
    /// </summary>
    [Fact]
    public void TypedFileWhoseKeysAllBind_RecordsNothing()
    {
        const string lesson = """
            ---
            nodeType: Edu/Lesson
            name: Why cubes
            category: DataModeling
            order: 3
            ---

            # Why cubes
            """;

        var node = BuiltIns().TryParse(".md", "Edu/WhyCubes.md", lesson, "Edu/WhyCubes.md");

        Assert.NotNull(node);
        Assert.Equal("Edu/Lesson", node!.NodeType);
        Assert.Null(ContentOf(node)!.UnboundFrontMatter);
    }

    /// <summary>
    /// THE POSITIVE CONTROL for what the record MEANS. Put a parser for the declared type ahead of
    /// the fallback — the shape <see cref="FileFormatParserRegistry"/>'s priority order exists to
    /// guarantee — and the same bytes produce a typed node with no record at all. So the record
    /// tracks "the fallback won", not "this file has extra keys", and it disappears when the module
    /// that owns the type is present.
    /// </summary>
    [Fact]
    public void WithTheTypesOwnParserRegistered_TheFallbackNeverRuns()
    {
        var registry = new FileFormatParserRegistry(
            new JsonSerializerOptions(), [new StubAgentParser()]);

        var node = registry.TryParse(".md", "Crm/Agent/crm-assistant.md", CrmAgentFile, "Crm/Agent/crm-assistant.md");

        Assert.NotNull(node);
        // Typed content, so there is nothing for a Markdown-shaped record to be attached to.
        Assert.IsType<StubAgentConfiguration>(node!.Content);
        Assert.Null(ContentOf(node));
    }

    /// <summary>The shape of a contributed parser: claims <c>.md</c>, recognises ONE front matter,
    /// and declines everything else so the fallback still handles ordinary pages. Modelled on the
    /// AI module's agent parser, which lives in MeshWeaver.Plugins and cannot be referenced here.
    /// </summary>
    private sealed class StubAgentParser : IFileFormatParser
    {
        public IReadOnlyList<string> SupportedExtensions => [".md"];

        public MeshNode? Parse(string filePath, string content, string relativePath) =>
            content.Contains("nodeType: Agent", StringComparison.Ordinal)
                ? new MeshNode("crm-assistant", "Crm/Agent")
                {
                    NodeType = "Agent",
                    Content = new StubAgentConfiguration { DisplayName = "CRM Assistant" }
                }
                : null;

        public string Serialize(MeshNode node) => string.Empty;

        public bool CanSerialize(MeshNode node) => node.Content is StubAgentConfiguration;
    }

    /// <summary>The typed content the declared node type should have produced.</summary>
    private sealed record StubAgentConfiguration
    {
        public string? DisplayName { get; init; }
    }
}
