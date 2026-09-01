using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Governance ratchet for the un-joined-disposal defect class: in a TEST BASE, an xUnit FIXTURE or
/// a RUN HARNESS, a hub may not be disposed without joining its teardown before the caller
/// proceeds.
///
/// <para><b>Why this is a crash and not a leak.</b> <c>IMessageHub.Dispose()</c> STARTS the disposal
/// state machine and returns immediately — the action blocks drain, the hosted hubs tear down, the
/// sync streams unregister and the registrants run AFTERWARDS, on other threads. A harness that
/// disposes and proceeds is running concurrently with all of that, and the very next thing it does
/// is always one of the three that live teardown cannot survive: dispose the service scope (a
/// continuation resolves from a dead Autofac scope), unload a collectible node ALC (a live thread
/// dereferences freed metadata — a native use-after-unload <b>SIGSEGV</b>), or return to xUnit so the
/// next test class starts on top of the previous one's teardown.</para>
///
/// <para><b>What it looks like when it fires.</b> Not a failing test — a dead process. MeshWeaver.Plugins
/// run 33236823482, job 99060770662: the plugin gate exited <b>139</b> mid-run while installing
/// packages, immediately after a burst of <c>[SYNC_STREAM] Not setting … — stream is disposed</c> and
/// <c>Stream …: resubscribe failed … TargetInvocationException</c>. The gate's FINAL teardown already
/// joined the mesh; what did not join was the render CLIENT disposed two statements earlier. That is
/// the shape this guard exists to stop reappearing: the join is present nearby, on a different hub,
/// which is why a reviewer reads the file and sees discipline.</para>
///
/// <para><b>The fix at a site</b> is <c>MeshWeaver.Messaging.HubDisposalJoin</c>:
/// <c>await hub.DisposeAndJoinAsync(report)</c> from an <c>async</c> teardown (it SUSPENDS the caller
/// rather than parking its thread, so it cannot self-deadlock against the hub's own scheduler), or
/// <c>hub.DisposeAndJoin(report)</c> at a genuinely synchronous run boundary. Both are bounded and
/// both SAY it when the bound is hit — a silent hang is not an improvement over a crash. For a MESH
/// ROOT prefer <c>MeshTeardownExtensions.TeardownAsync</c> / <c>WaitForDisposalAndIoDrainAsync</c>,
/// which additionally cancel+join the <c>IIoPool</c> leaves and quiesce the
/// <c>AsyncDisposeQueue</c>.</para>
///
/// <para><b>Why the allow file is EMPTY, and must stay that way.</b> Every site in the covered
/// surface was fixed in the change that added this guard — there were exactly four
/// (<c>MonolithMeshTestBase.DisposeTestClients</c>, <c>OrleansMeshTestBase.DisposeAsync</c>,
/// <c>SharedOrleansFixture.CleanupSiloHubsWithPrefix</c>, <c>PluginGateRunner</c>'s render client).
/// An allow file seeded with today's debt makes the debt permanent, so this one starts at zero and
/// may only ever be at zero: <b>adding a line to it is not a fix</b>, and the total budget below is
/// nought.</para>
///
/// <para><b>Scope, and why it is not "everywhere".</b> Product code disposes hubs on purpose and
/// does NOT join — a <c>SynchronizationStream</c> unsubscribing its sync hub, <c>Workspace</c>
/// evicting a client subscription, <c>OrleansRoutingService</c> releasing a pod claim,
/// <c>Activity</c> dropping its hosted hub. Those are live-system disposals: nothing is about to
/// tear the scope down or exit, the disposal's own state machine finishes on its own, and blocking a
/// hub thread to watch it finish would be the deadlock this repo's reactive rule forbids. The defect
/// is specific to a caller that PROCEEDS PAST the disposal into teardown or exit, which is what a
/// test base, a fixture and a run harness all do by definition. Test BODIES under <c>test/</c> are
/// also excluded: their base class joins the mesh in <c>DisposeAsync</c>, and the mesh's own
/// disposal joins its hosted hubs — a per-test client disposed inside a <c>[Fact]</c> is therefore
/// bounded by the class teardown rather than by process exit.</para>
/// </summary>
public class HubDisposalJoinRatchetGuard(ITestOutputHelper output)
{
    /// <summary>
    /// The seeded inventory's size — <b>zero</b>. Per-file entries would stop a new site in a file
    /// that already carries the shape; this stops the list as a WHOLE from growing, including by
    /// the trick of adding a new file's line. It may only ever go down, and it is already at the
    /// floor.
    /// </summary>
    private const int TotalBudget = 0;

    private const string AllowFileName = "HubDisposalJoinSites.allow";

    /// <summary>
    /// The roots walked before <see cref="IsHarnessFile"/> narrows them. Kept wide so a harness
    /// added in a new project is seen; the predicate, not the walk, is what defines the surface.
    /// </summary>
    private static readonly string[] ScannedRoots = ["src", "test", "tools"];

    /// <summary>
    /// A <c>.Dispose()</c> call, capturing the whole receiver chain (<c>reg.Hub</c>, <c>kv.Value.Stream.Hub</c>)
    /// so the join can be attributed to the SAME hub — the miss that let the plugin gate's client
    /// read as joined because the MESH's join happened to sit two statements below it.
    /// </summary>
    private static readonly Regex DisposeCall = new(
        @"(?<![A-Za-z0-9_])((?:[A-Za-z_]\w*)(?:\s*\??\.\s*[A-Za-z_]\w*)*)\s*\??\.\s*Dispose\s*\(\s*\)",
        RegexOptions.Compiled);

    /// <summary>
    /// 🚨 <b>LOAD-BEARING.</b> A receiver is treated as a hub when its last identifier ENDS in one of
    /// these — <c>hub</c>, <c>Hub</c>, <c>meshHub</c>, <c>podHub</c>, <c>Mesh</c>, <c>client</c>,
    /// <c>Client</c>. A hub variable named after none of them is invisible to this guard, which is
    /// how a whole family of sites goes uncounted (the identity-scope ratchet learned the same
    /// lesson the hard way: 19 sites across 10 files hid behind a helper name that was not on its
    /// list). Extend this in the same change that introduces the naming.
    ///
    /// <para>The match is anchored so it cannot fire on a PLURAL (<c>hostedHubs</c>, a
    /// <c>HostedHubsCollection</c> with its own collective join) or on an unrelated word ending in
    /// the same letters (<c>clientHost</c> is an <c>IHost</c>, <c>_portalProcess</c> is a
    /// <see cref="System.Diagnostics.Process"/>).</para>
    /// </summary>
    private static readonly Regex HubReceiver = new(
        @"(?:^|[a-z0-9_])(?:[Hh]ub|[Mm]esh|[Cc]lient)$", RegexOptions.Compiled);

    /// <summary>
    /// Joins that must name the hub being joined — <c>hub.DisposalCompleted</c>,
    /// <c>Mesh.DisposeAndJoin(…)</c>, <c>reg.Hub.TeardownAsync(…)</c>. Anchoring on the receiver is
    /// the whole point: an unanchored search reads a NEIGHBOURING hub's join as this one's.
    /// </summary>
    private static readonly string[] AnchoredJoins =
    [
        "DisposalCompleted", "DisposeAndJoin", "DisposeAndJoinAsync",
        "WaitForDisposalAndIoDrainAsync", "TeardownAsync",
    ];

    /// <summary>
    /// 🚨 <b>LOAD-BEARING, and deliberately tiny.</b> Named indirections that join the hub the
    /// enclosing method owns without naming it at the call site. <c>WaitWithProgressAsync</c> is
    /// <c>MonolithMeshTestBase</c>'s own private wait on <c>Mesh.DisposalCompleted</c> with a
    /// progress dump. Every entry here is a hole the guard cannot see through, so add one only when
    /// the indirection genuinely joins, and prefer renaming the helper to contain
    /// <c>DisposeAndJoin</c> over growing this list.
    /// </summary>
    private static readonly string[] UnanchoredJoins = ["WaitWithProgressAsync"];

    /// <summary>
    /// How far after the disposal a join may appear, in NON-BLANK lines of masked code. Counting
    /// code lines rather than physical ones is what lets a site stay clear through the long
    /// explanatory comment blocks this repo writes between the two statements
    /// (<c>SharedOrleansFixture.CleanupClientAsync</c> puts 14 lines of remark between its
    /// <c>Dispose()</c> and its <c>DisposalCompleted</c>).
    /// </summary>
    private const int JoinWindow = 15;

    [Fact]
    public void NoHarnessDisposesAHubWithoutJoiningItsTeardown()
    {
        var root = SourceScan.FindRepoRoot();
        var allowed = SourceScan.ReadAllowFile(Path.Combine(root, "test", AllowFileName), AllowFileName);
        var found = Scan(root, out var bare);

        var failures = new List<string>();

        foreach (var (file, count) in found.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!allowed.TryGetValue(file, out var budget))
                failures.Add(
                    $"  NEW SITE   {file} ({count}) — the hub(s) disposed here are never joined. "
                    + "Use `await hub.DisposeAndJoinAsync(report)` (async teardown) or "
                    + "`hub.DisposeAndJoin(report)` (synchronous run boundary); for a mesh root use "
                    + "MeshTeardownExtensions. Do NOT add a line to " + AllowFileName + ".");
            else if (count > budget)
                failures.Add(
                    $"  MORE       {file} ({count} > {budget} allowed) — an un-joined disposal was "
                    + "ADDED to a file that already carries the shape.");
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
            "A harness that disposes a hub and proceeds without joining its teardown runs the rest "
            + "of teardown CONCURRENTLY with the hub's own — and then disposes the scope, unloads a "
            + "node ALC, or hands the mesh to the next test. The result is a use-after-dispose that "
            + "kills the process (SIGSEGV / exit 139) mid-run, naming no test: exactly "
            + "MeshWeaver.Plugins run 33236823482. Join the disposal.\n"
            + string.Join("\n", failures)
            + (bare.Count == 0 ? string.Empty : "\n\nSites:\n  " + string.Join("\n  ", bare)));
    }

    /// <summary>
    /// Non-vacuity, part 1 — the CLASSIFIER, proven on synthetic source rather than on the tree.
    /// A ratchet whose allow file is empty passes just as happily when its scanner has stopped
    /// matching anything at all, so the discriminating behaviour is asserted directly: the bare
    /// shape must be flagged, the joined shape must not, and the join must be attributed to the
    /// hub it names rather than to whichever hub happens to be joined nearby.
    /// </summary>
    [Fact]
    public void TheClassifierTellsAJoinedDisposalFromABareOne()
    {
        const string bare = "void T() { client.Dispose(); Log(\"done\"); }";
        Assert.Equal(1, CountUnjoined(bare));

        const string joined = "void T() { client.Dispose(); client.DisposalCompleted.Wait(); }";
        Assert.Equal(0, CountUnjoined(joined));

        const string viaHelper = "void T() { client.DisposeAndJoin(Report); }";
        Assert.Equal(0, CountUnjoined(viaHelper));

        // The plugin-gate miss, verbatim in shape: the client is disposed, the MESH is joined two
        // statements later. An unanchored scan reads this as clean; it is the bug.
        const string neighboursJoin =
            "void T() { Client.Dispose(); Mesh.Dispose(); Mesh.DisposalCompleted.Wait(); }";
        Assert.Equal(1, CountUnjoined(neighboursJoin));

        // A dotted receiver is one site, joined through the same chain.
        const string dotted = "void T() { reg.Hub.Dispose(); reg.Hub.DisposalCompleted.Wait(); }";
        Assert.Equal(0, CountUnjoined(dotted));

        // Not hubs: a collection with its own collective join, an IHost, a Process.
        const string notHubs =
            "void T() { hostedHubs.Dispose(); clientHost.Dispose(); _portalProcess.Dispose(); }";
        Assert.Equal(0, CountUnjoined(notHubs));

        // Prose quoting the shape is not a call site — this file and the allow file both do it.
        const string prose = "void T() { /* client.Dispose(); with no join */ Log(\"x\"); }";
        Assert.Equal(0, CountUnjoined(prose));
    }

    /// <summary>
    /// Non-vacuity, part 2 — the WALK. The classifier can be perfect and the guard still gate
    /// nothing if <see cref="IsHarnessFile"/> selects no files or <see cref="SourceScan"/> stops
    /// finding them. The four harness sites the fixing change converted are all JOINED now, so the
    /// only honest evidence the surface is still being read is that the scan sees hub disposals in
    /// it at all.
    /// </summary>
    [Fact]
    public void TheScannerStillSeesTheHarnessSurface()
    {
        var root = SourceScan.FindRepoRoot();
        var harnessFiles = SourceScan.SourceFiles(root, ScannedRoots)
            .Select(f => SourceScan.Relative(root, f))
            .Where(IsHarnessFile)
            .ToList();

        Assert.True(harnessFiles.Count >= 10,
            "IsHarnessFile selected " + harnessFiles.Count + " files — the covered surface has "
            + "collapsed (a project renamed away from *.TestBase / *.Fixture, or tools/ moved), so "
            + "the ratchet above is gating nothing. Fix the predicate, do not delete the check.");

        var teardowns = harnessFiles
            .Select(rel =>
            {
                var text = File.ReadAllText(Path.Combine(root, rel));
                var code = SourceScan.MaskCommentsAndStrings(text);
                return (rel, sites: Sites(text), helper: HelperCalls.Matches(code).Count);
            })
            .Where(x => x.sites.Count > 0 || x.helper > 0)
            .ToList();

        // 🚨 The count includes helper calls ON PURPOSE, and that is not padding. Counting only
        // literal `.Dispose()` would make this assertion FALL as sites get converted to
        // DisposeAndJoin — a gate that goes red for doing exactly what the gate asks, which is the
        // shape that teaches people to stop shrinking a ratchet. Every hub teardown in the surface
        // is one of the two spellings, so the total is stable under fixing and only grows.
        var total = teardowns.Sum(x => x.sites.Count + x.helper);
        Assert.True(total >= 6,
            $"The scan found {total} hub teardowns across the whole harness surface (expected at "
            + "least 6). Either every harness stopped owning hubs — which would be news — or the "
            + "DisposeCall / HubReceiver / HelperCalls patterns no longer match this codebase, in "
            + "which case the empty allow file is proving nothing.");

        foreach (var (rel, sites, helper) in teardowns.OrderBy(x => x.rel, StringComparer.Ordinal))
            output.WriteLine(
                $"{rel}: {sites.Count(s => s.Joined)} joined / {sites.Count} bare-form disposals, "
                + $"{helper} via DisposeAndJoin");
    }

    /// <summary>The helper spellings, for the surface census above — not for the ratchet itself.</summary>
    private static readonly Regex HelperCalls = new(
        @"\.DisposeAndJoin(?:Async)?\s*\(", RegexOptions.Compiled);

    /// <summary>
    /// The covered surface. 🚨 LOAD-BEARING: a test base, an xUnit fixture or a run harness is
    /// exactly a place where the caller proceeds past a disposal into teardown or process exit.
    /// </summary>
    private static bool IsHarnessFile(string relative)
    {
        var parts = relative.Split('/');
        if (parts.Length < 2) return false;
        return parts[0] switch
        {
            // Every tool is a run harness: it boots a mesh, does a job and exits.
            "tools" => true,
            // The shared test bases and the fixture library they are built on.
            "src" => parts[1].EndsWith(".TestBase", StringComparison.Ordinal)
                     || parts[1].Contains(".Fixture", StringComparison.Ordinal),
            // The shared test bases and the fixture library (under test/ since 2026-08-30, so they
            // are never packed or published) plus fixtures — test BODIES are bounded by their base
            // class's join (see the remarks).
            "test" => parts[1].EndsWith(".TestBase", StringComparison.Ordinal)
                      || parts[1].Contains(".Fixture", StringComparison.Ordinal)
                      || parts[^1].Contains("Fixture", StringComparison.Ordinal),
            _ => false,
        };
    }

    private static Dictionary<string, int> Scan(string root, out List<string> bare)
    {
        var sites = new List<string>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var file in SourceScan.SourceFiles(root, ScannedRoots))
        {
            var relative = SourceScan.Relative(root, file);
            if (!IsHarnessFile(relative)) continue;

            string text;
            try { text = File.ReadAllText(file); }
            catch (IOException) { continue; } // a file a concurrent build is writing is not evidence

            var unjoined = Sites(text).Where(s => !s.Joined).ToList();
            if (unjoined.Count == 0) continue;

            counts[relative] = unjoined.Count;
            sites.AddRange(unjoined.Select(s => $"{relative}:{s.Line}  {s.Receiver}.Dispose()"));
        }

        bare = sites;
        return counts;
    }

    private static int CountUnjoined(string text) => Sites(text).Count(s => !s.Joined);

    /// <summary>
    /// Every hub disposal in <paramref name="text"/>, each marked joined or not. Comments and string
    /// literals are masked first, so prose quoting the shape — which this repo's remarks do
    /// constantly — is not counted as a call site.
    /// </summary>
    private static List<(int Line, string Receiver, bool Joined)> Sites(string text)
    {
        var result = new List<(int, string, bool)>();
        if (!text.Contains(".Dispose(", StringComparison.Ordinal)) return result;

        var code = SourceScan.MaskCommentsAndStrings(text);
        var lines = code.Split('\n');

        foreach (Match match in DisposeCall.Matches(code))
        {
            var chain = Whitespace.Replace(match.Groups[1].Value, string.Empty);
            var tail = chain.Split('.')[^1];
            if (!HubReceiver.IsMatch(tail)) continue;

            var line = code.Take(match.Index).Count(c => c == '\n') + 1;
            result.Add((line, chain, IsJoined(lines, line, chain, tail)));
        }

        return result;
    }

    private static bool IsJoined(string[] lines, int line, string chain, string tail)
    {
        var window = new List<string>();
        for (var i = line - 1; i < lines.Length && window.Count < JoinWindow; i++)
            if (lines[i].Trim().Length > 0)
                window.Add(lines[i]);

        var text = string.Join('\n', window);
        if (UnanchoredJoins.Any(j => text.Contains(j, StringComparison.Ordinal)))
            return true;

        var dense = Whitespace.Replace(text, string.Empty);
        return AnchoredJoins.Any(j =>
            dense.Contains(tail + "." + j, StringComparison.Ordinal)
            || dense.Contains(chain + "." + j, StringComparison.Ordinal));
    }

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
}
