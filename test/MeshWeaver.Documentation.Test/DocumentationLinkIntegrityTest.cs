using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using MeshWeaver.AI;
using MeshWeaver.Markdown;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Pins the runtime link-resolution semantics for every markdown link in the
/// embedded documentation (Doc partition), agent definitions (Agent partition),
/// and built-in skills (Skill partition).
///
/// <para>At render time <c>LinkUrlCleanupExtension</c> resolves relative link URLs
/// with <see cref="PathUtils.ResolveRelativePath"/> against the node's FULL path
/// (e.g. <c>Doc/Architecture/AsynchronousCalls</c>) — so a sibling link must be
/// written <c>../Sibling</c>, a parent→child link is the bare child name, and
/// there is no <c>xref:</c> handler and no <c>.md</c>-suffixed node path. This
/// test resolves every link with the REAL <see cref="PathUtils"/> and asserts
/// that every <c>Doc/…</c>, <c>Agent/…</c>, and <c>Skill/…</c> target maps to an
/// existing embedded resource. A failure message names the source doc, the literal
/// URL, and the resolved target.</para>
///
/// <para>Links inside fenced code blocks and inline code spans are not rendered
/// as links, so they are stripped before extraction. Image links (<c>![…]</c>)
/// route through <c>ImgPathMarkdownExtension</c> (static content), not the link
/// resolver, and are excluded. Targets outside the <c>Doc</c>/<c>Agent</c>/<c>Skill</c>
/// partitions (sample data, app routes) cannot be validated from embedded
/// resources and are skipped.</para>
/// </summary>
public class DocumentationLinkIntegrityTest
{
    private const string DocResourcePrefix = "MeshWeaver.Documentation.Data.";
    private const string AgentResourcePrefix = "MeshWeaver.AI.Data.Agent.";
    private const string SkillResourcePrefix = "MeshWeaver.AI.Data.Skill.";

    private static readonly Regex LinkRegex = new(@"(?<!\!)\[(?:[^\[\]]|\[[^\]]*\])*\]\(([^)\s]+)\)", RegexOptions.Compiled);
    private static readonly Regex FencedCodeRegex = new("```.*?```", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex InlineCodeRegex = new("`[^`\n]*`", RegexOptions.Compiled);

    [Fact]
    public void AllInternalDocAndAgentLinks_ResolveToExistingNodes()
    {
        var docAssembly = typeof(DocumentationExtensions).Assembly;
        var agentAssembly = typeof(AgentNodeType).Assembly;

        var docNodes = LoadMarkdownNodes(docAssembly, DocResourcePrefix, "Doc");
        var agentNodes = LoadMarkdownNodes(agentAssembly, AgentResourcePrefix, "Agent");
        var skillNodes = LoadMarkdownNodes(agentAssembly, SkillResourcePrefix, "Skill");

        var knownPaths = docNodes.Keys
            .Concat(agentNodes.Keys)
            .Concat(skillNodes.Keys)
            .Append("Doc")
            .Append("Agent")
            .Append("Skill")
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

        var failures = new List<string>();

        foreach (var (nodePath, content) in docNodes.Concat(agentNodes).Concat(skillNodes))
            CheckLinks(nodePath, content, knownPaths, failures);

        failures.Should().BeEmpty(
            "every internal Doc/Agent/Skill markdown link must resolve to an existing node " +
            "under the runtime semantics of LinkUrlCleanupExtension + PathUtils.ResolveRelativePath. " +
            "Sibling links need '../Sibling'; parent→child links are bare names; absolute links " +
            "start with '/'; 'xref:' and '.md' suffixes never resolve. Failures:\n{0}",
            string.Join("\n", failures));
    }

    private static void CheckLinks(
        string nodePath,
        string markdown,
        ImmutableHashSet<string> knownPaths,
        List<string> failures)
    {
        // Code is not rendered as links — drop fenced blocks first, then inline spans.
        var visible = InlineCodeRegex.Replace(FencedCodeRegex.Replace(markdown, ""), "");

        foreach (Match match in LinkRegex.Matches(visible))
        {
            var url = match.Groups[1].Value;

            // Mirrors LinkUrlCleanupExtension.ResolveLinks:
            var cleaned = url.TrimStart('@');
            if (cleaned.StartsWith("http", StringComparison.Ordinal)
                || cleaned.StartsWith('#')
                || cleaned.StartsWith("mailto:", StringComparison.Ordinal))
                continue;

            var hashIndex = cleaned.IndexOf('#');
            if (hashIndex >= 0)
                cleaned = cleaned[..hashIndex];
            if (cleaned.Length == 0)
                continue;

            var target = cleaned.StartsWith('/')
                ? cleaned.TrimStart('/')
                : PathUtils.ResolveRelativePath(cleaned, nodePath);

            if (target.Contains("xref:", StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"{nodePath}: ({url}) — 'xref:' has no handler in the markdown pipeline");
                continue;
            }

            if (target.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"{nodePath}: ({url}) — node paths have no '.md' suffix (resolved to /{target})");
                continue;
            }

            var partition = target.Split('/')[0];
            if (partition is not ("Doc" or "Agent" or "Skill"))
                continue; // other mesh partitions can't be validated from embedded resources

            if (!knownPaths.Contains(target.TrimEnd('/')))
                failures.Add($"{nodePath}: ({url}) — resolves to /{target}, which does not exist");
        }
    }

    /// <summary>
    /// Replicates the embedded-resource path mapping used at runtime
    /// (EmbeddedResourceStorageAdapter.BuildIndex + the parsers' DeriveIdAndNamespace):
    /// resource name → '/'-separated path under the partition; an <c>index</c> leaf
    /// represents the folder node itself.
    /// </summary>
    private static ImmutableDictionary<string, string> LoadMarkdownNodes(
        Assembly assembly, string prefix, string partition)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                continue;

            var rawPath = name[prefix.Length..^".md".Length].Replace('.', '/');
            if (rawPath.Equals("index", StringComparison.OrdinalIgnoreCase))
                rawPath = "";
            else if (rawPath.EndsWith("/index", StringComparison.OrdinalIgnoreCase))
                rawPath = rawPath[..^"/index".Length];

            var nodePath = rawPath.Length == 0 ? partition : $"{partition}/{rawPath}";

            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            builder[nodePath] = reader.ReadToEnd();
        }

        return builder.ToImmutable();
    }
}
