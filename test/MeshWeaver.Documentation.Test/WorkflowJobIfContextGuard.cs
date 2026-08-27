using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 A job-level <c>if:</c> may only read the contexts GitHub makes available there —
/// <c>github</c>, <c>inputs</c>, <c>needs</c>, <c>vars</c>. <b><c>matrix</c> is NOT one of them.</b>
///
/// <para>This is not a style rule, and getting it wrong does not produce a leg that skips. It
/// produces an <b>invalid workflow</b>, and GitHub answers an invalid workflow with a <i>startup
/// failure</i>: a run with zero jobs, zero steps, no log to read, and a conclusion of plain
/// <c>failure</c> — indistinguishable at a glance from an ordinary red. On a feature branch that
/// costs a confusing check. On <c>main-cd.yml</c> it costs <b>delivery</b>: Continuous Delivery
/// refuses to start at all, so no portal image, no migration image, no bake and no publication
/// happen for every commit until someone notices and reverts.</para>
///
/// <para>It is an easy mistake to make because the shape reads as obviously correct — "run this leg
/// only when it is the amd64 one" — and because <c>matrix</c> IS available in the neighbouring
/// keys: <c>runs-on</c>, <c>continue-on-error</c>, <c>timeout-minutes</c>, <c>name</c>, <c>env</c>
/// and every step. It was written exactly that way in the per-arch bake lane and only surfaced as
/// a nameless red suite on the branch.</para>
///
/// <para><b>What to do instead:</b> select the legs when the matrix is BUILT, not after. Compose
/// the list in an upstream job and consume it as
/// <c>matrix: { include: ${{ fromJSON(needs.&lt;job&gt;.outputs.&lt;list&gt;) }} }</c> — which is
/// also strictly better than a skip, because a leg that was never created cannot contribute a grey
/// tick to an aggregate <c>needs.&lt;job&gt;.result</c> that something downstream reads as
/// evidence.</para>
/// </summary>
public class WorkflowJobIfContextGuard
{
    [Fact]
    public void NoJobLevelIf_ReadsTheMatrixContext()
    {
        var workflows = Path.Combine(FindRepoRoot(), ".github", "workflows");
        Assert.True(Directory.Exists(workflows), $"expected {workflows} to exist");

        var offenders = Directory
            .EnumerateFiles(workflows, "*.yml", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.Ordinal)
            .SelectMany(f => JobLevelIfExpressions(File.ReadAllLines(f))
                .Where(e => e.Text.Contains("matrix.", StringComparison.Ordinal)
                            || e.Text.Contains("matrix[", StringComparison.Ordinal))
                .Select(e => $"{Path.GetFileName(f)}:{e.Line} — {e.Text.Trim()}"))
            .ToList();

        Assert.True(offenders.Count == 0,
            "A job-level `if:` cannot read the `matrix` context — GitHub allows only `github`, "
            + "`inputs`, `needs` and `vars` there, and rejects the WHOLE WORKFLOW with a startup "
            + "failure (zero jobs, zero steps, no log) rather than evaluating the condition. On "
            + "main-cd.yml that means nothing is built or published at all until it is reverted. "
            + "Select the legs where the matrix is BUILT instead — compose the list in an upstream "
            + "job and consume it as `include: ${{ fromJSON(needs.<job>.outputs.<list>) }}`. "
            + "Offending job-level conditions:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// Every job-level <c>if:</c> in the file, with its 1-based line number and full value —
    /// folded/literal block scalars included, since that is how the long ones are written.
    /// </summary>
    /// <remarks>
    /// Only the region after the top-level <c>jobs:</c> key is scanned: <c>on:</c> has two-space
    /// keys of its own (<c>push:</c>, <c>workflow_run:</c>) and nothing there is a job. A job-level
    /// key sits at exactly FOUR spaces — a step's <c>if:</c> is deeper (a step is <c>      - </c>),
    /// so the indent alone separates the two without needing a YAML parser.
    /// </remarks>
    private static IEnumerable<(int Line, string Text)> JobLevelIfExpressions(string[] lines)
    {
        var start = Array.FindIndex(lines, l => l.Equals("jobs:", StringComparison.Ordinal));
        if (start < 0)
            yield break;

        for (var i = start + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!line.StartsWith("    if:", StringComparison.Ordinal))
                continue;

            var text = line["    if:".Length..];
            // A block scalar (`>-`, `|`) carries its expression on the following, more-indented
            // lines. Take them all: the `matrix.` reference is as fatal on a continuation line as
            // it is inline, and a guard that only read the first line would miss the exact shape
            // this exists to catch.
            for (var j = i + 1; j < lines.Length; j++)
            {
                var next = lines[j];
                if (next.Length == 0)
                    break;
                var indent = next.Length - next.TrimStart().Length;
                if (indent <= 4)
                    break;
                text += " " + next.Trim();
            }
            yield return (i + 1, text);
        }
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
