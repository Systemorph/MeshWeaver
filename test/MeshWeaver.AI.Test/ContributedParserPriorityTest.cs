using System.Linq;
using MeshWeaver.Hosting.Persistence.Parsers;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// <see cref="FileFormatParserRegistry"/>'s ORDERING contract: a contributed parser is tried before
/// the built-ins, so a specialised parser wins over <see cref="MarkdownFileParser"/>, which accepts
/// every <c>.md</c> file.
///
/// <para>🚨 This exists because the failure mode is SILENT. If a contributed parser is missing — not
/// registered, registered after the built-ins, or dropped by a construction site that forgot to pass
/// the contributed set — the catch-all still produces a node. No exception, no log line, nothing
/// red: the file is parsed into a plain Markdown node and whatever richer content it carried simply
/// ceases to exist. Ordering is therefore part of the contract, and pinned here rather than left to
/// registration accident.</para>
///
/// <para>The contributor here is a test-local <see cref="ProbeFileParser"/> rather than a real
/// module's parser, deliberately (#2276). This is a test of the REGISTRY, and it should hold for
/// every contributor — including ones that do not exist yet. Pinning it against one particular
/// module's parser both coupled core's test suite to that module and quietly narrowed the assertion
/// to "this one client works". The real-parser proof for the agent format lives beside the parser
/// itself, in <c>MeshWeaver.AI.Test.AgentParserContributionTest</c>.</para>
/// </summary>
public class ContributedParserPriorityTest
{
    private const string ProbeMarkdown = """
        ---
        nodeType: Probe
        name: ProbeNode
        ---

        Body text that a richer parser would keep and the catch-all would flatten.
        """;

    /// <summary>
    /// A minimal stand-in for any module-contributed parser: it claims <c>.md</c> and produces a
    /// node the catch-all provably cannot, which is what makes the two outcomes distinguishable.
    /// </summary>
    private sealed class ProbeFileParser : IFileFormatParser
    {
        public const string ProbeNodeType = "Probe";

        public IReadOnlyList<string> SupportedExtensions => [".md"];

        public MeshNode? Parse(string filePath, string content, string relativePath)
            => content.Contains("nodeType: Probe")
                ? new MeshNode(relativePath) { NodeType = ProbeNodeType, Content = new ProbeContent(content) }
                : null;

        public string Serialize(MeshNode node) => ((ProbeContent)node.Content!).Raw;

        public bool CanSerialize(MeshNode node) => node.NodeType == ProbeNodeType;
    }

    private sealed record ProbeContent(string Raw);

    private static MeshNode? Parse(FileFormatParserRegistry registry) =>
        registry.TryParse(".md", "/data/Probe/ProbeNode.md", ProbeMarkdown, "Probe/ProbeNode.md");

    [Fact]
    public void ContributedParser_WinsOverTheCatchAllMarkdownParser()
    {
        var registry = new FileFormatParserRegistry(contributedParsers: [new ProbeFileParser()]);

        var node = Parse(registry);

        node.Should().NotBeNull();
        node!.Content.Should().BeOfType<ProbeContent>(
            "a contributed parser is tried BEFORE MarkdownFileParser, which accepts every .md");
    }

    [Fact]
    public void WithoutTheContributedParser_TheSameFileDegradesSilently()
    {
        // The regression this guard is aimed at, stated as an executable fact — and it is WORSE than
        // "the file fails to parse". The built-in set produces a node (no throw, no null), and
        // MarkdownFileParser even copies `nodeType:` off the front matter, so the node still CLAIMS
        // its type. What it loses is its CONTENT.
        //
        // That is why the discriminator asserted here is the content type and not the nodeType: a
        // guard keyed on nodeType would have passed while the platform shipped hollow nodes.
        var registry = new FileFormatParserRegistry();

        var node = Parse(registry);

        node.Should().NotBeNull("MarkdownFileParser accepts every .md — that is the trap");
        node!.Content.Should().NotBeOfType<ProbeContent>(
            "without the contributed parser the richer content is silently lost");
    }

    [Fact]
    public void BuiltInParsersStillHandleTheirOwnFormats()
    {
        // Contributing a parser must not disturb the rest of the chain.
        var registry = new FileFormatParserRegistry(contributedParsers: [new ProbeFileParser()]);

        registry.SupportedExtensions.Should().Contain(".md").And.Contain(".cs");
        registry.GetParsers(".md").Should().HaveCountGreaterThan(1,
            "both the contributed parser and the Markdown fallback serve .md");
        registry.GetParsers(".md").First().Should().BeOfType<ProbeFileParser>(
            "priority order is the contract: contributed parsers first");
    }
}
