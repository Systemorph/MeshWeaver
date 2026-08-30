using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Governance ratchet for the unthrottled-resubscribe defect class (#2341): a test may not wait on
/// a mesh stream with Rx's argument-less <c>.Repeat()</c> / <c>.Retry()</c>.
///
/// <para><b>Why this is a defect and not a style preference.</b> Both operators re-subscribe to the
/// source the instant it terminates, with NO delay of their own. The delay people believe they have
/// added lives in a <c>.Catch(_ =&gt; Observable.Empty&lt;T&gt;().Delay(200.Milliseconds()))</c>
/// handler — and <c>Catch</c> intercepts <b>OnError only</b>. A point read of a node that does not
/// exist yet <b>completes without emitting</b>, so that handler never runs and the pair degenerates
/// into a busy-wait. Measured on the #2341 site: <b>460 000 re-subscribes in 8.4 s (~54 kHz), zero
/// OnError</b> — the 200 ms backoff never executed once.</para>
///
/// <para><b>And it costs the whole shard, not one test.</b> 🚨 Rx subscribes SYNCHRONOUSLY, so the
/// loop runs inside the assertion's own <c>.ToTask()</c> call and the <c>await</c> is never reached.
/// That puts it out of reach of every timeout the harness has — <c>.Within(...)</c>,
/// <c>[Fact(Timeout)]</c>, <c>methodTimeout</c>, and <c>MonolithMeshTestBase</c>'s hard-deadline
/// watchdog all need the test method to return or to park at an await. One xUnit worker pins a core
/// forever; on a 4-vCPU runner that also starves the hub that has to create the very node the loop
/// is waiting for, so it can never terminate. CI reports <c>exit=124 TIMEOUT</c> with no failing
/// test named and no <c>.trx</c> — 12 occurrences in ~15 h across five branches and <c>main</c>
/// (2026-08-25/26) before it was root-caused.
/// This is the same signature as <see cref="BlockingBridgeInTestRatchetGuard"/>'s defect class and
/// the reason that guard could not see this one: nothing here blocks, it spins.</para>
///
/// <para><b>The fix at a site</b> is the composition AGENTS.md prescribes for a node that may not
/// exist yet — the keyed <c>GetQuery</c> listing for EXISTENCE (empty-on-absent, live) and only then
/// the owner's stream for CONTENT:
/// <code>
/// hub.GetQuery(id, $"path:{parent} scope:children select:path")
///    .Where(nodes =&gt; nodes.Any(n =&gt; string.Equals(n.Path, target, StringComparison.OrdinalIgnoreCase)))
///    .Take(1)
///    .SelectMany(_ =&gt; workspace.GetMeshNodeStream(target))
///    .Select(n =&gt; n.ContentAs&lt;T&gt;(hub.JsonSerializerOptions))
///    .Should().Within(...).Match(predicate);
/// </code>
/// Where a retry genuinely belongs (an owner that can transiently reject), use
/// <c>.RetryWhen(errors =&gt; errors.Select((_, i) =&gt; i).TakeWhile(i =&gt; i &lt; n)
/// .SelectMany(_ =&gt; Observable.Timer(...)))</c> — it re-subscribes on OnError ONLY, carries its own
/// timer, and is bounded, so a completing source can never turn it into a spin.</para>
///
/// <para><b>The allow file is EMPTY, and must stay that way.</b> Unlike the blocking-bridge ratchet
/// there was no inventory to seed: the three sites that existed (<c>SubThreadHangRepro</c> ×1,
/// <c>SubThreadColdStartRepro</c> ×2 — all three waiting on a delegation sub-thread) are fixed in
/// the change that adds this guard, so the correct count is zero. Adding a line here is not a fix.</para>
/// </summary>
public class UnthrottledResubscribeInTestRatchetGuard(ITestOutputHelper output)
{
    /// <summary>
    /// <c>test/</c> only. <c>src/</c> has two deliberate <c>.Repeat()</c> sites over sources that
    /// carry their own cadence (<c>InstanceSyncWorker</c>'s sweep timer,
    /// <c>PackageInstaller</c>'s), where the operator is doing exactly what it says. The defect is
    /// specific to a TEST waiting on a mesh read, where the source can terminate immediately and
    /// nothing bounds the loop — and where the blast radius is the whole test host.
    /// </summary>
    private static readonly string[] ScannedRoots = ["test"];

    /// <summary>
    /// The argument-less spellings, as they appear in source. The overloads that TAKE an argument
    /// (<c>Repeat(count)</c>, <c>RetryWhen(handler)</c>, <c>Retry(count)</c>) are deliberately not
    /// matched: a bounded count terminates, and <c>RetryWhen</c> is the sanctioned replacement.
    /// </summary>
    private static readonly string[] Markers = [".Repeat()", ".Retry()"];

    private const string AllowFileName = "UnthrottledResubscribeSites.allow";

    [Fact]
    public void NoTestWaitsOnAMeshStreamWithAnUnthrottledResubscribe()
    {
        var root = SourceScan.FindRepoRoot();
        var allowed = SourceScan.ReadAllowFile(Path.Combine(root, "test", AllowFileName), AllowFileName);
        var found = Scan(root);

        var failures = new List<string>();

        foreach (var (file, count) in found.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!allowed.TryGetValue(file, out var budget))
                failures.Add(
                    $"  NEW SITE   {file} ({count}) — `.Repeat()` / `.Retry()` re-subscribes with no "
                    + "delay of its own, and `.Catch(...)` does NOT cover a source that COMPLETES. "
                    + "Gate on existence with GetQuery and read content from the owner's stream, or "
                    + "use `.RetryWhen(...)` with a timer. Do NOT add a line to " + AllowFileName + ".");
            else if (count > budget)
                failures.Add(
                    $"  MORE       {file} ({count} > {budget} allowed) — an unthrottled resubscribe "
                    + "was ADDED to a file that already carries the shape.");
        }

        foreach (var (file, budget) in allowed.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var count = found.GetValueOrDefault(file, 0);
            if (count < budget)
                output.WriteLine(
                    $"STALE (please tidy): {file} — {count} found, {budget} allowed. "
                    + $"{(count == 0 ? "Delete the line" : $"Lower it to {count}")}.");
        }

        Assert.True(failures.Count == 0,
            "An argument-less .Repeat()/.Retry() over a mesh read spins at tens of kHz the moment the "
            + "source completes instead of erroring, INSIDE the assertion's synchronous subscribe — so "
            + "no timeout in the harness can bound it and the whole test host dies at CI's 8 m cap "
            + "with `exit=124` and no test named (#2341).\n"
            + string.Join("\n", failures));
    }

    /// <summary>
    /// Non-vacuity, pinned in the same run. This ratchet's inventory is EMPTY, so the usual
    /// "the scanner found something" check would be vacuous — a scanner that matched nothing at all
    /// (a renamed marker, a masker that blanked every file) would pass exactly like a clean tree.
    /// So the scanner is exercised against a fixture written here: the shape in CODE must count, and
    /// the same shape quoted in a COMMENT or a string must not — which is load-bearing, because the
    /// guard's own remarks above quote <c>.Repeat()</c> verbatim several times.
    /// </summary>
    [Fact]
    public void TheScannerSeesTheShapeInCodeAndIgnoresItInProse()
    {
        const string fixture = """
            // a comment mentioning .Repeat() must NOT count
            /* nor a block comment with .Retry() in it */
            public class Sample
            {
                private const string Doc = "a string literal naming .Repeat() must not count";
                public void M() => Source.Where(x => x).Repeat();
            }
            """;

        Assert.Equal(1, CountMarkers(fixture));

        // And the guard's own file — which quotes both markers repeatedly in prose — must scan clean,
        // or every count this ratchet reports is measured against documentation rather than code.
        var root = SourceScan.FindRepoRoot();
        var self = Path.Combine(root, "test", "MeshWeaver.Documentation.Test",
            "UnthrottledResubscribeInTestRatchetGuard.cs");
        Assert.True(File.Exists(self), "the guard's own file must exist for this check to mean anything");
        Assert.Contains(".Repeat()", File.ReadAllText(self), StringComparison.Ordinal);
        Assert.Equal(0, CountSites(self));
    }

    private static Dictionary<string, int> Scan(string root) =>
        SourceScan.SourceFiles(root, ScannedRoots)
            .Select(f => (Relative: SourceScan.Relative(root, f), Count: CountSites(f)))
            .Where(x => x.Count > 0)
            .ToDictionary(x => x.Relative, x => x.Count, StringComparer.Ordinal);

    private static int CountSites(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (IOException) { return 0; } // a file a concurrent build is writing is not evidence

        return CountMarkers(text);
    }

    private static int CountMarkers(string text)
    {
        if (!Markers.Any(m => text.Contains(m, StringComparison.Ordinal))) return 0;

        var code = SourceScan.MaskCommentsAndStrings(text);
        var count = 0;
        foreach (var marker in Markers)
        {
            var at = 0;
            while ((at = code.IndexOf(marker, at, StringComparison.Ordinal)) >= 0)
            {
                count++;
                at += marker.Length;
            }
        }
        return count;
    }
}
