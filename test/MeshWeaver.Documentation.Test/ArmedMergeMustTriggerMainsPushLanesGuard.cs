#pragma warning disable CS1591

using System;
using System.IO;
using System.Linq;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 A workflow that MERGES a pull request — or arms auto-merge on one — must never do it with
/// <c>secrets.GITHUB_TOKEN</c>. GitHub performs an auto-merge as the identity that armed it, and a
/// push created with <c>GITHUB_TOKEN</c> deliberately does not trigger workflow runs. The merge
/// lands, and <b>main's entire <c>push</c> lane silently does not exist for that commit</b>.
///
/// <para><b>The measured outage (#2916, 2026-09-01).</b> <c>auto-arm.yml</c> shipped armed with
/// <c>GITHUB_TOKEN</c>. Four consecutive merges landed as <c>github-actions[bot]</c> and produced
/// no <c>push</c>-event run of anything — not <c>MeshWeaver Build and Test</c>, not
/// <c>Chart Gate</c>, not <c>Hosting Operator</c>. No push run means no
/// <c>Consolidate test results</c> check on main's HEAD, and <c>main-cd</c>'s readiness gate reads
/// that as <c>absent/none</c> and waits — on every tick, forever. Nothing was promoted for six
/// hours and every self-updating install stayed on the previous image, while every dashboard was
/// green: the last commit that reached the image jobs was the last one a human had merged.</para>
///
/// <para><b>Why this guard reads CONFIGURATION, which is normally the weak shape.</b> AGENTS.md is
/// right that a check asserting configuration cannot see its outcome fail, and this file is a
/// deliberate exception rather than an oversight. The outcome here — "does merging this PR start
/// main's push lanes?" — is unobservable from a pull-request run BY CONSTRUCTION: it can only be
/// observed on main, after the merge, at which point the evidence that would have caught it is the
/// very thing that is missing. There is no run to inspect, no skipped job, no pending check; the
/// absence is the symptom. The credential is the only place a pre-merge check can stand. So the
/// guard is honest about measuring the input, and the input is causal rather than correlated: with
/// <c>GITHUB_TOKEN</c> the trigger is suppressed 100% of the time, by documented design.</para>
///
/// <para>The companion assertion is that the arm step still names a token at all — a step that
/// simply dropped its <c>GH_TOKEN</c> would fall back to whatever the runner has and reintroduce
/// the same failure by omission.</para>
/// </summary>
public class ArmedMergeMustTriggerMainsPushLanesGuard
{
    /// <summary>
    /// The <c>gh</c> invocations that merge a PR or arm it to merge later. Each produces a commit
    /// on the default branch attributed to whoever the token belongs to.
    /// </summary>
    private static readonly string[] MergingCommands =
    [
        "gh pr merge",
        "enablePullRequestAutoMerge",
    ];

    private static string WorkflowsDir() =>
        Path.Combine(FindRepoRoot(), ".github", "workflows");

    /// <summary>
    /// Executable lines only. A guard that matches comment prose is measuring the documentation,
    /// not the workflow — and this repository's workflows explain themselves at length, including
    /// by quoting the very token this file forbids.
    /// </summary>
    private static string[] ExecutableLines(string text) =>
        text.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("#", StringComparison.Ordinal))
            .ToArray();

    [Fact]
    public void NoWorkflowMergesOrArmsAPullRequestWithTheDefaultToken()
    {
        var offenders = Directory
            .EnumerateFiles(WorkflowsDir(), "*.yml")
            .Select(path => (path, lines: ExecutableLines(File.ReadAllText(path))))
            .Where(w => w.lines.Any(l => MergingCommands.Any(c => l.Contains(c, StringComparison.Ordinal))))
            .Where(w => w.lines.Any(l =>
                l.Contains("GH_TOKEN", StringComparison.Ordinal) &&
                l.Contains("secrets.GITHUB_TOKEN", StringComparison.Ordinal)))
            .Select(w => Path.GetFileName(w.path))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"These workflows merge (or arm auto-merge on) a pull request using secrets.GITHUB_TOKEN: "
            + $"{string.Join(", ", offenders)}.\n"
            + "GitHub performs the merge as the arming identity, and a push created with GITHUB_TOKEN "
            + "does not trigger workflow runs — so main's HEAD gets NO push-event run, no "
            + "'Consolidate test results' check, and main-cd waits for evidence that can never "
            + "arrive. Nothing is ever promoted and every self-updating install freezes (#2916).\n"
            + "Mint a GitHub App installation token instead (actions/create-github-app-token, "
            + "permission-contents: write + permission-pull-requests: write) and use that as GH_TOKEN.");
    }

    /// <summary>
    /// The arm lane must name its credential explicitly. Deleting the <c>GH_TOKEN</c> line entirely
    /// would pass the check above while recreating the outage — <c>gh</c> would fall back to
    /// whatever the runner exposes.
    /// </summary>
    [Fact]
    public void TheArmLaneNamesAMintedInstallationTokenExplicitly()
    {
        var path = Path.Combine(WorkflowsDir(), "auto-arm.yml");
        Assert.True(File.Exists(path), $"{path} is missing — the arm lane is the subject of this guard.");

        var lines = ExecutableLines(File.ReadAllText(path));

        Assert.True(
            lines.Any(l => l.Contains("actions/create-github-app-token", StringComparison.Ordinal)),
            "auto-arm.yml no longer mints a GitHub App installation token. Its merges are then "
            + "attributed to whatever identity remains, and if that is github-actions[bot] the "
            + "resulting push starts no CI at all (#2916).");

        Assert.True(
            lines.Any(l =>
                l.Contains("GH_TOKEN", StringComparison.Ordinal) &&
                l.Contains("steps.arm-token.outputs.token", StringComparison.Ordinal)),
            "auto-arm.yml's arm step does not pass the minted token as GH_TOKEN. Without it gh "
            + "falls back to the runner's default credential — the #2916 failure, by omission "
            + "rather than by choice.");

        foreach (var permission in new[] { "permission-contents: write", "permission-pull-requests: write" })
        {
            Assert.True(
                lines.Any(l => l.Contains(permission, StringComparison.Ordinal)),
                $"auto-arm.yml's token mint no longer requests '{permission}'. Arming is the "
                + "enablePullRequestAutoMerge mutation (Pull requests: write) and the merge it "
                + "performs writes to the branch (Contents: write); requesting both explicitly is "
                + "what makes a missing grant fail at the mint instead of somewhere downstream.");
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".github", "workflows")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
