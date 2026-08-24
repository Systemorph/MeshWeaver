using System.Linq;
using MeshWeaver.AI;
using MeshWeaver.AI.Persistence;
using MeshWeaver.Hosting.Persistence.Parsers;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Content.Test;

/// <summary>
/// The agent file format left core hosting: <see cref="AgentFileParser"/> ships with
/// <c>MeshWeaver.AI</c> and reaches <see cref="FileFormatParserRegistry"/> as a CONTRIBUTED parser
/// (registered by <c>AddAI</c>, resolved wherever a registry is built from a service provider).
/// That is what let <c>MeshWeaver.Hosting.csproj</c> drop its <c>ProjectReference</c> to the AI
/// assembly.
///
/// <para>🚨 This test exists because the failure mode is SILENT. <see cref="MarkdownFileParser"/>
/// accepts every <c>.md</c> file, so if the agent parser is missing — not registered, registered
/// after the built-ins, or dropped by a construction site that forgot to pass the contributed set —
/// an <c>.md</c> carrying <c>nodeType: Agent</c> is parsed into a plain Markdown node. No
/// exception, no log line, nothing red: the agent simply ceases to exist, and the first symptom is
/// a user being told "Selected agent 'X' was not found" in production. Ordering is therefore part
/// of the contract, and pinned here rather than left to registration accident.</para>
/// </summary>
public class ContributedParserPriorityTest
{
    private const string AgentMarkdown = """
        ---
        nodeType: Agent
        name: ProbeAgent
        description: A probe agent used to pin parser priority.
        ---

        You are ProbeAgent. These are the instructions.
        """;

    private static MeshNode? Parse(FileFormatParserRegistry registry) =>
        registry.TryParse(".md", "/data/Agent/ProbeAgent.md", AgentMarkdown, "Agent/ProbeAgent.md");

    [Fact]
    public void ContributedAgentParser_WinsOverTheCatchAllMarkdownParser()
    {
        var registry = new FileFormatParserRegistry(
            contributedParsers: [new AgentFileParser()]);

        var node = Parse(registry);

        node.Should().NotBeNull();
        node!.NodeType.Should().Be("Agent",
            "a contributed parser is tried BEFORE MarkdownFileParser, which accepts every .md");
        node.Content.Should().BeOfType<AgentConfiguration>(
            "the agent's front matter and body must survive as agent configuration");
    }

    [Fact]
    public void WithoutTheContributedParser_TheSameFileDegradesSilently()
    {
        // The regression this guard is aimed at, stated as an executable fact — and it is WORSE
        // than "the file fails to parse". The built-in set produces a node (no throw, no null),
        // and MarkdownFileParser even copies `nodeType: Agent` off the front matter, so the node
        // still CLAIMS to be an agent. What it loses is its content: no AgentConfiguration, so no
        // instructions, no delegations, no plugins. An agent-shaped node that cannot act.
        //
        // That is why the discriminator asserted here is the CONTENT TYPE and not the nodeType:
        // a guard keyed on nodeType would have passed while the platform shipped hollow agents.
        var registry = new FileFormatParserRegistry();

        var node = Parse(registry);

        node.Should().NotBeNull("MarkdownFileParser accepts every .md — that is the trap");
        node!.Content.Should().NotBeOfType<AgentConfiguration>(
            "without the AI module's parser the agent's configuration is silently lost");
    }

    [Fact]
    public void BuiltInParsersStillHandleTheirOwnFormats()
    {
        // Removing the hard-coded agent parser must not disturb the rest of the chain.
        var registry = new FileFormatParserRegistry(contributedParsers: [new AgentFileParser()]);

        registry.SupportedExtensions.Should().Contain(".md").And.Contain(".cs");
        registry.GetParsers(".md").Should().HaveCountGreaterThan(1,
            "both the contributed agent parser and the Markdown fallback serve .md");
        registry.GetParsers(".md").First().Should().BeOfType<AgentFileParser>(
            "priority order is the contract: contributed parsers first");
    }
}
