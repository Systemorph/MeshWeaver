using System.Text;
using Xunit;
using YamlDotNet.Serialization;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 <b>Every front-matter block under <c>src/MeshWeaver.Documentation/Data</c> must PARSE as
/// YAML</b> — the same rule <see cref="SkillFrontMatterGuard"/> already enforces for
/// <c>.claude/skills/*/SKILL.md</c>, pointed at the other tree that is read the same way.
///
/// <para>The guard existed and was correct; its SUBJECT was simply narrower than the defect. When
/// this was written, <b>20 of the 1,654 front-matter files in the doc tree were refused by a YAML
/// parser</b> — 7 Architecture pages (<c>ReadingCiSignals</c>, <c>ContinuousDeliveryContract</c>,
/// <c>DenialIsAnAnswer</c>, …), 12 What's New entries and one GUI page — every one of them shipped,
/// green, for weeks.</para>
///
/// <para><b>Why nothing went red.</b> <c>MarkdownFileParser</c> catches the deserializer failure and
/// falls back to a regex rescue that recovers <c>NodeType</c>, <c>Name</c>/<c>Title</c>,
/// <c>Category</c>, <c>Icon</c>/<c>Thumbnail</c>, <c>State</c> and <c>Order</c> — and <b>not</b>
/// <c>Description</c>/<c>Abstract</c> (<c>MarkdownFileParser.cs</c>: <c>Description =
/// frontMatter?.Abstract</c>). So the node lands, the page renders, the title is right, and the
/// description is silently absent from every catalog card, TOC entry and Postgres search row.</para>
///
/// <para>🚨 <b>And the test that was watching could not see it.</b>
/// <see cref="WhatsNewEntryIntegrityTest"/> asserts each entry carries a <c>Description:</c> — but
/// it reads the field with a REGEX, the same way the rescue does. A line the regex finds and the
/// parser refuses satisfies it. Twelve entries were green in that test and description-less in the
/// mesh at the same time: two readers, one artefact, opposite verdicts. This guard runs the REAL
/// parser, which is the only reader whose answer matches the runtime's.</para>
///
/// <para>Scope, deliberately narrow: a file with no front matter at all is not a failure here (a
/// doc page may legitimately carry none) — the rule is that a block which OPENS must parse.</para>
/// </summary>
public class DocumentationFrontMatterGuard
{
    private const string DataRoot = "src/MeshWeaver.Documentation/Data";

    /// <summary>Every shipped doc markdown file that OPENS a front-matter block, as (path, front matter).</summary>
    private static (string Path, string FrontMatter)[] FrontMatterFiles()
    {
        var root = SourceScan.FindRepoRoot();
        var dir = Path.Combine(root, DataRoot);
        if (!Directory.Exists(dir))
            return [];

        return Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => (Path: Path.GetRelativePath(root, p), FrontMatter: FrontMatterOf(File.ReadAllText(p))))
            .Where(x => x.FrontMatter is not null)
            .Select(x => (x.Path, FrontMatter: x.FrontMatter!))
            .ToArray();
    }

    /// <summary>
    /// The text between the opening and closing <c>---</c> fences; null when the file opens none.
    ///
    /// <para>🚨 An UNTERMINATED block is not "no front matter" — it returns
    /// <see cref="Unterminated"/> so the caller reports it. Treating it as absent would let the one
    /// shape that cannot possibly be read escape the guard silently, which is the skip-trapdoor this
    /// file exists to close.</para>
    /// </summary>
    private const string Unterminated = "\u0000unterminated";

    private static string? FrontMatterOf(string text)
    {
        var normalized = text.Replace("\r\n", "\n");
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
            return null;
        var end = normalized.IndexOf("\n---", 3, StringComparison.Ordinal);
        return end < 0 ? Unterminated : normalized[4..end];
    }

    [Fact]
    public void EveryDocFrontMatter_Parses()
    {
        var files = FrontMatterFiles();

        // 🚨 A guard must never pass on no evidence. A moved or renamed Data tree would otherwise
        // turn this into a green tick over an empty scan — the skip-trapdoor shape AGENTS.md bans.
        Assert.True(files.Length > 100,
            $"Only {files.Length} front-matter file(s) found under '{DataRoot}' — this guard would "
            + "pass having checked (almost) nothing. Point DataRoot at where the doc tree moved to; "
            + "never lower this floor to make it green.");

        var offenders = new StringBuilder();
        foreach (var (path, frontMatter) in files)
        {
            if (ReferenceEquals(frontMatter, Unterminated))
            {
                offenders.AppendLine(
                    $"  {path}: the front-matter block OPENS with '---' and never closes, so no "
                    + "parser can read it and every field in it is lost.");
                continue;
            }

            try
            {
                new DeserializerBuilder().Build().Deserialize<Dictionary<string, object>>(frontMatter);
            }
            catch (Exception ex)
            {
                offenders.AppendLine(
                    $"  {path}: front matter does NOT parse — {ex.GetType().Name}: "
                    + ex.Message.Replace("\n", " ").Trim());
            }
        }

        Assert.True(offenders.Length == 0,
            "These doc pages have front matter a YAML parser rejects. They still IMPORT — the "
            + "MarkdownFileParser rescue recovers Name, Category, Icon, State and Order — but NOT "
            + "Description, so the page ships with no summary on any card, TOC entry or search row, "
            + "and nothing anywhere goes red:\n" + offenders
            + "\nUsually the fix is to QUOTE the scalar: an unquoted value containing ': ' makes the "
            + "line read as a nested mapping and kills the whole document. A long Description reads "
            + "best as a folded block scalar (Description: >-).");
    }

    [Fact]
    public void NoDocFrontMatterValue_IsTruncatedByAnUnquotedHash()
    {
        var files = FrontMatterFiles();
        Assert.True(files.Length > 100, $"Too few front-matter files under '{DataRoot}' — see the sibling test.");

        var offenders = new StringBuilder();
        foreach (var (path, frontMatter) in files)
        {
            if (ReferenceEquals(frontMatter, Unterminated))
                continue;   // reported by the sibling test; nothing here can be read

            // This half cannot be caught by parsing alone: ' #' opens a comment, so the document is
            // VALID and the value is simply shorter than it was written. Compare what the parser
            // would return against the raw line to see the loss.
            foreach (var line in frontMatter.Split('\n'))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0 || line.StartsWith(' ') || line.StartsWith('#'))
                    continue;
                var raw = line[(colon + 1)..].Trim();
                if (raw.Length == 0 || raw[0] is '"' or '\'' or '|' or '>')
                    continue;   // quoted or block scalar — '#' is literal there
                var hash = raw.IndexOf(" #", StringComparison.Ordinal);
                if (hash >= 0)
                    offenders.AppendLine(
                        $"  {path}: '{line[..colon]}' is unquoted and contains ' #', so YAML truncates "
                        + $"it at \"{raw[..hash]}\" — losing \"{raw[(hash + 1)..]}\".");
            }
        }

        Assert.True(offenders.Length == 0,
            "These doc front-matter values are silently truncated by an unquoted '#'. The document "
            + "parses, so nothing else reports it — only the text goes missing:\n" + offenders
            + "\nQuote the scalar.");
    }
}
