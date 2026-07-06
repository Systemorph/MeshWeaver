using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MeshWeaver.AI;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Pins that every LIVE layout-area embed (<c>@@…</c> at the start of a line — the
/// <see cref="MeshWeaver.Markdown.LayoutAreaMarkdownParser"/> is a BLOCK parser, so only a
/// line-start <c>@@</c> renders; an <c>@@</c> inside an SVG <c>&lt;text&gt;</c> or an HTML
/// <c>&lt;code&gt;</c> is not an embed) in the embedded documentation targets a node in a partition
/// that SHIPS WITH THE DOCS.
///
/// <para>The docs are served on their own (the <c>Doc</c> partition, plus <c>Agent</c>/<c>Skill</c>);
/// on a real portal the sample partitions (<c>PythonDemo</c>, <c>Northwind</c>, <c>ACME</c>, …) are
/// NOT loaded. A doc page that <c>@@</c>-embeds a node from a non-shipped partition renders the ugly
/// "No renderer is registered for area <c>{firstSegment}</c> on hub <c>{docHub}</c>" — the exact
/// failure on <c>Doc/Architecture/PythonCodeNodes</c> (the raw path <c>@@PythonDemo/SampleStatistics</c>
/// did not resolve, so the client fell back to treating <c>PythonDemo</c> as an area on the doc's own
/// hub). Example nodes must therefore ship IN the Doc partition (as <c>Cession</c> / <c>SocialMedia</c>
/// do), not be embedded from a sample partition.</para>
///
/// <para>A shippable partition is <c>Doc</c>/<c>Agent</c>/<c>Skill</c>, the first segment of any node
/// shipped in that data (json namespaces), or the id of a <c>NodeType</c> node shipped there (a
/// NodeType's instances address under a partition named after it — e.g. <c>@@Cession/MotorXL</c>).
/// Keyword self-references (<c>@@data/…</c>, <c>@@content/…</c>, <c>@@schema/…</c>, <c>@@area/…</c>,
/// <c>@@model/…</c>, <c>@@collection/…</c>) target the CURRENT node and are always valid.</para>
/// </summary>
public class DocumentationEmbedIntegrityTest
{
    private const string DocResourcePrefix = "MeshWeaver.Documentation.Data.";
    private const string AgentResourcePrefix = "MeshWeaver.AI.Data.Agent.";
    private const string SkillResourcePrefix = "MeshWeaver.AI.Data.Skill.";

    private static readonly Regex FencedCodeRegex = new("```.*?```", RegexOptions.Singleline | RegexOptions.Compiled);
    // A live embed: line-start (optional indent) '@@' followed by a path token (up to whitespace).
    private static readonly Regex LineStartEmbedRegex =
        new(@"^\s*@@([^\s`<]+)", RegexOptions.Multiline | RegexOptions.Compiled);

    // Reference verbs that target the CURRENT node (its data/content/schema/…), never a partition.
    private static readonly ImmutableHashSet<string> ReservedKeywords =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase,
            "data", "area", "schema", "model", "content", "collection", "menu");

    [Fact]
    public void AllLiveDocEmbeds_TargetPartitionsThatShipWithTheDocs()
    {
        var docAssembly = typeof(DocumentationExtensions).Assembly;
        var agentAssembly = typeof(AgentNodeType).Assembly;

        var markdown = LoadResources(docAssembly, DocResourcePrefix, "Doc", ".md")
            .Concat(LoadResources(agentAssembly, AgentResourcePrefix, "Agent", ".md"))
            .Concat(LoadResources(agentAssembly, SkillResourcePrefix, "Skill", ".md"))
            .ToImmutableArray();

        var shippablePartitions = BuildShippablePartitions(docAssembly, agentAssembly);

        var failures = new List<string>();

        foreach (var (nodePath, content) in markdown)
        {
            var visible = FencedCodeRegex.Replace(content, "");
            foreach (Match m in LineStartEmbedRegex.Matches(visible))
            {
                var token = m.Groups[1].Value.Trim('(', ')', '"');
                if (token.Length == 0)
                    continue;

                var normalized = token.TrimStart('/');
                // Relative / single-segment (@@Sibling, @@../X, @@Area) resolves against THIS doc node,
                // so it stays in the doc's own (shipped) partition — nothing cross-partition to check.
                if (!normalized.Contains('/') || normalized.StartsWith('.'))
                    continue;

                var firstSegment = normalized.Split('/')[0];
                // A reference verb (data/content/schema/…) targets the current node — always valid.
                if (ReservedKeywords.Contains(firstSegment) || firstSegment.Contains(':'))
                    continue;

                if (!shippablePartitions.Contains(firstSegment))
                    failures.Add(
                        $"{nodePath}: @@{token} — partition '{firstSegment}' is not shipped with the docs " +
                        $"(only {string.Join(", ", shippablePartitions.OrderBy(x => x))}). It will not resolve where " +
                        $"the docs are served, and renders \"No renderer is registered for area '{firstSegment}'\". " +
                        $"Ship the example node IN the Doc partition (like Cession/SocialMedia) and embed its Doc path.");
            }
        }

        failures.Should().BeEmpty(
            "every LIVE (line-start) @@ embed must target a partition that ships with the docs — an example " +
            "node from a sample partition (PythonDemo, Northwind, …) is absent on a real portal and renders " +
            "the \"No renderer is registered for area …\" error. Failures:\n{0}",
            string.Join("\n", failures));
    }

    /// <summary>
    /// The <c>Doc/Architecture/PythonCodeNodes</c> page embeds a live <c>python</c> Code node; that node
    /// must SHIP in the Doc partition (not a sample partition) so it resolves where the docs are served.
    /// Loads the Doc nodes with the real provider and asserts the embed target is a python Code node.
    /// </summary>
    [Fact]
    public void PythonCodeNodes_SampleStatistics_ShipsInDoc_AsAPythonCodeNode()
    {
        const string embedTarget = "Doc/Architecture/PythonCodeNodes/SampleStatistics";

        // Doc .json nodes use camelCase keys — case-insensitive options map nodeType/content
        // (the real import passes the hub options; here we only need the node's shape).
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var node = DocumentationNodeProvider.LoadIndexableNodes(options)
            .FirstOrDefault(n => string.Equals(n.Path, embedTarget, StringComparison.OrdinalIgnoreCase));

        node.Should().NotBeNull(
            $"the doc embeds @@{embedTarget}, so that node must ship in the Doc partition (Data/Architecture/PythonCodeNodes/SampleStatistics.json)");
        node!.NodeType.Should().Be("Code", "the embedded example is a runnable Code node");
    }

    /// <summary>
    /// The partitions a doc-serving portal is guaranteed to have alongside the docs:
    /// Doc/Agent/Skill, the partition of every node shipped in that data, and (because a NodeType's
    /// instances address under its own name) the id of every shipped <c>NodeType</c> node.
    /// </summary>
    private static ImmutableHashSet<string> BuildShippablePartitions(Assembly docAssembly, Assembly agentAssembly)
    {
        var set = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
        set.Add("Doc");
        set.Add("Agent");
        set.Add("Skill");

        foreach (var (assembly, prefix) in new[]
                 {
                     (docAssembly, DocResourcePrefix),
                     (agentAssembly, AgentResourcePrefix),
                     (agentAssembly, SkillResourcePrefix),
                 })
        foreach (var (_, json) in LoadResources(assembly, prefix, partition: null, ".json"))
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("namespace", out var ns) && ns.ValueKind == JsonValueKind.String)
                {
                    var partition = ns.GetString()!.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                    if (!string.IsNullOrEmpty(partition))
                        set.Add(partition);
                }
                // A NodeType node makes its own id an addressable partition (instances live under it).
                if (root.TryGetProperty("nodeType", out var nt) && nt.ValueKind == JsonValueKind.String
                    && string.Equals(nt.GetString(), "NodeType", StringComparison.OrdinalIgnoreCase)
                    && root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    set.Add(id.GetString()!);
            }
            catch (JsonException)
            {
                // Non-node json (config, data files) — not a partition source.
            }
        }

        return set.ToImmutable();
    }

    /// <summary>Loads embedded resources with the given extension, mapping resource name → node path.</summary>
    private static IEnumerable<(string Path, string Content)> LoadResources(
        Assembly assembly, string prefix, string? partition, string extension)
    {
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (!name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) continue;

            var rawPath = name[prefix.Length..^extension.Length].Replace('.', '/');
            if (rawPath.Equals("index", StringComparison.OrdinalIgnoreCase))
                rawPath = "";
            else if (rawPath.EndsWith("/index", StringComparison.OrdinalIgnoreCase))
                rawPath = rawPath[..^"/index".Length];
            var nodePath = partition is null
                ? rawPath
                : rawPath.Length == 0 ? partition : $"{partition}/{rawPath}";

            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            yield return (nodePath, reader.ReadToEnd());
        }
    }
}
