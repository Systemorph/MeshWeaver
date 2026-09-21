using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Holds <c>AGENTS.md</c> and the <c>.claude/skills/**</c> files it MANDATES to the same rule.
///
/// <para><b>Why it exists (MeshWeaver#4965).</b> #4964 changed the What's New rule from *per
/// user-facing change* to *per release*, in <c>AGENTS.md</c> and in <c>ReleaseProcess.md</c>. It did
/// not change <c>.claude/skills/pullrequest/SKILL.md</c>, whose step 0.5 still opened with <i>"every
/// USER-FACING PR ships a 'What's New' entry"</i> and still carried the minting apparatus. And
/// <c>AGENTS.md</c> *requires* that skill for PR work, so for as long as both stood an agent received
/// two contradictory rules and the operative one was whichever it read last — with the skill holding
/// the copy-pasteable commands. CI was green throughout; the automatic reviewer caught it by hand.
/// The two guards that already read <c>.claude/skills/**</c> check something else
/// (<see cref="GuidanceBridgeRatchetGuard"/> ratchets <c>.ToTask(</c> inside csharp fences;
/// <c>SkillFrontMatterGuard</c> checks front-matter shape), so a rule could land in <c>AGENTS.md</c>
/// while its operational half stayed contradicted, indefinitely and silently.</para>
///
/// <para>🚨 <b>This deliberately does NOT try to be a semantic diff.</b> "Do two prose documents
/// agree?" is not mechanically decidable. It is a RATCHET over (rule, forbidden literal) pairs — the
/// cheaper of the two forms #4965 proposes, and the one that catches the case that happened.</para>
///
/// <para>🚨 <b>And the pairs cannot go stale silently, which is the property that makes a table of
/// literals safe to keep.</b> A forbidden-literal table is a statement about today's wording: retire
/// the rule and the entry permits nothing, forbids nothing, and reads as coverage. So every pair also
/// names EVIDENCE that its rule is still stated in <c>AGENTS.md</c>, and a pair whose evidence has
/// gone is <b>RED, not inert</b> (<see cref="EveryPairsRuleIsStillStatedInAgentsMd"/>). Whoever
/// retires a rule has to retire its pair in the same change set — which is exactly the
/// half-a-change-lands-and-nothing-is-red failure this guard exists for, applied to the guard itself.
/// It is the same discipline the transitional allow files use: an entry that has outlived its subject
/// fails rather than lingering.</para>
///
/// <para><b>What it does NOT establish.</b> That the skills agree with <c>AGENTS.md</c> in general —
/// only that they do not contain the specific literals a superseded rule left behind, plus the one
/// SHAPE below. A contradiction phrased differently passes. That is a floor, not a ceiling, and the
/// register is meant to grow an entry each time a rule change is found to have left an operational
/// half behind.</para>
/// </summary>
public class AgentsRuleAndSkillAgreementGuard
{
    /// <param name="RuleId">The policy or shared-rule id, for the failure message.</param>
    /// <param name="RuleEvidence">
    /// A literal that must still appear in <c>AGENTS.md</c>. Its absence means the rule was retired
    /// or reworded and this pair is stale — which FAILS, so the pair cannot outlive its subject.
    /// </param>
    /// <param name="ForbiddenInSkills">
    /// Literals that must not appear anywhere under <c>.claude/skills/**</c>. Each is a fragment the
    /// SUPERSEDED rule left behind, chosen to be command-shaped or verbatim-quotation-shaped so that a
    /// skill correctly STATING the current rule cannot match it.
    /// </param>
    /// <param name="Why">Printed on failure: what the contradiction does to a reader.</param>
    private sealed record Pair(string RuleId, string RuleEvidence, string[] ForbiddenInSkills, string Why);

    private static readonly Pair[] Pairs =
    [
        new("whatsnew-cadence",
            // Both AGENTS.md sentences that carry this rule cite the policy id, so the id is the
            // evidence: reword the prose freely, retire the policy and this pair reds.
            RuleEvidence: "policy `whatsnew-cadence`",
            ForbiddenInSkills:
            [
                // The exact sentence the superseded rule opened with. A skill saying the OPPOSITE
                // ("a merge mints no What's New file") cannot match it.
                "every USER-FACING PR ships",
                // The minting apparatus: the shell variable the old step used to name the file it
                // created, and the front-matter printf that filled it.
                "NOTE_FILE",
            ],
            Why: "a skill telling an author to mint a dated What's New file per PR contradicts "
                 + "AGENTS.md, and the skill is the half with the copy-pasteable commands — so the "
                 + "skill is the half that gets followed"),
    ];

    /// <summary>
    /// 🚨 THE ANTI-ROT HALF. A pair whose rule is no longer stated in <c>AGENTS.md</c> is stale, and a
    /// stale pair forbids literals for a rule nobody holds any more while reading as coverage. It
    /// fails, so retiring a rule and retiring its pair are one change set.
    /// </summary>
    [Fact]
    public void EveryPairsRuleIsStillStatedInAgentsMd()
    {
        var agents = File.ReadAllText(Path.Combine(FindRepoRoot(), "AGENTS.md"));

        var stale = Pairs
            .Where(p => !agents.Contains(p.RuleEvidence, StringComparison.Ordinal))
            .Select(p => $"  {p.RuleId} — AGENTS.md no longer contains its evidence: \"{p.RuleEvidence}\"")
            .ToArray();

        Assert.True(stale.Length == 0,
            "🚨 A registered (rule, forbidden literal) pair has outlived its rule. It now forbids "
            + "text on behalf of a rule AGENTS.md no longer states — permitting nothing, catching "
            + "nothing, and reading as coverage. Delete the pair in the same change set that retired "
            + "the rule, or restore the evidence.\n"
            + string.Join("\n", stale));
    }

    /// <summary>
    /// The half #4965 is about: no skill carries the wording or the apparatus of a rule
    /// <c>AGENTS.md</c> has superseded.
    /// </summary>
    [Fact]
    public void NoSkillCarriesASupersededRulesWording()
    {
        var root = FindRepoRoot();
        var findings = SkillFiles(root)
            .SelectMany(file => Contradictions(File.ReadAllText(file))
                .Select(hit => $"  {Path.GetRelativePath(root, file)} — {hit}"))
            .ToArray();

        Assert.True(findings.Length == 0,
            "🚨 A skill contradicts a rule AGENTS.md states. AGENTS.md REQUIRES these skills for the "
            + "work they cover, so an agent reads both and follows whichever it read last — and the "
            + "skill is the one with the commands in it. Fix the skill; do not delete the pair.\n"
            + string.Join("\n", findings));
    }

    /// <summary>
    /// The one assertion here that is a SHAPE rather than a literal, so a differently-worded relapse
    /// still reds: nothing under <c>.claude/skills/**</c> may CONSTRUCT a path under <c>WhatsNew/</c>.
    /// Naming the path in prose with placeholder metavariables — which the release procedure
    /// legitimately does — is fine; building it with a shell interpolation, or naming it beside a
    /// <c>printf</c>, a <c>cat &gt;</c>, a redirect or a <c>git add</c>, is minting one. See the
    /// pattern's own remarks: its first version looked for the verbs alone and MISSED the real defect.
    /// </summary>
    [Fact]
    public void NoSkillMintsAFileUnderAWhatsNewPath()
    {
        var root = FindRepoRoot();
        var findings = SkillFiles(root)
            .SelectMany(file => MintingLines(File.ReadAllText(file))
                .Select(line => $"  {Path.GetRelativePath(root, file)} — {line}"))
            .ToArray();

        Assert.True(findings.Length == 0,
            "🚨 A skill contains a line that WRITES a file under a What's New path. A What's New entry "
            + "is written per RELEASE (policy `whatsnew-cadence`), so a per-PR procedure must have "
            + "nothing to mint — a merge updates its DOC PAGE instead.\n"
            + string.Join("\n", findings));
    }

    /// <summary>
    /// 🚨 THE POSITIVE CONTROL. A guard whose detector has stopped matching passes having checked
    /// nothing, and both detectors above are expected to find nothing in a healthy tree — so neither
    /// of them can tell "clean" from "broken" on its own. These cases feed each detector the text the
    /// real defect had and assert it is NAMED, and feed it the corrected text and assert it is not.
    /// </summary>
    [Fact]
    public void BothDetectorsSeeTheDefectTheyWereWrittenFor()
    {
        // Verbatim from .claude/skills/pullrequest/SKILL.md as #4964 left it (MeshWeaver#4965).
        const string asItWas = """
            ### 0.5 What's New
            🚨 every USER-FACING PR ships a "What's New" entry.
            DATE=$(date -u +%Y-%m-%d)
            NOTE_FILE="src/MeshWeaver.Documentation/Data/WhatsNew/$DATE-$SLUG.md"
            printf 'Name: %s\n' "$TITLE" > "$NOTE_FILE"
            git add "$NOTE_FILE"
            """;

        Contradictions(asItWas).Should().NotBeEmpty(
            "the wording detector must name the sentence and the minting variable the real defect had");
        Contradictions(asItWas).Should().HaveCountGreaterThanOrEqualTo(2,
            "both of whatsnew-cadence's forbidden literals are present in that text");
        MintingLines(asItWas).Should().NotBeEmpty(
            "the shape detector must name the line that writes under a WhatsNew path");

        // And the corrected shape — which STATES the current rule and names the path in prose, as the
        // release procedure legitimately does — must be clean under both, or the guard reds the fix.
        const string asItIs = """
            ### 0.5 The durable form
            🚨 A merge mints no What's New file — those are written per RELEASE.
            When you are CUTTING A RELEASE the entry's path is
              core: src/MeshWeaver.Documentation/Data/WhatsNew/<yyyy-MM-dd>-<slug>.md
            """;

        Contradictions(asItIs).Should().BeEmpty("a skill stating the CURRENT rule must not match");
        MintingLines(asItIs).Should().BeEmpty("naming the path in prose is not minting a file");
    }

    /// <summary>Literals from a superseded rule, found in one skill's text.</summary>
    private static IEnumerable<string> Contradictions(string skill) =>
        from pair in Pairs
        from forbidden in pair.ForbiddenInSkills
        where skill.Contains(forbidden, StringComparison.Ordinal)
        select $"carries \"{forbidden}\", superseded by `{pair.RuleId}`: {pair.Why}";

    // 🚨 THE DISCRIMINATOR IS A PATH BEING BUILT AT RUN TIME, not a verb — measured against the real
    // defect and against the corrected text (see BothDetectorsSeeTheDefectTheyWereWrittenFor).
    //
    // The first version of this pattern looked for `printf` / `cat >` / `git add` on the same line as
    // `WhatsNew`, and it MISSED the actual defect: the apparatus assigned the path to a variable
    // (`NOTE_FILE="…/WhatsNew/$DATE-$SLUG.md"`) and every verb afterwards named the VARIABLE. So the
    // shape is a `WhatsNew/` PATH carrying a shell interpolation — which only a line that constructs a
    // filename does. The release procedure legitimately quotes the same path with placeholder
    // metavariables (`WhatsNew/<yyyy-MM-dd>-<slug>.md`), and those carry no `$`.
    //
    // The trailing slash is load-bearing: without it this matches `WhatsNewEntryIntegrityTest`, which
    // the skills mention on a line that also quotes an anchored regex ending in `$`.
    //
    // The second arm keeps the verb form, for a mint that interpolates nothing.
    private static readonly Regex Minting = new(
        @"WhatsNew/[^\s]*\$|\$\{?[A-Za-z_][^\s]*WhatsNew/|(printf|cat\s*>|git\s+add|>\s*""?)[^\n]*WhatsNew/",
        RegexOptions.Compiled);

    private static IEnumerable<string> MintingLines(string skill) =>
        skill.Split('\n')
            .Select(line => line.Trim())
            .Where(line => Minting.IsMatch(line))
            .Select(line => "mints a What's New file: " + (line.Length > 160 ? line[..160] + "…" : line));

    private static IEnumerable<string> SkillFiles(string root)
    {
        var skills = Path.Combine(root, ".claude", "skills");

        // 🚨 Not an optional root. AGENTS.md links these skills and requires them for the work they
        // cover, so an absent directory means the scan found nothing to check — which is a broken
        // guard, not a clean tree (the failure SourceScan raises for the same reason).
        Assert.True(Directory.Exists(skills),
            $"🚨 {skills} does not exist, so this guard scanned NOTHING. AGENTS.md mandates the "
            + "skills under it; a guard that checks no files still passes, which is the failure it "
            + "exists to prevent. Fix the path or the checkout — never drop the root.");

        var files = Directory.EnumerateFiles(skills, "*.md", SearchOption.AllDirectories).ToArray();

        Assert.True(files.Length > 0,
            $"🚨 {skills} exists and contains no .md file, so this guard scanned NOTHING.");

        return files;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException(
                "Could not locate the repo root (MeshWeaver.slnx) from " + AppContext.BaseDirectory);
    }
}
