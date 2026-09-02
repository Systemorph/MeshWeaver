#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 A <c>workflow_run</c>-triggered workflow that also declares a <c>concurrency</c> group must
/// filter the trigger with <c>branches:</c>.
///
/// <para><b>Why the two conditions together.</b> A <c>workflow_run</c> trigger fires on every
/// completion of the named workflow on ANY branch — a pull request's build included. On its own
/// that is often harmless and sometimes wanted (<c>retry-known-transients.yml</c> deliberately
/// retries transients on PR builds too, and declares no concurrency group, so it evicts nothing).
/// It stops being harmless the moment the workflow shares a concurrency group: GitHub keeps exactly
/// ONE pending run per group, so each arrival CANCELS the pending one. An off-branch trigger then
/// evicts an on-branch run that was waiting its turn.</para>
///
/// <para><b>The measured outage (2026-09-02).</b> <c>main-cd.yml</c> had no <c>branches:</c> filter.
/// Between 07:35 and 07:43, six CD runs were created and every one was cancelled — four from pull
/// request builds, two from real pushes to main:</para>
/// <code>
/// 07:35 PR fix/arm-red-is-repo-scoped          -> CD run, cancelled
/// 07:37 PR ci/hard-cap-every-job-at-45-minutes -> CD run, cancelled
/// 07:39 main push                              -> CD run, cancelled   &lt;- real delivery
/// 07:40 PR fix/oversized-pod-hub-delivery      -> CD run, cancelled
/// 07:41 PR fix/3022-identity-fork-remainder    -> CD run, cancelled
/// 07:43 main push                              -> CD run, cancelled   &lt;- real delivery
/// </code>
/// <para>Nothing was promoted for over four hours while every dashboard stayed green: the delivery
/// runs existed, they were simply never allowed to finish. <b>The failure scales with pull-request
/// throughput</b>, which is the worst possible shape — delivery stops hardest exactly when the repo
/// is busiest, and it presents as "CD is slow today" rather than as a defect.</para>
///
/// <para><b>Why review cannot catch it.</b> A CD run started by a PR build is indistinguishable
/// from a real one in the run list: for a <c>workflow_run</c> event the run's own
/// <c>head_branch</c> is the workflow FILE's ref, i.e. always the default branch. The triggering
/// branch survives only in the trigger filter itself.</para>
/// </summary>
public class WorkflowRunTriggerBranchFilterGuard
{
    private static string WorkflowsDir() => Path.Combine(FindRepoRoot(), ".github", "workflows");

    private static string[] ExecutableLines(string text) =>
        text.Split('\n')
            .Where(l => !l.TrimStart().StartsWith("#", StringComparison.Ordinal))
            .ToArray();

    /// <summary>
    /// The <c>workflow_run:</c> block — from that key to the next key at the same indentation.
    /// Returns null when the workflow has no such trigger.
    /// </summary>
    private static string[]? WorkflowRunBlock(string[] lines)
    {
        var start = Array.FindIndex(lines, l => l.TrimEnd() == "  workflow_run:");
        if (start < 0) return null;

        var block = new List<string>();
        for (var i = start + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Trim().Length == 0) continue;
            // A key at the same indent (two spaces, not more) ends the block.
            var indent = line.Length - line.TrimStart().Length;
            if (indent <= 2) break;
            block.Add(line);
        }
        return block.ToArray();
    }

    private static bool DeclaresConcurrency(string[] lines) =>
        lines.Any(l => l.TrimEnd() == "concurrency:");

    [Fact]
    public void AWorkflowRunTriggerSharingAConcurrencyGroupFiltersItsBranches()
    {
        var offenders = new List<string>();
        var examined = 0;

        foreach (var path in Directory.EnumerateFiles(WorkflowsDir(), "*.yml").OrderBy(p => p, StringComparer.Ordinal))
        {
            var lines = ExecutableLines(File.ReadAllText(path));
            var block = WorkflowRunBlock(lines);
            if (block is null) continue;

            examined++;
            if (!DeclaresConcurrency(lines)) continue;   // evicts nothing; an unfiltered trigger is safe here
            if (block.Any(l => l.TrimStart().StartsWith("branches:", StringComparison.Ordinal))) continue;

            offenders.Add(Path.GetFileName(path));
        }

        // 🚨 Control arm. If the block matcher stops recognising `workflow_run:` — a reformat, a
        // move to a different quoting style — this test would pass having examined nothing, which
        // is the failure mode it exists to prevent one level up.
        Assert.True(examined > 0,
            "No workflow_run trigger was found in .github/workflows. Either every one was removed, "
            + "or the block matcher no longer recognises them — in the second case this guard checks "
            + "nothing while reporting green.");

        Assert.True(
            offenders.Count == 0,
            $"These workflows declare a concurrency group AND a workflow_run trigger with no "
            + $"`branches:` filter: {string.Join(", ", offenders)}.\n"
            + "workflow_run fires on completions from EVERY branch, including pull-request builds. "
            + "GitHub keeps exactly one pending run per concurrency group, so each arrival cancels "
            + "the pending one — an off-branch trigger evicts an on-branch run that was waiting.\n"
            + "Measured on main-cd.yml (2026-09-02): six consecutive CD runs cancelled, four of them "
            + "started by PR builds, and nothing promoted for over four hours while every dashboard "
            + "was green. The failure scales with PR throughput, and a PR-started run is "
            + "indistinguishable from a real one in the run list (head_branch is the workflow file's "
            + "ref, always the default branch).\n"
            + "Add `branches: [main]` to the workflow_run trigger.");
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
