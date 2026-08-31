#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 Every bounded wait in <c>PackageInstaller</c> must NAME ITSELF when it expires.
///
/// <para>The installer carries eight bounds — warm, gating settle, declared-access verify, root
/// recycle, root-type settle, root ready, root-type probe, and the per-type seed bound. They are
/// ceilings, not sleeps: none costs anything when the work it bounds completes. So an install that
/// writes 31 MB in under a second and then sits idle for minutes means **one of them expired**, and
/// which one is the entire diagnosis (#2446).</para>
///
/// <para><b>Seven of the eight said so. One did not.</b> <c>RootTeardownSettled</c> ended
/// <c>.Timeout(RootRecycleTimeout).Catch(_ =&gt; Observable.Return(Unit.Default))</c> — continuing
/// is right, but it continued in silence, so a wedged root teardown cost thirty seconds that
/// appeared in no log. Its own remark says the bound "is only ever reached when a hub's teardown
/// itself wedges", which makes expiry the single most interesting event on that path and the one
/// thing it never reported.</para>
///
/// <para>This is the <c>AGENTS.md</c> swallow-and-continue shape narrowly applied: the recovery is
/// correct, the silence is not. A wait nobody can see is a wait nobody can fix — and a post-hoc log
/// read, which is the only way this class of stall gets diagnosed on a live portal, is incomplete
/// for exactly as long as one bound stays quiet.</para>
/// </summary>
public class InstallerBoundsNameThemselvesGuard
{
    private const string Subject = "src/MeshWeaver.PluginCatalog/PackageInstaller.cs";

    /// <summary>How far after a `.Timeout(...)` the expiry's own report may sit.</summary>
    private const int Window = 20;

    private static string[] SubjectLines() =>
        File.ReadAllLines(Path.Combine(FindRepoRoot(), Subject.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>`.Timeout(X)` sites where X is a named bound, not a literal or a parameter.</summary>
    private static List<(int Line, string Bound)> BoundedWaits(string[] lines)
    {
        var found = new List<(int, string)>();
        for (var i = 0; i < lines.Length; i++)
        {
            var m = Regex.Match(lines[i], @"\.Timeout\(\s*([A-Za-z_][A-Za-z0-9_]*)\s*\)");
            if (m.Success) found.Add((i, m.Groups[1].Value));
        }
        return found;
    }

    private static bool ReportsItsExpiry(string[] lines, int at) =>
        string.Join('\n', lines.Skip(at).Take(Window)).Contains("Log", StringComparison.Ordinal);

    [Fact]
    public void EveryBoundedWait_ReportsItsOwnExpiry()
    {
        var lines = SubjectLines();
        var silent = BoundedWaits(lines)
            .Where(w => !ReportsItsExpiry(lines, w.Line))
            .Select(w => $"{Subject}:{w.Line + 1} — .Timeout({w.Bound}) expires without a log")
            .ToList();

        Assert.True(silent.Count == 0,
            "a bounded wait that expires in silence costs the install its full budget and appears "
            + "in no log, so the stall it causes cannot be attributed afterwards (#2446). Continue "
            + "if continuing is right — but say so, naming the bound and what it was waiting for:\n  "
            + string.Join("\n  ", silent));
    }

    /// <summary>
    /// 🚨 The scanner must actually find the sites. A regex that matched nothing would satisfy the
    /// test above vacuously — which is the same defect the test exists to prevent, one level up.
    ///
    /// <para>🚨 It asserts a COUNT and a SHAPE, never specific identifiers. The first version listed
    /// three bound names, and CI reddened it within the hour: #2849 had renamed
    /// <c>GatingSettleTimeout</c> to <c>GatingDetectorBudget</c> while this branch was being written,
    /// so a guard about *silence on expiry* failed for a reason that had nothing to do with silence.
    /// A guard naming a symbol inherits every rename of that symbol as a false failure — and a guard
    /// that cries wolf gets deleted, taking the real check with it. Bind to the property, not the
    /// name.</para>
    /// </summary>
    [Fact]
    public void TheScannerSeesTheBoundsItClaimsTo()
    {
        var waits = BoundedWaits(SubjectLines());

        Assert.True(waits.Count >= 6,
            $"expected the installer's bounded waits to be found; saw {waits.Count}. A scanner that "
            + "matches nothing makes the guard above pass on an empty set.");

        // The SHAPE of a bound's name, which survives renaming: these are budget-ish identifiers,
        // not literals and not locals. If a future bound is a bare TimeSpan literal the regex will
        // not see it at all, and the count assertion above is what notices.
        var odd = waits
            .Where(w => !(w.Bound.EndsWith("Timeout", StringComparison.Ordinal)
                          || w.Bound.EndsWith("Budget", StringComparison.Ordinal)
                          || w.Bound.EndsWith("Bound", StringComparison.Ordinal)
                          || w.Bound is "bound"))
            .Select(w => $"{Subject}:{w.Line + 1} — .Timeout({w.Bound})")
            .ToList();

        Assert.True(odd.Count == 0,
            "every bounded wait should name a budget-shaped identifier, so the scanner is provably "
            + "matching bounds rather than incidental tokens:\n  " + string.Join("\n  ", odd));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".github")))
            dir = dir.Parent;
        Assert.True(dir is not null, "could not locate the repository root");
        return dir!.FullName;
    }
}
