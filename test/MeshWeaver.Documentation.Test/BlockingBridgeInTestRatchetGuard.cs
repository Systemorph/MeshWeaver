using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Governance ratchet for the blocking-bridge defect class (#2013): a test that bridges an
/// observable — or a task — to a BLOCKING call may not appear at a new site.
///
/// <para><b>Why this is a defect and not a style preference.</b> <c>.ToEnumerable()</c>, Rx's
/// <c>IObservable&lt;T&gt;.Wait()</c>, <c>.Result</c> and <c>.GetAwaiter().GetResult()</c> all park the
/// calling thread on a semaphore until the source produces. When the source schedules onto that same
/// thread — a hub action block, a grain turn, xUnit's single-threaded
/// <c>MaxConcurrencySyncContext</c> — it self-deadlocks. Which thread it lands on depends on the
/// scheduler an operator picks, so it is INTERMITTENT: one local run passes with the offending test
/// present and the next two wedge for 31 and 15 minutes at ~0% CPU (#1991).</para>
///
/// <para><b>And it costs a whole shard, not one test.</b> 🚨 xUnit's <c>methodTimeout</c> cannot abort
/// a thread parked in a native wait. So instead of a 30 s test failure you get an unbounded host
/// wedge that CI reports as <c>exit=124 TIMEOUT</c> with NO failing test named and a marker whose own
/// guess ("likely fixture/init hang") points away from the cause. That signature was misattributed
/// twice in one day. Product code that deadlocks fails one request; a test that deadlocks takes the
/// shard's remaining tests with it and lies about why.</para>
///
/// <para><b>The fix at a site</b> is to <c>await</c> the stream
/// (<c>await source.Should().Emit()</c> / <c>.Match(...)</c>), which suspends the test rather than
/// parking its thread and therefore CANNOT self-deadlock. Where the assertion must stay
/// synchronous, subscribe and collect on <c>ImmediateScheduler</c> — the pattern
/// <c>PresentationScreenTest</c> moved to and documents.</para>
///
/// <para>🚨 <b>The fix is NOT an Rx-to-Task bridge.</b> This guard's remarks used to name
/// <c>.FirstAsync().ToTask(ct)</c> as the sanctioned test edge; that exemption is RETRACTED
/// (maintainer, 2026-08-30: <i>"no ToTask ever"</i>). It does not park a thread, so it is not the
/// defect THIS guard ratchets — but it resumes the awaiting test INLINE on the signalling thread,
/// inside Rx's trampoline, which is its own defect class and is ratcheted by
/// <see cref="ObservableToTaskBridgeGuard"/>. Trading one for the other is not a fix; where a wait
/// must produce a Task, it goes through
/// <c>MeshWeaver.Messaging.ReactiveCompletion.ObserveCompletion</c>.</para>
///
/// <para><b>Why the file is seeded rather than empty, and what the seeding measured.</b> #2013 counted
/// 37 <c>.Wait()</c> sites and asked for per-site judgement rather than a blanket rewrite. The
/// 2026-08-25 re-triage across all three markers found <c>.ToEnumerable()</c> at ZERO and THREE sites
/// that were the real thing — <c>CodeEditRecompileTest</c>, <c>CompileSourceSnapshotWedgeTest</c> and
/// <c>SourceDiscoveryUnavailableTest</c> each implemented <see cref="IDisposable"/> and blocked on
/// <c>base.DisposeAsync()</c>, parking the disposing thread on MESH TEARDOWN, outside
/// <c>methodTimeout</c>. Those are fixed in the same change that added this guard. Every remaining
/// <c>.Wait()</c> is over a source that has already completed before the wait begins
/// (<c>Observable.Return</c>, <c>Scheduler.Immediate</c>, an NSubstitute stub, a fake store) or is
/// bounded by an upstream <c>.Timeout(...)</c>, whose timer runs on the default scheduler and
/// therefore always unblocks the parked thread — a named failure, not a wedge. The
/// <c>GetAwaiter().GetResult()</c> setup helpers were left NOT individually cleared, flagged as the
/// first place to look if an <c>exit=124</c> with no named test recurred — and on 2026-08-26 it did,
/// in the very assembly hosting them (<c>MeshWeaver.Hosting.Monolith.Test</c>, run 32939560960). The
/// <c>MeshWeaver.Hosting.Monolith.Test</c>/<c>MeshWeaver.Persistence.Test</c> sites are fixed as of
/// that recurrence: <c>ScheduledPostWatcherTest</c>'s <c>StartAsync(default)</c> helper is now
/// <c>async Task&lt;ScheduledPostWatcher&gt;</c> and awaited from its (already-async) callers, and the
/// <c>persistence.SaveNode(...)</c> setup helpers — called from a synchronous
/// <c>ConfigureMesh(MeshBuilder)</c> override with no <c>await</c>-able call site — go through
/// <c>IStorageAdapterTestExtensions.SaveNodeSynchronously</c>, a direct <c>Subscribe()</c> with no
/// <c>Task</c>/<c>GetAwaiter</c>/<c>GetResult</c> bridge in between, so there is no native wait to
/// park on even in principle. The identical shape in
/// <c>MeshWeaver.Hosting.Orleans.Test</c>'s <c>OrleansScheduledPostTest</c> /
/// <c>OrleansEventSubscriptionTimerTest</c> is the SAME defect and the SAME fix, deliberately left
/// for a follow-up — that project is scope-excluded from this pass while a concurrent session moves
/// files out of it (see the allow file's entry for the reason an edit there is unsafe right now).
/// </para>
///
/// <para><b>The ratchet may only SHRINK.</b> A new file, a raised count, or a raised TOTAL is a
/// failure. A line that has become stale (its site was fixed) is reported, not failed: two PRs
/// closing sites concurrently would otherwise red <c>main</c> on whichever merged second, and a gate
/// that punishes the direction it is asking for teaches people to stop shrinking. Delete the stale
/// line and lower <see cref="TotalBudget"/> in the same change.</para>
/// </summary>
public class BlockingBridgeInTestRatchetGuard(ITestOutputHelper output)
{
    /// <summary>
    /// The seeded inventory's size. Per-file entries stop a new site in a file that already carries
    /// the shape; this stops the list as a WHOLE from growing — including by the trick of adding a
    /// new file's line. Lower it whenever you delete or lower an entry.
    /// </summary>
    private const int TotalBudget = 26;

    /// <summary>
    /// <c>test/</c> only — this guard exists because tests were treated as exempt from the
    /// no-blocking rule, and the consequence there is WORSE, not milder: a test that self-deadlocks
    /// takes its whole shard with it and reports <c>exit=124</c> with no failing test named.
    ///
    /// <para>🚨 CORRECTED 2026-08-30. This remark used to say <c>src/</c> was covered "by the
    /// harder rule AGENTS.md already states for product code … and by the reviews that enforce it".
    /// <b>Reviews are not a gate</b>, and the production trees in fact carried 9 unguarded blocking
    /// sites across 5 files. They are now scanned — by
    /// <see cref="ObservableToTaskBridgeGuard.NoNewBlockingBridgeInProductionCode"/>, against
    /// <c>test/ProductionBlockingBridgeSites.allow</c>, using the same two markers. The two guards
    /// are split by ROOT rather than by marker so each keeps one budget over one tree; do not widen
    /// this array to production roots or the same sites would be counted against two budgets.</para>
    /// </summary>
    private static readonly string[] ScannedRoots = ["test"];

    /// <summary>
    /// The bridges, as they appear in source.
    ///
    /// <para>🚨 <c>.Result</c> is deliberately NOT here. It is overwhelmingly a domain property in
    /// this repo — <c>ToolCall.Result</c>, <c>PatchResult.Result</c> — and a marker that matches a
    /// hundred innocent reads to catch one bridge would be ignored, which is how a ratchet dies. The
    /// <c>.GetAwaiter().GetResult()</c> spelling below IS unambiguous and is matched.</para>
    /// </summary>
    private static readonly string[] Markers =
        [".ToEnumerable()", ".Wait()", ".GetAwaiter().GetResult()"];

    private const string AllowFileName = "BlockingBridgeSites.allow";

    [Fact]
    public void NoNewTestBridgesAnObservableToABlockingWait()
    {
        var root = SourceScan.FindRepoRoot();
        var allowed = SourceScan.ReadAllowFile(Path.Combine(root, "test", AllowFileName), AllowFileName);
        var found = Scan(root);

        var failures = new List<string>();

        foreach (var (file, count) in found.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!allowed.TryGetValue(file, out var budget))
                failures.Add(
                    $"  NEW SITE   {file} ({count}) — await the stream instead "
                    + "(`await source.Should().Emit()` / `.FirstAsync().Await(ct)`), or subscribe and "
                    + "collect on ImmediateScheduler. Do NOT add a line to " + AllowFileName + ", and do NOT "
                    + "swap it for an Rx-to-Task bridge (see ObservableToTaskBridgeGuard).");
            else if (count > budget)
                failures.Add(
                    $"  MORE       {file} ({count} > {budget} allowed) — a bridge was ADDED to a file "
                    + "that already carries the shape.");
        }

        var total = allowed.Values.Sum();
        if (total > TotalBudget)
            failures.Add(
                $"  TOTAL      {total} allowances > {TotalBudget} budgeted — the inventory GREW. "
                + "Adding a line to " + AllowFileName + " is not a fix.");

        foreach (var (file, budget) in allowed.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var count = found.GetValueOrDefault(file, 0);
            if (count < budget)
                output.WriteLine(
                    $"STALE (please tidy): {file} — {count} found, {budget} allowed. "
                    + $"{(count == 0 ? "Delete the line" : $"Lower it to {count}")} and lower "
                    + $"TotalBudget by {budget - count}.");
        }

        Assert.True(failures.Count == 0,
            "A blocking bridge in a test parks the calling thread in a native wait that xUnit's "
            + "methodTimeout CANNOT abort, so a self-deadlock costs the whole shard and reports "
            + "`exit=124 TIMEOUT` with no test named (#2013). Await the stream instead.\n"
            + string.Join("\n", failures));
    }

    /// <summary>
    /// Non-vacuity, pinned in the same run: the scanner must actually SEE the shape. The seeded allow
    /// file is non-empty, so a scanner that silently matched nothing — a renamed marker, a masking bug
    /// that blanked every file — would report every entry as STALE rather than pass; this states the
    /// expectation directly so the failure names the scanner instead of the tree.
    /// </summary>
    [Fact]
    public void TheScannerFindsTheShapeItIsRatcheting()
    {
        var root = SourceScan.FindRepoRoot();
        var found = Scan(root);

        Assert.True(found.Count > 0,
            "The scanner found no blocking bridge anywhere under " + string.Join(", ", ScannedRoots)
            + ". Either every site was migrated — in which case empty " + AllowFileName
            + " and delete this assertion — or the scanner is broken, which would make the ratchet "
            + "above pass on no evidence.");

        // Several tests EXPLAIN in a comment why they no longer use a bridge, quoting the shape
        // verbatim (PresentationScreenTest, SyncedQueryPgTest, ThreadStreamingIdentityTest). A
        // scanner that counted those would ratchet against prose, so prove the masking works.
        var explained = Path.Combine(root, "test", "MeshWeaver.Graph.Test", "PresentationScreenTest.cs");
        Assert.True(File.Exists(explained), "the quoting file must exist for this check to mean anything");
        Assert.Contains(".ToEnumerable()", File.ReadAllText(explained), StringComparison.Ordinal);
        Assert.False(found.ContainsKey("test/MeshWeaver.Graph.Test/PresentationScreenTest.cs"),
            "the scanner counted a COMMENT as a call site — comment masking is broken, and every "
            + "count in the allow file is therefore unreliable.");
    }

    private static Dictionary<string, int> Scan(string root) =>
        SourceScan.SourceFiles(root, ScannedRoots)
            .Select(f => (Relative: SourceScan.Relative(root, f), Count: CountSites(f)))
            .Where(x => x.Count > 0)
            .ToDictionary(x => x.Relative, x => x.Count, StringComparer.Ordinal);

    /// <summary>Occurrences of any <see cref="Markers"/> entry, with comments and string literals
    /// masked first.</summary>
    private static int CountSites(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (IOException) { return 0; } // a file a concurrent build is writing is not evidence

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

        // `.GetAwaiter().GetResult()` also contains no other marker, but `.Wait()` never overlaps
        // either — so no double counting to correct for.
        return count;
    }
}
