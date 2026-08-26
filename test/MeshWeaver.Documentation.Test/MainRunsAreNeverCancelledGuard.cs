using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 <b>A run on <c>main</c> must never be cancelled by the next merge</b> (#2412).
///
/// <para><b>Why this is correctness, not runner-minute tuning.</b> Cancelling a superseded run is
/// the right default on a PR branch — the later push tests a strict successor of what was killed,
/// and that is where #2316's ~28% of runner demand is saved. On <c>main</c> the same setting
/// destroys two different things, and neither is recoverable by re-running later:</para>
///
/// <list type="number">
///   <item><description><b>Nothing builds the combination that LANDED.</b> The repo runs
///   <c>strict: false</c> branch protection, so each PR is tested against the <c>main</c> it
///   branched from — the MERGED tree is first compiled by main's own run. Cancel that and a
///   semantic conflict between two independently-green PRs ships unbuilt. On 2026-08-26 five
///   merges inside fifteen seconds put <c>CS0246: 'MeshOperations' could not be found</c> on
///   main exactly this way.</description></item>
///   <item><description><b>Nothing publishes.</b> <c>main-cd.yml</c> gates delivery on the
///   required check <c>Consolidate test results</c> reaching <c>success</c> FOR THAT SHA. CD does
///   still wake on a cancelled run — it subscribes with <c>types: [completed]</c> and cancelled
///   counts as completed — it simply finds no success to act on. A burst of merges therefore
///   publishes nothing, leaving the tail to the hourly reconciler.</description></item>
/// </list>
///
/// <para>Both failures are silent in the same way the rest of this repo's worst bugs are: a
/// cancelled run and a run that was never needed look identical in the runs list, so "main is
/// quiet" reads as healthy. Measured the day the guard was written, main's five consecutive runs
/// between 20:28 and 20:38 were ALL <c>cancelled</c>, each by the next merge.</para>
///
/// <para>🚨 This is a text guard because the invariant lives in a GitHub Actions expression, where
/// no unit test can observe it — the only way it can regress is someone editing the line back to
/// save minutes, which is precisely what a comment cannot prevent.</para>
/// </summary>
public class MainRunsAreNeverCancelledGuard
{
    private const string Workflow = ".github/workflows/dotnet-test.yml";

    /// <summary>
    /// The assertion is on the EXPRESSION, not on a byte-exact line: any form that excludes
    /// <c>refs/heads/main</c> from cancellation passes, and a bare <c>true</c> — or an expression
    /// that no longer mentions the ref at all — fails.
    /// </summary>
    [Fact]
    public void BuildAndTest_NeverCancelsAnInProgressRunOnMain()
    {
        var path = Path.Combine(FindRepoRoot(), Workflow);
        var body = File.ReadAllText(path);

        var cancel = Regex.Match(body, @"^\s*cancel-in-progress:\s*(?<value>.+)$", RegexOptions.Multiline);

        Assert.True(cancel.Success,
            $"{Workflow} no longer declares cancel-in-progress. If concurrency was removed entirely "
            + "that is fine for main, but this guard can no longer see it — re-point or delete it "
            + "deliberately rather than letting it rot.");

        var value = cancel.Groups["value"].Value.Trim();

        Assert.False(value is "true" or "\"true\"" or "'true'",
            $"{Workflow} sets cancel-in-progress: true unconditionally, so every merge to main "
            + "cancels the run for the merge before it. Nothing then compiles the tree that landed "
            + "(strict: false means the merged combination is first built by main's own run — this "
            + "is how CS0246 reached main on 2026-08-26), and nothing publishes (CD gates on "
            + "'Consolidate test results' reaching success for that SHA, which a cancelled run "
            + "never produces). See #2412.");

        Assert.True(value.Contains("refs/heads/main", StringComparison.Ordinal),
            $"{Workflow}'s cancel-in-progress expression no longer excludes refs/heads/main: "
            + $"'{value}'. Superseding is correct on a PR branch and wrong on main — a cancelled "
            + "main run loses both the compile of the combination that shipped and the ability to "
            + "ship it (#2412). Keep the ref check, or state in the expression why it is safe.");
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
