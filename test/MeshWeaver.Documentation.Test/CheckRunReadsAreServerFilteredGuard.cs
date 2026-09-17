using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 <b>A read of <c>commits/&lt;sha&gt;/check-runs</c> must name the check it wants
/// (<c>check_name=</c>), never filter one PAGE of them client-side</b> (#4526).
///
/// <para><b>What went wrong.</b> CD's <c>gate</c> decides which commit to deliver by asking whether
/// <c>Consolidate test results</c> — this repository's one required check — is green on it. It read
/// <c>commits/$sha/check-runs?per_page=100</c> and picked the check out of the answer with
/// <c>jq</c>. That endpoint PAGES at 100, and a main commit here collects far more than 100
/// check-runs: every workflow, every matrix leg, both synthetic probes, the combo verdict, the
/// alert jobs. Measured 2026-09-17 on <c>be8f452c79</c>, main's tip: <b>278</b> check-runs, of which
/// page 1 held <b>zero</b> named <c>Consolidate test results</c> and page 3 held its two, both
/// <c>completed/success</c> since 22:41Z the previous evening.</para>
///
/// <para><b>What it cost.</b> The gate logged <c>candidate be8f452 → absent/none</c> and walked back
/// to the tip's PARENT, saying "the tip ships on a later tick" — which never came, because the
/// truncation does not heal: a busy commit only accumulates more check-runs. Six hours of hourly
/// reconciles targeted the parent, spent its 3-attempt heal budget, and CD then reported delivery
/// STUCK with main green and ten merges unpublished.</para>
///
/// <para><b>Why a text guard.</b> The defect is invisible to every other instrument: the API answers
/// 200, the jq filter is correct, the read simply cannot see the row it is filtering for. Nothing
/// in an assembly models it, and a run only shows it once a commit crosses 100 check-runs — which
/// is a property of the repository's check volume, not of the change under test. A bigger
/// <c>per_page</c> is not the fix (100 is the maximum); asking the API for the check by name is.</para>
/// </summary>
public class CheckRunReadsAreServerFilteredGuard
{
    /// <summary>
    /// Every <c>check-runs</c> read in a workflow carries <c>check_name=</c> (or pages the answer
    /// with <c>--paginate</c>), so the row it needs cannot be truncated away.
    /// </summary>
    [Fact]
    public void EveryCheckRunReadNamesTheCheckOrPages()
    {
        var offenders = CheckRunReads()
            .Where(r => !r.Url.Contains("check_name=", StringComparison.Ordinal)
                        && !r.PagesExplicitly)
            .ToList();

        Assert.True(offenders.Count == 0,
            "a workflow reads commits/<sha>/check-runs without naming the check:\n  "
            + string.Join("\n  ", offenders.Select(o => $"{o.File}:{o.Line}: {o.Url}"))
            + "\nThat endpoint pages at 100 and a main commit here carries ~280 check-runs, so the "
            + "row the reader filters for in jq may not be on the page at all — measured on "
            + "be8f452c79, where `Consolidate test results` sat on page 3 and CD read `absent/none` "
            + "for a green commit and stopped delivering (#4526). Add "
            + "`&check_name=<the check>` so the API does the filtering, or `--paginate` if the "
            + "reader genuinely wants every check-run.");
    }

    /// <summary>
    /// 🚨 Discovery found something. A renamed endpoint or a reshaped call would empty the set and
    /// leave the assertion above passing while it examined nothing — the failure this repository
    /// meets most often. CD's own gate makes two such reads (the candidate walk and the target's
    /// required check), so two is the floor.
    /// </summary>
    [Fact]
    public void TheWorkflowsActuallyYieldCheckRunReads()
    {
        var reads = CheckRunReads();

        Assert.True(reads.Count >= 2,
            $"Expected at least CD's two check-run reads; found {reads.Count}. Either they were "
            + "removed or this matcher no longer recognises them — in both cases this guard "
            + "verifies nothing.");
    }

    private sealed record Read(string File, int Line, string Url, bool PagesExplicitly);

    /// <summary>
    /// Every <c>commits/&lt;sha&gt;/check-runs…</c> URL in <c>.github/workflows</c>, with the line
    /// it sits on and whether that same line asks for pagination. Comment lines are skipped: a
    /// comment quoting the bad shape is documentation, not a read.
    /// </summary>
    private static IReadOnlyList<Read> CheckRunReads()
    {
        var root = Path.Combine(FindRepoRoot(), ".github", "workflows");
        var reads = new List<Read>();

        foreach (var file in Directory.EnumerateFiles(root, "*.yml", SearchOption.TopDirectoryOnly).OrderBy(f => f))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (Regex.IsMatch(lines[i], @"^\s*#")) continue;
                var m = Regex.Match(lines[i], @"commits/[^""'\s]*/check-runs[^""'\s]*");
                if (!m.Success) continue;

                reads.Add(new Read(
                    Path.GetFileName(file),
                    i + 1,
                    m.Value,
                    lines[i].Contains("--paginate", StringComparison.Ordinal)));
            }
        }

        return reads;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        return dir?.FullName
               ?? throw new InvalidOperationException("Could not locate the repository root (no MeshWeaver.slnx above the test binary).");
    }
}
