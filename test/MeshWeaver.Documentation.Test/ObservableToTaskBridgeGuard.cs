using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Governance ratchet for the Rx-to-<see cref="System.Threading.Tasks.Task"/> bridge:
/// <c>.ToTask(</c> may not appear in code, anywhere.
///
/// <para><b>The ruling (maintainer, 2026-08-30): <i>"totask is forbidden" · "strictly" · "no totask
/// ever" · "the only place they may work is inside activities but usually even there avoid"</i>.</b>
/// The exemption this repo used to state — <i>tests are the ONLY place
/// <c>await …FirstAsync().ToTask()</c> is acceptable</i> — is RETRACTED, in
/// <c>AGENTS.md</c>, in <c>Doc/Architecture/AsynchronousCalls</c> and in the <c>/async</c> and
/// <c>/testing</c> skills.</para>
///
/// <para><b>Why the test edge was never safe either, and this repo measured it.</b> Rx's bridge
/// completes its <see cref="System.Threading.Tasks.TaskCompletionSource{TResult}"/> from INSIDE the
/// pipeline, without <see cref="System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously"/>.
/// So <c>TrySetResult</c> resumes the awaiter <b>inline, on the signalling thread, still inside Rx's
/// trampoline</b> (<c>Producer.SubscribeRaw</c>) — and everything the continuation then does inherits
/// that flag. The captured 558-frame stack in <c>InlineObservableExtensions</c>' remarks shows it
/// escaping the pipeline entirely (#2377), and #2301 is the same mechanism parking a grain's turn
/// scheduler on the wait its own deactivation needed. It is sticky, too: <c>await</c> captures
/// <see cref="System.Threading.Tasks.TaskScheduler.Current"/> when there is no
/// <see cref="System.Threading.SynchronizationContext"/>, so once one continuation lands on that
/// scheduler every later <c>await</c> in the same method schedules onto it. A bridge written "only
/// in a test" therefore changes how the code under test runs, and a green test proves the wrong
/// thing.</para>
///
/// <para><b>The fix at a site.</b> Compose reactively and
/// <c>.Subscribe(onNext, onError)</c>. Where an external signature genuinely forces a
/// <see cref="System.Threading.Tasks.Task"/> — an ASP.NET endpoint, an
/// <c>ILifecycleObserver.OnStop</c>, an SDK interface you implement — wait through
/// <c>MeshWeaver.Messaging.ReactiveCompletion.ObserveCompletion</c>, which subscribes, completes
/// with <c>RunContinuationsAsynchronously</c> (the line that stops the inline resumption) and keeps
/// its error arm attached so a late fault is reported rather than orphaned. Where the result is not
/// needed, subscribe and return <c>Task.CompletedTask</c> (<c>/async</c> skill, Rule 1a).</para>
///
/// <para><b>Two rules, deliberately different.</b>
/// <list type="bullet">
/// <item><see cref="ProductionRoots"/> — <c>src/</c>, <c>tools/</c>, <c>samples/</c>,
/// <c>clients/</c> — are held at ZERO with <b>no allow file at all</b>. The maintainer's words:
/// <i>"in src especially we should have zero"</i> — zero, not "zero except". There is nothing to
/// add a line to, so the only way past this rule is to fix the site.</item>
/// <item><see cref="RatchetedRoots"/> — <c>test/</c> and <c>memex/</c> — carry a seeded inventory
/// that may only SHRINK, because their sweeps land in later waves. When a wave empties one, move
/// its root into <see cref="ProductionRoots"/> in the same change.</item>
/// </list></para>
///
/// <para><b>The ratchet may only SHRINK.</b> A new file, a raised count, or a raised TOTAL is a
/// failure. A line that has become stale (its site was fixed) is REPORTED, not failed: two PRs
/// closing sites concurrently would otherwise red <c>main</c> on whichever merged second, and a gate
/// that punishes the direction it is asking for teaches people to stop shrinking. Delete the stale
/// line and lower <see cref="TotalBudget"/> in the same change.</para>
/// </summary>
public class ObservableToTaskBridgeGuard(ITestOutputHelper output)
{
    /// <summary>
    /// Held at ZERO, with no allow file. See the type remarks for why these carry no escape hatch.
    /// </summary>
    private static readonly string[] ProductionRoots = ["src", "tools", "samples", "clients"];

    /// <summary>
    /// Still carrying a seeded inventory, one wave behind. <c>test/</c> is the bulk of the fleet's
    /// sites; <c>memex/</c> is the portal tree, swept last by the maintainer's wave order. Each
    /// moves to <see cref="ProductionRoots"/> when its sweep reaches zero.
    /// </summary>
    private static readonly string[] RatchetedRoots = ["test", "memex"];

    /// <summary>
    /// The bridge as it appears in source.
    ///
    /// <para>🚨 The trailing <c>(</c> matters: it matches the CALL and not the prose, so
    /// <c>ToTask</c> named in a sentence, or the <c>System.Reactive.Threading.Tasks</c> namespace
    /// that also hosts the SAFE direction (<c>Task&lt;T&gt;.ToObservable()</c>), are untouched. The
    /// leading <c>.</c> keeps a method someone happens to name <c>ToTask</c> on their own type out
    /// of scope only when it is not invoked as an extension — which is the honest reading: any
    /// <c>.ToTask(</c> in this repo is Rx's.</para>
    /// </summary>
    private const string Marker = ".ToTask(";

    private const string AllowFileName = "ObservableToTaskBridgeSites.allow";

    /// <summary>
    /// The ONE exemption, and it is not an escape hatch: the test whose entire PURPOSE is to
    /// demonstrate the banned shape's behaviour, by measuring that it resumes its awaiter on the
    /// signalling thread. A rule about a defect needs one place that may still exhibit the defect,
    /// or it cannot be evidenced — and this one is what stops the plausible "simplification" of
    /// swapping the bridge for a direct <c>await</c> of the observable, which resumes inline in
    /// exactly the same way.
    ///
    /// <para>🚨 It is verified rather than trusted: <see cref="TheExemptedPinningTestStillPinsTheShape"/>
    /// fails if this file stops existing or stops containing the shape, so the exemption cannot
    /// quietly decay into a hole someone parks a real bridge in.</para>
    /// </summary>
    private static readonly string[] ExemptPinningFiles =
        ["test/MeshWeaver.Messaging.Hub.Test/InlineResumptionMechanismTest.cs"];

    /// <summary>
    /// The seeded inventory's size. Per-file entries stop a new site in a file that already carries
    /// the shape; this stops the list as a WHOLE from growing — including by the trick of adding a
    /// new file's line. Lower it whenever you delete or lower an entry.
    /// </summary>
    private const int TotalBudget = 1548;

    /// <summary>
    /// The hard half: <c>src/</c> and friends are at zero and stay there. No allow file is read,
    /// so there is no line to add and no budget to raise — the only way to make this pass is to
    /// remove the bridge.
    /// </summary>
    [Fact]
    public void NoProductionCodeBridgesAnObservableToATask()
    {
        var root = SourceScan.FindRepoRoot();
        var found = Scan(root, ProductionRoots);

        Assert.True(found.Count == 0,
            "🚨 `.ToTask(` is FORBIDDEN in production code — zero, with no allow-list "
            + "(maintainer, 2026-08-30: \"no ToTask ever\"; \"in src especially we should have "
            + "zero\"). Rx completes the Task from inside its own pipeline without "
            + "RunContinuationsAsynchronously, so the awaiter resumes INLINE on the signalling "
            + "thread, still inside Rx's trampoline, and everything the continuation does inherits "
            + "that (#2377, #2301).\n"
            + "Fix the site: compose reactively and .Subscribe(onNext, onError); where an external "
            + "signature forces a Task, wait through ReactiveCompletion.ObserveCompletion; where "
            + "the result is not needed, subscribe and return Task.CompletedTask.\n"
            + string.Join("\n", found
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"  {kv.Key} ({kv.Value})")));
    }

    /// <summary>
    /// The shrinking half: the trees whose sweep is still in flight may not GROW.
    /// </summary>
    [Fact]
    public void NoNewBridgeInTheTreesStillBeingSwept()
    {
        var root = SourceScan.FindRepoRoot();
        var allowed = SourceScan.ReadAllowFile(Path.Combine(root, "test", AllowFileName), AllowFileName);
        var found = Scan(root, RatchetedRoots);

        var failures = new List<string>();

        foreach (var (file, count) in found.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!allowed.TryGetValue(file, out var budget))
                failures.Add(
                    $"  NEW SITE   {file} ({count}) — await the observable through "
                    + "ReactiveCompletion.ObserveCompletion, or stay reactive and Subscribe. Do NOT "
                    + "add a line to " + AllowFileName + ".");
            else if (count > budget)
                failures.Add(
                    $"  MORE       {file} ({count} > {budget} allowed) — a bridge was ADDED to a "
                    + "file that already carries the shape.");
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
            "`.ToTask(` bridges an observable to a Task whose awaiter Rx resumes INLINE on the "
            + "signalling thread, inside its own trampoline — so a bridge written \"only in a "
            + "test\" changes how the code under test runs (maintainer, 2026-08-30: \"no ToTask "
            + "ever\"). These trees are mid-sweep: the inventory may shrink, never grow.\n"
            + string.Join("\n", failures));
    }

    /// <summary>
    /// Non-vacuity, pinned in the same run — and it has to be stated rather than inferred, because
    /// the production half's evidence is an EMPTY result. "The scanner found nothing in src/" and
    /// "the scanner is broken" are the same observation, which is exactly the skip-trapdoor shape
    /// AGENTS.md forbids in a gate. So this drives the real scanner over a synthetic file and
    /// asserts BOTH directions: it counts a call, and it does not count the same text in prose.
    ///
    /// <para>The prose half is not hypothetical. This repo's remarks quote the banned shape
    /// verbatim, at length, to explain WHY it is banned — <c>ReactiveCompletion</c>,
    /// <c>InlineObservableExtensions</c>, <c>IIoPool</c>, this very file. A scanner that counted
    /// those would ratchet against its own documentation and force the deletion of the institutional
    /// record that makes the rule teachable.</para>
    /// </summary>
    [Fact]
    public void TheScannerCountsCallsAndIgnoresProse()
    {
        const string sample = """
            // A comment mentioning .ToTask() must NOT count.
            /// <summary>Nor an XML doc quoting <c>source.FirstAsync().ToTask(ct)</c>.</summary>
            /* Nor a block comment: .ToTask(ct) */
            public class Sample
            {
                private const string Prose = "not a call either: .ToTask(";
                public Task<int> Real() => source.FirstAsync().ToTask(ct);
            }
            """;

        Assert.Equal(1, CountIn(sample));

        // And the masker really is what makes the difference — without it the same text reads as
        // five sites, so a regression in MaskCommentsAndStrings would be caught here rather than
        // silently inflating every count in the allow file.
        var unmasked = 0;
        for (var at = 0; (at = sample.IndexOf(Marker, at, StringComparison.Ordinal)) >= 0; at += Marker.Length)
            unmasked++;
        Assert.True(unmasked > 1,
            "the sample must contain prose occurrences, or this test proves nothing about masking");

        // The live counterpart: the trees still being swept must actually yield sites, so the
        // shrinking half above cannot be passing on an empty scan.
        var root = SourceScan.FindRepoRoot();
        Assert.True(Scan(root, RatchetedRoots).Count > 0,
            "The scanner found no bridge anywhere under " + string.Join(", ", RatchetedRoots)
            + ". Either every site was swept — in which case move those roots into ProductionRoots, "
            + "empty " + AllowFileName + " and delete this assertion — or the scanner is broken, "
            + "which would make the ratchet above pass on no evidence.");
    }

    private static Dictionary<string, int> Scan(string root, IEnumerable<string> roots) =>
        SourceScan.SourceFiles(root, roots)
            .Select(f => (Relative: SourceScan.Relative(root, f), Count: CountSites(f)))
            .Where(x => x.Count > 0)
            .Where(x => !ExemptPinningFiles.Contains(x.Relative, StringComparer.Ordinal))
            .ToDictionary(x => x.Relative, x => x.Count, StringComparer.Ordinal);

    /// <summary>
    /// Keeps <see cref="ExemptPinningFiles"/> honest. An exemption that outlives its subject is a
    /// hole with a comment on it — the failure mode AGENTS.md names as "a guard whose subject moved
    /// and whose roots did not passes having checked nothing".
    /// </summary>
    [Fact]
    public void TheExemptedPinningTestStillPinsTheShape()
    {
        var root = SourceScan.FindRepoRoot();
        foreach (var relative in ExemptPinningFiles)
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path),
                $"{relative} is exempted from this guard but no longer exists. Delete the exemption "
                + "— an exemption whose subject is gone is a hole a real bridge can be parked in.");
            Assert.True(CountSites(path) > 0,
                $"{relative} is exempted from this guard but no longer contains the shape it exists "
                + "to demonstrate. Either it was rewritten (delete the exemption) or the scanner "
                + "broke (fix that first — every count in the allow file depends on it).");
        }
    }

    /// <summary>Occurrences of <see cref="Marker"/>, with comments and string literals masked first.</summary>
    private static int CountSites(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (IOException) { return 0; } // a file a concurrent build is writing is not evidence

        return text.Contains(Marker, StringComparison.Ordinal) ? CountIn(text) : 0;
    }

    private static int CountIn(string text)
    {
        var code = SourceScan.MaskCommentsAndStrings(text);
        var count = 0;
        for (var at = 0; (at = code.IndexOf(Marker, at, StringComparison.Ordinal)) >= 0; at += Marker.Length)
            count++;
        return count;
    }
}
