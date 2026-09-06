#pragma warning disable CS1591

using System;
using System.Collections.Generic;
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

    /// <summary>
    /// The <c>gh</c> invocations (and actions) that OPEN a pull request. A pull request created
    /// with the default token starts no <c>pull_request</c> workflow run at all.
    /// </summary>
    private static readonly string[] PullRequestOpeningCommands =
    [
        "gh pr create",
        "peter-evans/create-pull-request",
    ];

    /// <summary>
    /// The two spellings of the suppressed credential. <c>${{ github.token }}</c> and
    /// <c>${{ secrets.GITHUB_TOKEN }}</c> are the SAME token; a guard that knows only one of them
    /// is a guard the next author walks past by writing the other. Pure.
    /// </summary>
    private static bool NamesTheDefaultToken(string line) =>
        line.Contains("GH_TOKEN", StringComparison.Ordinal)
        && (line.Contains("secrets.GITHUB_TOKEN", StringComparison.Ordinal)
            || line.Contains("github.token", StringComparison.Ordinal));

    /// <summary>
    /// One workflow's <c>steps:</c> entries, as text blocks — everything from a <c>- name:</c> /
    /// <c>- uses:</c> line up to the next one, with the file's preamble (workflow and job level,
    /// where a job-wide <c>env:</c> can also set <c>GH_TOKEN</c>) as the first block.
    ///
    /// <para>Step scope is what makes a token check honest. A file-wide match reads
    /// <c>release.yml</c> — which opens its bump pull request with a MINTED token and, four steps
    /// later, publishes the GitHub Release with the default one — as an offender, and a guard that
    /// slanders a correct file is a guard people learn to override.</para>
    ///
    /// <para>The blind spot, named rather than papered over: a token exported into
    /// <c>$GITHUB_ENV</c> by an earlier step is invisible here. Nothing in the fleet does that, and
    /// a guard that claims to see it would be worse than one that says where it stops. Pure.</para>
    /// </summary>
    private static string[] StepBlocks(string text)
    {
        var lines = text.Split('\n')
            .Where(l => !l.TrimStart().StartsWith("#", StringComparison.Ordinal))
            .ToArray();
        var blocks = new List<string>();
        var current = new List<string>();
        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("- name:", StringComparison.Ordinal)
                || trimmed.StartsWith("- uses:", StringComparison.Ordinal)
                || trimmed.StartsWith("- run:", StringComparison.Ordinal))
            {
                blocks.Add(string.Join('\n', current));
                current.Clear();
            }
            current.Add(line);
        }
        blocks.Add(string.Join('\n', current));
        return [.. blocks];
    }

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
            .Where(w => w.lines.Any(NamesTheDefaultToken))
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

    /// <summary>
    /// 🚨 <c>auto-arm.yml</c> tolerates a failed mint, and that tolerance is only defensible
    /// because the assertion it drops was MOVED rather than deleted.
    ///
    /// <para>The move was made because a missing grant is a property of the org installation, not
    /// of any pull request: asserting it per-PR produced an identical red on every open PR at
    /// once, told a reader nothing about the branch they were looking at, and could not be acted
    /// on from there. A check whose value never varies carries no information, and a permanent
    /// wall of red is how a real red stops being read. So the arm lane now degrades to a warning
    /// — the honest per-PR statement is "this PR was not armed", which costs a convenience and no
    /// check — while <c>arm-credential.yml</c> keeps failing red, once, against the repository.</para>
    ///
    /// <para><b>Delete that lane and the tolerance becomes exactly the trapdoor AGENTS.md
    /// forbids:</b> a credential that silently stopped working, an arm that silently stops
    /// happening, and green everywhere. This test is the coupling — the two files may not drift
    /// apart, and the day someone removes the preflight, the build says why it mattered.</para>
    /// </summary>
    [Fact]
    public void ToleratingAFailedMintRequiresARepoScopedAssertion()
    {
        var armLines = ExecutableLines(File.ReadAllText(Path.Combine(WorkflowsDir(), "auto-arm.yml")));
        if (!armLines.Any(l => l.Contains("continue-on-error", StringComparison.Ordinal)))
            return; // The arm lane asserts for itself; no companion lane is owed.

        var preflight = Path.Combine(WorkflowsDir(), "arm-credential.yml");
        Assert.True(
            File.Exists(preflight),
            "auto-arm.yml tolerates a failed token mint, but arm-credential.yml — the lane that "
            + "carries the assertion it dropped — is gone. Nothing now fails when the App loses "
            + "Pull requests: write: the mint fails, the arm is skipped, the job is green, and PRs "
            + "quietly stop landing with no red anywhere. Restore the lane, or delete the "
            + "continue-on-error and let auto-arm assert for itself again.");

        var lines = ExecutableLines(File.ReadAllText(preflight));

        Assert.True(
            lines.All(l => !l.Contains("continue-on-error", StringComparison.Ordinal)),
            "arm-credential.yml carries continue-on-error. It is the ONLY thing left that fails "
            + "when the arm credential is unusable; a tolerated failure there means no lane in the "
            + "repository asserts the credential at all.");

        // The whole point is that it runs without a pull request. A dispatch-only lane asserts
        // only when a human already suspects the answer.
        Assert.True(
            lines.Any(l => l.StartsWith("schedule:", StringComparison.Ordinal))
            || lines.Any(l => l.StartsWith("- cron:", StringComparison.Ordinal)),
            "arm-credential.yml has no schedule. Manual dispatch only asserts the credential when "
            + "someone already suspects it is broken, which is precisely when the assertion is no "
            + "longer needed.");

        foreach (var permission in new[] { "permission-contents: write", "permission-pull-requests: write" })
        {
            Assert.True(
                lines.Any(l => l.Contains(permission, StringComparison.Ordinal)),
                $"arm-credential.yml does not request '{permission}'. A token minted without naming "
                + "both permissions inherits whatever the installation happens to hold — and "
                + "'whatever it happens to hold' is the state that produced #2916. Requesting them "
                + "explicitly is the entire mechanism by which a missing grant fails here.");
        }
    }

    /// <summary>
    /// 🚨 A step that authenticates with an App INSTALLATION token must not assert on
    /// <c>repos/{owner}/{repo}</c>'s <c>.permissions</c> object.
    ///
    /// <para>That object reports the <b>authenticated user's</b> permissions on the repository. An
    /// installation token has no user, so GitHub answers every field <c>false</c> —
    /// <c>{"admin":false,"maintain":false,"pull":false,"push":false,"triage":false}</c> — for a
    /// token that works perfectly. Grepping it for <c>"push":true</c> therefore yields a check that
    /// <b>can never pass</b>.</para>
    ///
    /// <para><b>Measured.</b> <c>arm-credential.yml</c> shipped with exactly that probe and was red
    /// on runs 33605406624 and 33605578037 — <i>after</i> the org grant had landed, and after
    /// <c>auto-arm.yml</c> had demonstrably armed a pull request on its own using a token from the
    /// same mint. The lane was reporting a broken credential while the credential worked.</para>
    ///
    /// <para>An assertion that cannot pass is the same defect as one that cannot fail: it stops
    /// carrying information. It is arguably worse, because a permanent red slanders something that
    /// is healthy and trains readers to disbelieve the lane. The endpoint an installation token can
    /// actually answer about itself is <c>/installation/repositories</c>.</para>
    /// </summary>
    [Fact]
    public void NoInstallationTokenStepAssertsOnTheRepositoryPermissionsObject()
    {
        var offenders = Directory
            .EnumerateFiles(WorkflowsDir(), "*.yml")
            .Select(path => (path, text: File.ReadAllText(path)))
            .Where(w =>
            {
                var lines = ExecutableLines(w.text);
                // Only lanes that actually mint an installation token can hit this.
                if (!lines.Any(l => l.Contains("create-github-app-token", StringComparison.Ordinal)))
                    return false;
                return lines.Any(l =>
                    l.Contains("repos/", StringComparison.Ordinal) &&
                    l.Contains(".permissions", StringComparison.Ordinal));
            })
            .Select(w => Path.GetFileName(w.path))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "These workflows mint a GitHub App installation token and then assert on "
            + $"repos/{{owner}}/{{repo}}'s .permissions object: {string.Join(", ", offenders)}.\n"
            + "That object reports the AUTHENTICATED USER's permissions. An installation token has no "
            + "user, so every field comes back false for a token that works — the assertion can never "
            + "pass, and it reports a broken credential while arming succeeds elsewhere in the same "
            + "run (measured on arm-credential.yml, runs 33605406624 and 33605578037).\n"
            + "Use /installation/repositories, which an installation token answers about itself, and "
            + "let the mint's explicit permission-* requests assert the permissions.");
    }

    /// <summary>
    /// 🚨 The same suppression, one step earlier: a workflow that OPENS a pull request must not do
    /// it with the default token either.
    ///
    /// <para>GitHub suppresses the <c>pull_request</c> event for anything <c>GITHUB_TOKEN</c>
    /// creates — the same recursion guard that swallowed the merges in #2916. A bot pull request
    /// opened that way gets <b>no check runs at all</b>, so on a repository whose branch protection
    /// requires contexts (MeshWeaver.Plugins requires five) it can never merge, and the state it
    /// presents is not "failed" but "not started yet", indefinitely.</para>
    ///
    /// <para><b>Why that is the worse half.</b> A failing automated bump is read and fixed. A bump
    /// that opens a pull request nobody's CI touches looks like work in flight, so the pile grows
    /// while the pin it exists to move stays exactly where it was — automation whose only effect is
    /// to make the lag harder to notice. <c>node-repo-platform-ref-bump.yml</c> shipped in that
    /// shape and was never called by any satellite, so it was never observed; it is called now
    /// (MeshWeaver.Plugins, MeshWeaver.SocialMedia), which is what makes this guard load-bearing
    /// rather than theoretical.</para>
    ///
    /// <para>Both spellings of the credential count. <c>${{ github.token }}</c> is the same token as
    /// <c>${{ secrets.GITHUB_TOKEN }}</c>, and the lane above used the first one.</para>
    /// </summary>
    [Fact]
    public void NoWorkflowOpensAPullRequestWithTheDefaultToken()
    {
        var offenders = Directory
            .EnumerateFiles(WorkflowsDir(), "*.yml")
            .Select(path => (path, blocks: StepBlocks(File.ReadAllText(path))))
            .Where(w =>
            {
                var opening = w.blocks
                    .Where(b => PullRequestOpeningCommands.Any(c => b.Contains(c, StringComparison.Ordinal)))
                    .ToArray();
                if (opening.Length == 0)
                    return false;
                // The step's own env, and the preamble — a job-wide `env:` reaches every step in it.
                return opening.Append(w.blocks[0])
                    .Any(b => ExecutableLines(b).Any(NamesTheDefaultToken));
            })
            .Select(w => Path.GetFileName(w.path))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"These workflows OPEN a pull request using the default token: {string.Join(", ", offenders)}.\n"
            + "GitHub does not raise a pull_request event for anything GITHUB_TOKEN creates, so the "
            + "resulting PR gets no check runs — and a repository that requires status contexts can "
            + "never merge it. It does not read as broken; it reads as 'checks have not started', "
            + "forever, while the pile of unmergeable bot PRs hides the very lag the automation was "
            + "added to end.\n"
            + "Mint a GitHub App installation token (actions/create-github-app-token, "
            + "permission-contents: write + permission-pull-requests: write), check out with it so "
            + "the push is the App's, and pass it as GH_TOKEN.");
    }

    /// <summary>
    /// The bump lane must name its minted token explicitly, for the same reason the arm lane must:
    /// deleting the <c>GH_TOKEN</c> line passes the check above while recreating the failure by
    /// omission, because <c>gh</c> falls back to whatever credential the runner exposes.
    /// </summary>
    [Fact]
    public void ThePlatformRefBumpLaneNamesAMintedInstallationTokenExplicitly()
    {
        var path = Path.Combine(WorkflowsDir(), "node-repo-platform-ref-bump.yml");
        Assert.True(File.Exists(path), $"{path} is missing — the bump lane is the subject of this guard.");

        var lines = ExecutableLines(File.ReadAllText(path));

        Assert.True(
            lines.Any(l => l.Contains("actions/create-github-app-token", StringComparison.Ordinal)),
            "node-repo-platform-ref-bump.yml no longer mints a GitHub App installation token. Its "
            + "pull requests are then opened by whatever identity remains, and if that is the "
            + "default token no CI runs on them at all.");

        Assert.True(
            lines.Any(l =>
                l.Contains("GH_TOKEN", StringComparison.Ordinal) &&
                l.Contains("steps.bump-token.outputs.token", StringComparison.Ordinal)),
            "node-repo-platform-ref-bump.yml's PR step does not pass the minted token as GH_TOKEN. "
            + "Without it gh falls back to the runner's default credential — the same suppressed "
            + "trigger, by omission rather than by choice.");

        Assert.True(
            lines.Any(l =>
                l.Contains("token:", StringComparison.Ordinal) &&
                l.Contains("steps.bump-token.outputs.token", StringComparison.Ordinal)),
            "node-repo-platform-ref-bump.yml checks out without the minted token, so the branch is "
            + "pushed with the default credential. The pull request would then be the App's and the "
            + "commit the default token's — and a push GITHUB_TOKEN made starts nothing either.");

        foreach (var permission in new[] { "permission-contents: write", "permission-pull-requests: write" })
        {
            Assert.True(
                lines.Any(l => l.Contains(permission, StringComparison.Ordinal)),
                $"node-repo-platform-ref-bump.yml's token mint no longer requests '{permission}'. "
                + "Pushing the bump branch needs Contents: write and opening the pull request needs "
                + "Pull requests: write; requesting both explicitly is what makes a missing grant "
                + "fail at the mint instead of somewhere downstream.");
        }

        Assert.True(
            lines.All(l => !l.Contains("continue-on-error", StringComparison.Ordinal)),
            "node-repo-platform-ref-bump.yml carries continue-on-error. Unlike auto-arm.yml — where "
            + "a tolerated mint failure costs one PR its arm and the assertion lives on in "
            + "arm-credential.yml — nothing else asserts this credential. A tolerated failure here "
            + "means the pin silently stops being bumped, which is the state the lane exists to end.");
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
