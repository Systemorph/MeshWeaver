#pragma warning disable CS1591

using System.IO;
using System.Linq;
using MeshWeaver.AI;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Guards the built-in AI content files against the one mistake that is both easy to make and
/// catastrophic: <b>malformed YAML front matter</b>.
///
/// <para>These nodes load during mesh startup, so a single bad file used to propagate a
/// <c>YamlException</c> straight out of <see cref="BuiltInAgentProvider.GetStaticNodes"/> and take
/// the host with it. On 2026-08-07 an unquoted <c>:</c> in one agent's description did exactly that
/// — every full-mesh suite (Orleans, Monolith, Query) went red at once, reported as ~400 unrelated
/// test failures with nothing naming the actual file. The two tests here make that impossible to
/// repeat: the first fails on the offending file BY NAME, the second proves a bad file can no
/// longer crash the load.</para>
/// </summary>
public class BuiltInAgentContentTest
{
    [Fact]
    public void EveryShippedContentFile_LoadsAsANode()
    {
        var root = AiContentLocator.SectionRoot();
        Assert.NotNull(root);
        var agentDir = Path.Combine(root!, "Agent");
        Assert.True(Directory.Exists(agentDir), $"agent section not found: {agentDir}");

        var files = Directory.EnumerateFiles(agentDir, "*.md").OrderBy(f => f, StringComparer.Ordinal).ToList();
        Assert.NotEmpty(files);

        var nodes = BuiltInAgentProvider.LoadAllNodes()
            .ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);

        // NOT every file here is an agent — the section also ships supporting documentation
        // (nodeType Markdown, e.g. ToolsReference). The invariant is that every file LOADS;
        // whether it is an Agent or a doc is the file's own declaration, asserted below.
        var missing = files
            .Select(Path.GetFileNameWithoutExtension)
            .Where(id => id is not null && !nodes.ContainsKey(id!))
            .ToList();

        // Naming the file is the point — this is the message that was missing in the incident.
        Assert.True(missing.Count == 0,
            "These content files did not load — almost always invalid YAML front matter "
            + "(an unquoted ':' inside a value; quote the value): " + string.Join(", ", missing));
    }

    [Fact]
    public void EveryFileDeclaringNodeTypeAgent_LoadsAsAnAgentNode()
    {
        var root = AiContentLocator.SectionRoot();
        var agentDir = Path.Combine(root!, "Agent");
        var nodes = BuiltInAgentProvider.LoadAllNodes()
            .ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);

        // A file that SAYS it is an agent but silently loads as a plain markdown node is the exact
        // shape of the 2026-08-07 breakage: IsAgentFrontmatter swallowed the YAML error and answered
        // "not an agent", so the file degraded instead of failing.
        var degraded = Directory.EnumerateFiles(agentDir, "*.md")
            .Where(f => File.ReadLines(f).Take(15)
                .Any(l => l.StartsWith("nodeType:", StringComparison.OrdinalIgnoreCase)
                          && l.Contains("Agent", StringComparison.OrdinalIgnoreCase)))
            .Select(Path.GetFileNameWithoutExtension)
            .Where(id => id is not null
                         && (!nodes.TryGetValue(id!, out var node)
                             || !string.Equals(node.NodeType, "Agent", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.True(degraded.Count == 0,
            "These files declare 'nodeType: Agent' but did NOT load as Agent nodes — their front "
            + "matter is invalid YAML (an unquoted ':' inside a value; quote the value): "
            + string.Join(", ", degraded));
    }

    [Fact]
    public void EveryShippedAgentNode_HasTheMetadataThePickerNeeds()
    {
        var agents = BuiltInAgentProvider.LoadAllNodes()
            .Where(n => string.Equals(n.NodeType, "Agent", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.NotEmpty(agents);

        // A silently-degraded parse yields a node whose Name falls back to the id and whose content
        // is not an AgentConfiguration — which shows up as an empty/incoherent picker entry rather
        // than an error. Assert the shape instead of trusting "it loaded".
        foreach (var agent in agents)
        {
            Assert.False(string.IsNullOrWhiteSpace(agent.Name), $"{agent.Id}: no Name");
            Assert.IsType<AgentConfiguration>(agent.Content);
            var config = (AgentConfiguration)agent.Content!;
            Assert.False(string.IsNullOrWhiteSpace(config.Instructions),
                $"{agent.Id}: no Instructions (the markdown body IS the instructions)");
        }
    }

    /// <summary>
    /// A file with malformed front matter must be <b>skipped</b>, not thrown on — one bad file cannot be
    /// allowed to stop the host from starting — and it must be <b>reported</b>.
    ///
    /// <para>🚨 Asserting only <c>Assert.Null(node)</c> is exactly the blind spot this whole area is
    /// about: null is what a loudly-skipped file and a silently-vanished one both return, so the
    /// assertion cannot tell them apart. Silent is the bad one — the catalog shrinks with nothing red
    /// anywhere and the author sees only "my agent doesn't appear". Assert the diagnostic.</para>
    /// </summary>
    [Fact]
    public void MalformedFrontMatter_IsSkipped_AndReported_AndDoesNotThrow()
    {
        const string malformed =
            "---\n" +
            "nodeType: Agent\n" +
            "name: Broken\n" +
            "description: has an unquoted colon: right here which is invalid YAML\n" +
            "---\n\n" +
            "Body.\n";

        // Must not throw. Before the guard this threw YamlException straight out of mesh startup.
        var node = BuiltInAgentProvider.TryParseEmbeddedNode(malformed, "Agent/broken.md", out var error);

        Assert.Null(node);
        Assert.False(string.IsNullOrWhiteSpace(error),
            "a skipped file must say WHY — that reason is the only trace a dropped agent leaves");
        Assert.Contains("YAML", error!, StringComparison.OrdinalIgnoreCase);
    }
}
