using System.Text;
using Xunit;
using YamlDotNet.Serialization;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 <b>Every <c>.claude/skills/*/SKILL.md</c> front matter must PARSE as YAML.</b>
///
/// <para>A skill whose front matter is malformed does not error — it simply <b>never loads</b>, and
/// the only symptom is that the skill is missing from the listing. AGENTS.md already records one of
/// these costing a CI cycle (an unquoted <c>: </c> stopped a skill loading, 2026-08-12), and it
/// happened again on the <c>/sigsegv</c> skill in this very PR: an unquoted <c>description</c>
/// containing both <c>runtime bug: it is async</c> and <c>the #613 teardown inversion</c>.</para>
///
/// <para>Two different YAML rules, one silent outcome:</para>
/// <list type="bullet">
///   <item>a bare <c>: </c> inside an unquoted scalar makes the line read as a nested mapping —
///     <c>"mapping values are not allowed here"</c>, and the WHOLE document fails to parse;</item>
///   <item>a bare <c> #</c> opens a comment, silently TRUNCATING the value at that point — this one
///     parses cleanly, so nothing anywhere complains.</item>
/// </list>
///
/// <para>The guard runs the real parser rather than pattern-matching the two known shapes, so a
/// third way of breaking the document is caught the first time rather than after it ships. Quoting
/// the scalar fixes every one of them.</para>
/// </summary>
public class SkillFrontMatterGuard
{
    private const string SkillsRoot = ".claude/skills";

    /// <summary>Every shipped skill file, as (relative path, text).</summary>
    private static (string Path, string Text)[] SkillFiles()
    {
        var dir = Path.Combine(SourceScan.FindRepoRoot(), SkillsRoot);
        return Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "SKILL.md", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.Ordinal)
                .Select(p => (Path.GetRelativePath(SourceScan.FindRepoRoot(), p), File.ReadAllText(p)))
                .ToArray()
            : [];
    }

    /// <summary>
    /// The text between the opening and closing <c>---</c> fences, or null when the file carries no
    /// front matter at all (which is its own failure — a skill is configured by its front matter).
    /// </summary>
    private static string? FrontMatterOf(string text)
    {
        var normalized = text.Replace("\r\n", "\n");
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
            return null;
        var end = normalized.IndexOf("\n---", 3, StringComparison.Ordinal);
        return end < 0 ? null : normalized[4..end];
    }

    [Fact]
    public void EverySkill_HasFrontMatterThatParses()
    {
        var files = SkillFiles();

        // 🚨 A guard must never pass on no evidence. A moved skills directory would otherwise turn
        // this into a green tick over an empty scan — the skip-trapdoor shape AGENTS.md bans.
        Assert.True(files.Length > 0,
            $"No SKILL.md found under '{SkillsRoot}' — this guard would pass having checked nothing. "
            + "Point SkillsRoot at where the skills moved to; never delete the check to make it green.");

        var offenders = new StringBuilder();
        foreach (var (path, text) in files)
        {
            var frontMatter = FrontMatterOf(text);
            if (frontMatter is null)
            {
                offenders.AppendLine($"  {path}: no '---' front-matter block — the skill cannot be configured.");
                continue;
            }

            try
            {
                var parsed = new DeserializerBuilder().Build()
                    .Deserialize<Dictionary<string, object>>(frontMatter);
                if (parsed is null || !parsed.ContainsKey("name"))
                    offenders.AppendLine($"  {path}: front matter parsed but has no 'name'.");
            }
            catch (Exception ex)
            {
                offenders.AppendLine(
                    $"  {path}: front matter does NOT parse — {ex.GetType().Name}: "
                    + ex.Message.Replace("\n", " ").Trim());
            }
        }

        Assert.True(offenders.Length == 0,
            "These skills have front matter a YAML parser rejects, so they load as NOTHING and go "
            + "missing from the listing with no error anywhere:\n" + offenders
            + "\nUsually the fix is to QUOTE the scalar: an unquoted value containing ': ' makes the "
            + "line read as a nested mapping and kills the whole document.");
    }

    [Fact]
    public void NoSkillDescription_IsTruncatedByAnUnquotedHash()
    {
        var files = SkillFiles();
        Assert.True(files.Length > 0, $"No SKILL.md found under '{SkillsRoot}' — see the sibling test.");

        var offenders = new StringBuilder();
        foreach (var (path, text) in files)
        {
            var frontMatter = FrontMatterOf(text);
            if (frontMatter is null)
                continue;

            // This half cannot be caught by parsing alone: ' #' opens a comment, so the document is
            // VALID and the value is simply shorter than it was written. Compare what the parser
            // returns against the raw line to see the loss.
            foreach (var line in frontMatter.Split('\n'))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0 || line.StartsWith(' ') || line.StartsWith('#'))
                    continue;
                var key = line[..colon];
                var raw = line[(colon + 1)..].Trim();
                if (raw.Length == 0 || raw[0] is '"' or '\'' or '|' or '>')
                    continue;   // quoted or block scalar — '#' is literal there
                var hash = raw.IndexOf(" #", StringComparison.Ordinal);
                if (hash >= 0)
                    offenders.AppendLine(
                        $"  {path}: '{key}' is unquoted and contains ' #', so YAML truncates it at "
                        + $"\"{raw[..hash]}\" — losing \"{raw[(hash + 1)..]}\".");
            }
        }

        Assert.True(offenders.Length == 0,
            "These skill front-matter values are silently truncated by an unquoted '#'. The document "
            + "parses, so nothing else reports it — only the text goes missing:\n" + offenders
            + "\nQuote the scalar.");
    }
}
