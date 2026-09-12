#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 THE TRAP: a reusable lane checks Systemorph/MeshWeaver out at the caller's <c>platform-ref</c>
/// — the PLATFORM the modules build against, which for an unpinned caller is the newest SEALED
/// set, hours behind the lane itself when the lane is called at <c>@main</c> — and then runs its
/// OWN helper scripts out of that checkout (<c>python3 meshweaver/.github/scripts/&lt;x&gt;.py</c>).
/// A script added to core in the SAME PR as the lane step that calls it is therefore absent from
/// every caller's checkout until a seal carries it, and the lane is red fleet-wide with
/// <c>python3: can't open file '…/meshweaver/.github/scripts/&lt;x&gt;.py': [Errno 2]</c>.
///
/// <para>It has happened twice. Plugins#1566 (2026-09-09): <c>check-abbreviated-shas.py</c> fetched
/// at a platform-ref cut hours before it landed — answered by the <c>scripts-ref</c> input on the
/// gate lane (MeshWeaver#3842) and the curl fetch shape of #4095. #4096 (2026-09-12, 16:43Z):
/// <c>module-pack-batch.py</c> landed with the batching step that runs it in <c>select</c>,
/// <c>pack</c> and <c>tests</c>; every <c>pack</c> leg of MeshWeaver.Plugins main (run
/// 34707906260) read it out of a checkout sealed at a7450f3aa (14:46Z) and died, and Plugins
/// published nothing until a seal carried the file. Nothing in core's own CI can see this: core's
/// <c>main-cd</c> calls the lane at <c>./</c> with its own sha as <c>platform-ref</c>, so the
/// checkout there always has the script.</para>
///
/// <para>THE RULE this guard holds: every <c>meshweaver/.github/scripts/&lt;file&gt;</c> a lane job
/// runs is either (a) FETCHED in that job, at the lane's SCRIPTS ref (<c>?ref=${SCRIPTS_REF}</c>
/// bound from <c>inputs.scripts-ref</c>, curl or <c>gh api</c>, into the path the job reads,
/// BEFORE the first use — or the job's whole <c>meshweaver</c> checkout is at
/// <c>inputs.scripts-ref</c>), or (b) on the explicit allow-list below of scripts old enough to be
/// in every set a caller can resolve. A new lane script is not "old enough" — it goes in (a), or
/// this guard names it. And the allow-list is a ratchet in both directions: an entry nothing
/// references any more is RED too.</para>
/// </summary>
public class ScriptsRefFetchGuard
{
    private const string ModulePack = ".github/workflows/node-repo-module-pack.yml";

    /// <summary>
    /// Scripts read out of the <c>platform-ref</c> checkout WITHOUT a fetch, and why that is
    /// tolerable for each: the date it landed on core main, after which every sealed set carries
    /// it. A frozen caller (<c>vars.MW_PLATFORM_REF</c>) pins the lane to the same wave, so the
    /// checkout it makes is never older than the lane that reads it. Adding an entry here is a
    /// claim that no caller can resolve a set from before that date — NOT a way past a red guard
    /// on the day the script lands.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> OldEnoughToBeInEverySealedSet =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["acr-login.sh"] = "2026-09-07 (88201f021)",
            ["check-module-platform-floor.py"] = "2026-09-07 (88201f021)",
            ["module-build-key.py"] = "2026-09-07 (88201f021)",
            ["module-build-ledger.py"] = "2026-09-07 (88201f021)",
            ["module-owned-platform.sh"] = "2026-09-07 (88201f021)",
            ["node-repo-pack-verify.py"] = "2026-09-07 (88201f021)",
            ["node-repo-scope.py"] = "2026-09-07 (88201f021)",
            ["workspace-build-verify.py"] = "2026-09-07 (88201f021)",
            ["node-repo-publication-base.py"] = "2026-09-09 (3a5f44b02)",
            ["node-repo-publication-reuse.py"] = "2026-09-09 (3a5f44b02)",
            ["module-publication.py"] = "2026-09-10 (0c7e29e50)",
        };

    private static readonly Regex ScriptReference =
        new(@"meshweaver/\.github/scripts/(?<file>[A-Za-z0-9_.-]+\.(?:py|sh))", RegexOptions.Compiled);

    [Fact]
    public void EveryLaneScriptReadFromTheMeshweaverCheckout_IsFetchedAtScriptsRef_OrOldEnough()
    {
        var offenders = new List<string>();
        var referenced = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (workflow, job, body) in LaneJobs())
        {
            var scripts = ScriptReference.Matches(body).Select(m => m.Groups["file"].Value).Distinct().ToList();
            if (scripts.Count == 0)
                continue;

            // (a') The job's whole checkout of Systemorph/MeshWeaver is at the lane's scripts ref —
            // the gate's `merge` job shape for gate-shard-merge.py.
            var checkoutAtScriptsRef = Regex.IsMatch(body,
                @"repository: Systemorph/MeshWeaver\n\s+ref: \$\{\{ inputs\.scripts-ref[^\n]*\n\s+path: meshweaver\n");

            foreach (var script in scripts)
            {
                referenced.Add(script);
                if (checkoutAtScriptsRef)
                    continue;

                var fetch = body.IndexOf($"contents/.github/scripts/{script}?ref=${{SCRIPTS_REF}}", StringComparison.Ordinal);
                var firstUse = ScriptReference.Matches(body).First(m => m.Groups["file"].Value == script).Index;
                if (fetch >= 0)
                {
                    // The ref the fetch reads must be the LANE's scripts ref, never platform-ref alone.
                    if (!Regex.IsMatch(body, @"SCRIPTS_REF: \$\{\{ inputs\.scripts-ref(?: \|\||\s*\}\})"))
                        offenders.Add($"{workflow} job `{job}`: fetches {script} at ${{SCRIPTS_REF}} but binds SCRIPTS_REF to something other than `inputs.scripts-ref || …`");
                    // Before the first use: the STEP that fetches (its `SCRIPT=…` target path is
                    // itself a reference) must be the first step that names the script.
                    var fetchStep = Math.Max(0, body.LastIndexOf("\n      - ", fetch, StringComparison.Ordinal));
                    if (firstUse < fetchStep)
                        offenders.Add($"{workflow} job `{job}`: fetches {script} AFTER its first use (fetch step at {fetchStep}, first use at {firstUse})");
                    continue;
                }

                if (OldEnoughToBeInEverySealedSet.ContainsKey(script))
                    continue;

                offenders.Add($"{workflow} job `{job}`: runs meshweaver/.github/scripts/{script} out of the platform-ref checkout "
                              + "without fetching it at ${SCRIPTS_REF}, and it is not on the old-enough allow-list");
            }
        }

        Assert.True(offenders.Count == 0,
            "A reusable lane may not run a script out of its `meshweaver/` checkout unless the script is fetched in that "
            + "job at the lane's SCRIPTS ref (`curl … contents/.github/scripts/<x>?ref=${SCRIPTS_REF}` with "
            + "`SCRIPTS_REF: ${{ inputs.scripts-ref || … }}`, before the first use — the node-repo-gate.yml "
            + "compose-gate-host.sh shape, #4095) or is old enough to be in every sealed platform set (the allow-list in "
            + "this guard, with its landing date). The checkout is at the caller's PLATFORM-ref — for an unpinned caller "
            + "the newest SEALED set, hours behind a lane called at @main — so a script that lands with the step that runs "
            + "it is absent fleet-wide until a seal carries it: #4096 stalled MeshWeaver.Plugins main publication on "
            + "2026-09-12 exactly this way (`python3: can't open file '…/meshweaver/.github/scripts/module-pack-batch.py'`).\n  "
            + string.Join("\n  ", offenders));

        var stale = OldEnoughToBeInEverySealedSet.Keys.Where(k => !referenced.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.True(stale.Count == 0,
            "Allow-list entries no lane reads out of its meshweaver/ checkout any more — remove them, the list is a ratchet: "
            + string.Join(", ", stale));
    }

    [Theory]
    [InlineData("select")]
    [InlineData("pack")]
    [InlineData("tests")]
    public void ModulePack_FetchesTheBatchScriptAtItsScriptsRef_AndSelfTestsIt_InEveryJobThatRunsIt(string job)
    {
        var body = JobBody(ModulePack, job);
        Assert.Contains("meshweaver/.github/scripts/module-pack-batch.py", body, StringComparison.Ordinal);

        var step = StepBlock(body, "- name: Fetch module-pack-batch.py at the lane's scripts ref");
        // The lane's scripts ref, falling back to this lane's existing selection pin (an immutable
        // core SHA the Plugins caller resolves at run time — what unstrands it without a caller
        // change) and only then to the platform's commit.
        Assert.Contains("SCRIPTS_REF: ${{ inputs.scripts-ref || inputs.build-logic-ref || inputs.platform-ref }}", step, StringComparison.Ordinal);
        // curl, not gh: `pack` and `tests` honour `runs-on: ${{ inputs.runner }}` and the self-hosted
        // image ships no `gh`. Same headers as the gate's compose-gate-host.sh fetch.
        Assert.Contains("curl -fsSL --retry 3 --retry-delay 5 -o \"$SCRIPT\"", step, StringComparison.Ordinal);
        Assert.Contains("-H \"Accept: application/vnd.github.raw+json\"", step, StringComparison.Ordinal);
        Assert.Contains("/repos/Systemorph/MeshWeaver/contents/.github/scripts/module-pack-batch.py?ref=${SCRIPTS_REF}", step, StringComparison.Ordinal);
        // Into the path every later `python3 meshweaver/.github/scripts/module-pack-batch.py` reads.
        Assert.Contains("SCRIPT=\"meshweaver/.github/scripts/module-pack-batch.py\"", step, StringComparison.Ordinal);
        // RED by name on a miss, on empty bytes, on a non-script — and the fetched bytes prove
        // themselves before anything drives them. Never weakened to a warning, never skipped.
        Assert.Contains("could not fetch module-pack-batch.py at ${SCRIPTS_REF}", step, StringComparison.Ordinal);
        Assert.Contains("[ -s \"$SCRIPT\" ] ||", step, StringComparison.Ordinal);
        Assert.Contains("head -c 2 \"$SCRIPT\" | grep -q '#!' ||", step, StringComparison.Ordinal);
        Assert.Contains("python3 \"$SCRIPT\" --self-test", step, StringComparison.Ordinal);
        Assert.DoesNotContain("continue-on-error", step, StringComparison.Ordinal);
        Assert.DoesNotContain("::warning::", step, StringComparison.Ordinal);
    }

    [Fact]
    public void ModulePack_DeclaresScriptsRef_AndSelectStillSelfTestsTheBatchScript()
    {
        var text = File.ReadAllText(Path.Combine(FindRepoRoot(), ModulePack));
        var input = Regex.Match(text, @"\n      scripts-ref:\n(?<body>(?:(?:        .*)?\n)+)");
        Assert.True(input.Success, $"{ModulePack} must declare a `scripts-ref` input (the gate lane's contract, MeshWeaver#3842)");
        Assert.Contains("default: ''", input.Groups["body"].Value, StringComparison.Ordinal);
        // The selection's own self-test inventory keeps the batch script — the fetch step proves
        // the bytes, this proves the selector's inventory is complete.
        Assert.Contains("python3 meshweaver/.github/scripts/module-pack-batch.py --self-test", JobBody(ModulePack, "select"), StringComparison.Ordinal);
    }

    /// <summary>Every (workflow, job, executable body) of every workflow under .github/workflows
    /// — the reusable lanes are the ones with a <c>meshweaver/</c> checkout, and the reference
    /// scan is what selects them.</summary>
    private static IEnumerable<(string Workflow, string Job, string Body)> LaneJobs()
    {
        var dir = Path.Combine(FindRepoRoot(), ".github", "workflows");
        foreach (var file in Directory.EnumerateFiles(dir, "*.yml", SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.Ordinal))
        {
            var text = File.ReadAllText(file);
            var name = Path.GetFileName(file);
            foreach (var (job, body) in Jobs(text))
                yield return (name, job, body);
        }
    }

    /// <summary>The jobs after the top-level <c>jobs:</c> key — a job key sits at exactly two
    /// spaces — with comment lines dropped so a prose mention of a script is not an invocation.</summary>
    private static IEnumerable<(string Job, string Body)> Jobs(string yaml)
    {
        var lines = yaml.Split('\n');
        var start = Array.FindIndex(lines, l => l == "jobs:");
        if (start < 0)
            yield break;
        string? job = null;
        var body = new List<string>();
        for (var i = start + 1; i <= lines.Length; i++)
        {
            var line = i < lines.Length ? lines[i] : null;
            var header = line is not null ? Regex.Match(line, @"^  (?<job>[A-Za-z_][A-Za-z0-9_-]*):\s*$") : null;
            if (line is null || header!.Success)
            {
                if (job is not null)
                    yield return (job, string.Join('\n', body.Where(l => !l.TrimStart().StartsWith('#'))));
                if (line is null)
                    yield break;
                job = header!.Groups["job"].Value;
                body = new List<string>();
                continue;
            }
            body.Add(line);
        }
    }

    private static string JobBody(string workflow, string job)
    {
        var text = File.ReadAllText(Path.Combine(FindRepoRoot(), workflow));
        var found = Jobs(text).FirstOrDefault(j => j.Job == job);
        Assert.True(found.Job == job, $"{workflow} must have a `{job}` job");
        return found.Body;
    }

    /// <summary>The step that carries <paramref name="marker"/>, up to the next step.</summary>
    private static string StepBlock(string job, string marker)
    {
        var at = job.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(at >= 0, $"the step `{marker.Trim()}` is gone — a lane job runs module-pack-batch.py out of a checkout that may not have it (#4096)");
        var end = job.IndexOf("\n      - ", at + 1, StringComparison.Ordinal);
        return job.Substring(at, (end < 0 ? job.Length : end) - at);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null, "could not locate the repository root (MeshWeaver.slnx) above the test bin");
        return dir!.FullName;
    }
}
