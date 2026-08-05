using System.Linq;
using MeshWeaver.AI;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Documentation;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Threading.Test;

/// <summary>
/// Pins what <see cref="BuiltInAgentProvider"/> still owns after the built-in agents moved OUT of the
/// binary and into the <c>Agent</c> plugin (<c>MeshWeaver.Plugins</c>, pre-installed).
///
/// <para>This file used to assert the shipped catalog — Assistant, Worker, Researcher,
/// ToolsReference — and scan every agent's markdown for literal <c>@path</c> placeholders and
/// unresolvable <c>@@</c> references. Those assertions moved to the plugins repo's gate
/// (<c>scripts/validate-repos.py</c>), which is where the markdown now lives; asserting them here
/// would only prove the framework had NOT let go of the content.</para>
///
/// <para>What remains here is the half the framework kept, and both halves matter:</para>
/// <list type="bullet">
///   <item><b>ThreadNamer survives.</b> It is the one agent with no <c>.md</c> file — built in C#
///     because the framework itself invokes it to name threads. It was the thing most likely to be
///     lost in the move, so it is pinned.</item>
///   <item><b>Nothing else ships.</b> If an agent <c>.md</c> is ever re-added under
///     <c>content/ai/Agent</c>, it would be embedded and served in-memory while the plugin serves
///     the same paths from Postgres — two sources for one partition, silently disagreeing. This
///     test fails the moment that happens.</item>
/// </list>
/// </summary>
public class AgentMarkdownParsingTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddAI()
            .AddDocumentation();

    /// <summary>
    /// The in-memory offline path still yields the code-defined ThreadNamer and the partition's
    /// read-only access policy — the agent picker's fallback on a mesh that does not serve Agent
    /// from the DB (monolith, tests, MAUI).
    /// </summary>
    [Fact]
    public void TheProviderStillYieldsThreadNamerAndTheAccessPolicy()
    {
        var nodes = new BuiltInAgentProvider().GetStaticNodes().ToList();

        foreach (var node in nodes)
            Output.WriteLine($"  {node.Path}: {node.NodeType} - {node.Name}");

        nodes.Should().Contain(
            n => n.Path == "Agent/ThreadNamer",
            "ThreadNamer is defined in C#, not as markdown — the framework invokes it to name "
            + "threads, so it must survive the agents' move to the plugin");
        nodes.Should().Contain(
            n => n.NodeType == "PartitionAccessPolicy",
            "the Agent partition's PublicRead policy is what makes the catalog readable at all");
    }

    /// <summary>
    /// 🚨 No agent MARKDOWN ships in the binary any more. A re-added <c>content/ai/Agent/*.md</c>
    /// would be embedded and served in-memory while the Agent plugin serves the same paths from
    /// Postgres — the two-sources-for-one-partition split this move exists to end.
    /// </summary>
    [Fact]
    public void NoAgentMarkdownIsEmbeddedInTheBinaryAnyMore()
    {
        var markdownAgents = new BuiltInAgentProvider().GetStaticNodes()
            .Where(n => n.Content is MeshWeaver.Markdown.MarkdownContent
                        || (n.NodeType == "Agent" && n.Path != "Agent/ThreadNamer"))
            .Select(n => n.Path)
            .ToList();

        markdownAgents.Should().BeEmpty(
            "the built-in agents live in the Agent plugin (MeshWeaver.Plugins) now. Shipping one "
            + "here too gives the Agent partition two disagreeing sources — add it to the plugin "
            + "instead");
    }
}
