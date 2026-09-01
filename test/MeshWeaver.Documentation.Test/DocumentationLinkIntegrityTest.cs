using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using MeshWeaver.Markdown;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Pins the runtime link-resolution semantics for every markdown link in THIS repository's
/// embedded documentation (<c>src/MeshWeaver.Documentation/Data</c> → the <c>Doc</c> partition).
///
/// <para>At render time <c>LinkUrlCleanupExtension</c> resolves relative link URLs with
/// <see cref="PathUtils.ResolveRelativePath"/> against the node's FULL path (e.g.
/// <c>Doc/Architecture/AsynchronousCalls</c>) — so a sibling link must be written
/// <c>../Sibling</c>, a parent→child link is the bare child name, and there is no <c>xref:</c>
/// handler and no <c>.md</c>-suffixed node path. This test resolves every link with the REAL
/// <see cref="PathUtils"/> and asserts that every <c>Doc/…</c> target maps to an existing embedded
/// resource. A failure message names the source doc, the literal URL, and the resolved target.</para>
///
/// <para>🚨 WHY IT LIVES HERE. Its subject is this repository's own doc tree, and until now the
/// only thing pinning it was <c>MeshWeaver.AI.Test</c> in the PRIVATE sibling
/// <c>MeshWeaver.Plugins</c> — so a core pull request that broke a doc link went green here and
/// turned a DIFFERENT repository red, hours later, on a change it did not make. A gate belongs in
/// the repository that owns what it measures; the dependency between these two repos runs one way
/// (Plugins consumes the platform), and a gate pointing the other way inverts it.</para>
///
/// <para>Scope, deliberately: this checks <c>Doc/…</c> targets only. Links from a doc page INTO the
/// <c>Agent</c>/<c>Skill</c> partitions cannot be resolved from here — those resources are embedded
/// in <c>MeshWeaver.AI</c>, which ships from MeshWeaver.Plugins — and they stay pinned by that
/// repository's copy of this test, which sees BOTH assemblies. That is the correct direction: the
/// repo holding both halves validates the union; this repo validates its own half unconditionally.
/// Targets in any other mesh partition (sample data, app routes) are not resolvable from embedded
/// resources at all and are skipped, exactly as they are there.</para>
///
/// <para>Links inside fenced code blocks and inline code spans are not rendered as links, so they
/// are stripped before extraction. Image links (<c>![…]</c>) route through
/// <c>ImgPathMarkdownExtension</c> (static content), not the link resolver, and are excluded.</para>
/// </summary>
public class DocumentationLinkIntegrityTest
{
    private const string DocResourcePrefix = "MeshWeaver.Documentation.Data.";

    private static readonly Regex LinkRegex = new(@"(?<!\!)\[(?:[^\[\]]|\[[^\]]*\])*\]\(([^)\s]+)\)", RegexOptions.Compiled);
    private static readonly Regex FencedCodeRegex = new("```.*?```", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex InlineCodeRegex = new("`[^`\n]*`", RegexOptions.Compiled);

    [Fact]
    public void AllInternalDocLinks_ResolveToExistingNodes()
    {
        var docNodes = LoadMarkdownNodes(typeof(DocumentationExtensions).Assembly, DocResourcePrefix, "Doc");

        docNodes.Should().NotBeEmpty(
            "the Doc partition's markdown must be embedded in MeshWeaver.Documentation — an empty " +
            "resource set would make this gate pass having checked nothing, which is the one " +
            "outcome it exists to prevent");

        var knownPaths = docNodes.Keys
            .Append("Doc")
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

        var failures = new List<string>();

        foreach (var (nodePath, content) in docNodes)
            CheckLinks(nodePath, content, knownPaths, failures);

        failures.Should().BeEmpty(
            "every internal Doc markdown link must resolve to an existing node under the runtime " +
            "semantics of LinkUrlCleanupExtension + PathUtils.ResolveRelativePath. Sibling links " +
            "need '../Sibling'; parent→child links are bare names; absolute links start with '/'; " +
            "'xref:' and '.md' suffixes never resolve. Failures:\n{0}",
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

            // Mirrors LinkUrlCleanupExtension.ResolveLinks — with ONE deliberate difference,
            // recorded here because it looks like an oversight (Copilot review, #2965).
            // Production writes `url.StartsWith("http")`, the culture-sensitive overload; this
            // uses Ordinal. The two can disagree only for a URL whose "http" is preceded by a
            // culture-ignorable character, and they disagree in the SAFE direction: Ordinal skips
            // FEWER links, so such a URL is checked rather than waved through. The worst case is a
            // false RED naming the exact page and URL — never a broken link that slips past. The
            // alternative would make a test's verdict depend on the runner's current culture,
            // which is a flake source production does not have to care about and a gate does.
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

            // Doc only. Agent/Skill resolve out of MeshWeaver.AI (MeshWeaver.Plugins) and are
            // pinned by that repo's copy; every other partition is unresolvable from resources.
            if (!string.Equals(target.Split('/')[0], "Doc", StringComparison.OrdinalIgnoreCase))
                continue;

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
