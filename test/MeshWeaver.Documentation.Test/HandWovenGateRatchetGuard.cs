using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 A hand-woven concurrency gate — <c>SemaphoreSlim</c>, <c>ManualResetEventSlim</c>,
/// <c>AutoResetEvent</c>, <c>CountdownEvent</c>, <c>Monitor.Wait</c> — is FORBIDDEN in production,
/// outside the one place sealed inside <c>IoPool</c>.
///
/// <para><b>Why.</b> These park a thread. On the actor-model mesh that thread is a single-threaded
/// action block or a grain turn, so the message the gate is waiting on can never be processed —
/// the deadlock class <c>/async</c> names alongside <c>await</c> on a hub. Serialization belongs to
/// the hub (a <c>Subject</c> + <c>.Select(Run).Concat()</c>, or <c>GetMeshNodeStream(path).Update</c>,
/// where the owning hub serialises every writer); concurrency bounding and one-shot init belong to
/// <c>IIoPool</c> (<c>pool.Run(...)</c> held in an INSTANCE <c>PromiseCache</c>).</para>
///
/// <para><b>ONE tier as of 2026-08-30 — the allow file is GONE.</b> #2762's shape had two: a
/// verified register for production, and a seeded inventory for <c>test/</c> that could only
/// shrink. It was seeded at <b>79 sites across 23 files</b>, every one a
/// <c>ManualResetEventSlim</c>, 32 of them in <c>IoPoolTest.cs</c>. All 79 were converted in one
/// sweep and <c>test/HandWovenGateSites.allow</c> was deleted, so <c>test</c> now sits in
/// <see cref="ProductionRoots"/> alongside everything else and is held at ZERO with no escape
/// hatch. Nothing to append a line to is the strongest form this rule has.</para>
///
/// <para><b>What the sweep replaced them with</b>, so the next author does not reach for an event
/// again. A signal a PRODUCER raises and a test consumes becomes an
/// <c>AsyncSubject&lt;Unit&gt;</c> the producer completes (<c>OnNext</c> then <c>OnCompleted</c>),
/// awaited through the house assertion helpers (<c>await x.Should().Within(...).Emit(because)</c>,
/// or <c>NotEmit(within)</c> for the negative) — never <c>.Wait()</c> on an observable, which only
/// trades this ratchet for <see cref="BlockingBridgeInTestRatchetGuard"/>. A RELEASE travelling the
/// other way — into a worker a test deliberately parks, because "a leaf that ignores its
/// cancellation token" or "a wedged action block" IS the subject — becomes a volatile
/// <c>int</c> the parked worker polls under a bounded
/// <c>SpinWait.SpinUntil(predicate, budget)</c>, written in a <c>finally</c> so an assertion that
/// throws first cannot strand that worker (the exact leak <c>IoPoolResidualNamesItsPoolTest</c>
/// produced: a two-minute pool-thread hold bleeding into the next test).</para>
///
/// <para>🚨 This guard is proven by MUTATION, not by passing — see
/// <see cref="TheScannerSeesWhatItClaimsTo"/>, which plants a tree and runs the REAL scan over it.
/// A ratchet whose self-test only exercises its regex can stop covering half its subject and stay
/// green, which is the defect this repo found in three separate guards on one day.</para>
/// </summary>
public class HandWovenGateRatchetGuard
{
    /// <summary>
    /// Every root, held at ZERO with no allow file anywhere. The only permitted gates are the
    /// <see cref="SanctionedGates"/> entries, each re-checked against the tree on every run.
    /// </summary>
    private static readonly string[] ProductionRoots =
        ["src", "tools", "samples", "clients", "memex", "test"];

    /// <summary>
    /// The primitives that park a thread. <c>Monitor.Wait</c> is included because it is the same
    /// thing spelled differently; plain <c>lock</c> is NOT — it is uncontended-fast and does not
    /// wait on another party's signal, and flagging it would cry wolf on a hundred innocent sites.
    /// </summary>
    private static readonly Regex Gate = new(
        @"\b(SemaphoreSlim|ManualResetEventSlim|ManualResetEvent|AutoResetEvent|CountdownEvent|Monitor\s*\.\s*Wait)\b",
        RegexOptions.Compiled);

    /// <summary>
    /// The ONE sanctioned home, VERIFIED not listed: the entry must still exist and must still
    /// contain a gate. An exemption that outlives its subject is how the next hole gets hidden.
    /// </summary>
    private static readonly (string File, string Why)[] SanctionedGates =
    [
        ("src/MeshWeaver.Mesh.Contract/Threading/IoPool.cs",
            "IoPool IS the single boundary between the turn-based hub schedulers and genuinely "
            + "async I/O leaves. Its gate is what bounds concurrency and what teardown joins on; "
            + "AGENTS.md names it as the one sanctioned SemaphoreSlim in the repo. Everything else "
            + "channels through it precisely so no other file needs one."),

        ("tools/MeshWeaver.ThumbnailGenerator/ThumbnailGenerator.cs",
            "A STANDALONE Playwright CLI (OutputType Exe) whose csproj references Microsoft."
            + "Playwright, SixLabors.ImageSharp and System.CommandLine and NOT ONE MeshWeaver "
            + "assembly. There is no hub, no grain turn and no IIoPool in that process, so the "
            + "defect this rule prevents — parking a turn-based scheduler — cannot occur: its "
            + "SemaphoreSlim bounds concurrent browser pages against an external server. Kept in "
            + "scope rather than dropping `tools` from ProductionRoots, so a gate added to a tool "
            + "that DOES reference the mesh still fails. 🚨 If this project ever gains a MeshWeaver "
            + "ProjectReference, delete this entry and route the bound through IIoPool."),
    ];

    [Fact]
    public void ProductionCodeHasNoHandWovenGate_OutsideTheSanctionedOne()
    {
        var root = SourceScan.FindRepoRoot();
        var sanctioned = SanctionedGates.Select(s => s.File).ToHashSet(StringComparer.Ordinal);

        var offenders = SourceScan.SourceFiles(root, ProductionRoots)
            .Select(f => (Path: SourceScan.Relative(root, f), Count: CountIn(f)))
            .Where(x => x.Count > 0 && !sanctioned.Contains(x.Path))
            .OrderByDescending(x => x.Count)
            .ToList();

        Assert.True(offenders.Count == 0,
            "🚨 A hand-woven concurrency gate parks a thread — on a hub action block or a grain "
            + "turn that is the deadlock the mesh cannot recover from, and in a TEST it strands a "
            + "blocked worker whenever an assertion throws before the release runs. Serialize "
            + "through the hub (a Subject + .Select(Run).Concat(), or GetMeshNodeStream(path)"
            + ".Update, where the owner serialises writers); bound concurrency through IIoPool "
            + "(pool.Run(...) in an INSTANCE PromiseCache). IN A TEST: a producer→test signal is an "
            + "AsyncSubject<Unit> the producer completes, awaited through the assertion helpers "
            + "(await x.Should().Within(...).Emit() / .NotEmit(within)) — never .Wait() on an "
            + "observable, which just trips BlockingBridgeInTestRatchetGuard instead; a release "
            + "INTO a worker the test deliberately parks is a volatile int polled under a bounded "
            + "SpinWait.SpinUntil, written in a `finally`. Do NOT add an allow entry — there is no "
            + "allow file, for any root.\n"
            + string.Join("\n", offenders.Select(o => $"  {o.Path} ({o.Count})")));
    }

    [Fact]
    public void TheSanctionedRegisterIsStillAccurate()
    {
        var root = SourceScan.FindRepoRoot();
        foreach (var (file, why) in SanctionedGates)
        {
            var full = Path.Combine(root, file);
            Assert.True(File.Exists(full),
                $"The sanctioned register names {file}, which no longer exists. An entry that "
                + "outlives its subject is a hole nobody can see — delete it.");
            Assert.True(CountIn(full) > 0,
                $"{file} is registered as a sanctioned hand-woven gate but no longer contains one. "
                + $"Delete the entry — it now exempts nothing. Reason on file: {why}");

            // 🚨 Verify the REASON, not just the subject. The ThumbnailGenerator entry rests
            // entirely on that project referencing no MeshWeaver assembly; the moment it does, the
            // exemption is false and the gate must move to IIoPool. An exemption whose PREMISE
            // silently stops holding is worse than one whose subject moved — the subject's absence
            // at least fails loudly.
            var project = Directory
                .EnumerateFiles(Path.GetDirectoryName(full)!, "*.csproj")
                .FirstOrDefault();
            if (project is not null && file.StartsWith("tools/", StringComparison.Ordinal))
                Assert.DoesNotContain("MeshWeaver", ProjectReferences(project),
                    StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// 🚨 The allow file must STAY deleted. A ratchet that empties and is then quietly re-seeded is
    /// how a cleared inventory grows back, and <see cref="SourceScan.ReadAllowFile"/> is explicit
    /// that a REGENERATED allow file blesses whatever is in the tree. There is nothing left to
    /// tolerate — <c>test</c> is in <see cref="ProductionRoots"/> — so the file's re-appearance is
    /// itself the defect, and it fails here rather than silently widening the rule.
    /// </summary>
    [Fact]
    public void TheTestTreeAllowFileStaysDeleted()
    {
        var root = SourceScan.FindRepoRoot();
        var allow = Path.Combine(root, "test", "HandWovenGateSites.allow");

        Assert.False(File.Exists(allow),
            "test/HandWovenGateSites.allow is back. It was deleted when the last of its 79 seeded "
            + "sites was converted, and `test` moved into ProductionRoots in the same change — so "
            + "nothing reads this file any more and its only possible effect is to make a new gate "
            + "look sanctioned to a human reader. Convert the site instead: an AsyncSubject<Unit> "
            + "for a producer→test signal, a volatile int polled under a bounded SpinUntil (released "
            + "in a `finally`) for a release into a deliberately parked worker.");
    }

    /// <summary>
    /// 🚨 The self-test plants a real tree and runs the REAL scan over it, rather than exercising
    /// the regex against strings. That distinction is the whole point: a guard whose self-test
    /// never calls the scanner can lose half its scope and stay green.
    /// </summary>
    [Fact]
    public void TheScannerSeesWhatItClaimsTo()
    {
        var dir = Directory.CreateTempSubdirectory("gate-guard-selftest");
        try
        {
            var src = Directory.CreateDirectory(Path.Combine(dir.FullName, "src")).FullName;
            File.WriteAllText(Path.Combine(src, "Real.cs"),
                "class C { private readonly SemaphoreSlim _g = new(1,1); }");
            File.WriteAllText(Path.Combine(src, "OnlyProse.cs"),
                "// SemaphoreSlim is banned here; see /async.\nclass D { }");
            File.WriteAllText(Path.Combine(src, "OnlyALiteral.cs"),
                "class E { const string S = \"SemaphoreSlim\"; }");
            File.WriteAllText(Path.Combine(src, "Script.csx"),
                "var e = new ManualResetEventSlim(false);");
            Directory.CreateDirectory(Path.Combine(src, "bin"));
            File.WriteAllText(Path.Combine(src, "bin", "Ignored.cs"),
                "class F { private readonly SemaphoreSlim _g = new(1,1); }");

            var found = SourceScan.SourceFiles(dir.FullName, ["src"])
                .Select(f => (Name: Path.GetFileName(f), Count: CountIn(f)))
                .Where(x => x.Count > 0)
                .ToDictionary(x => x.Name, x => x.Count, StringComparer.Ordinal);

            Assert.True(found.ContainsKey("Real.cs"), "a real gate must be found");
            Assert.True(found.ContainsKey("Script.csx"),
                "🚨 .csx must be scanned — runtime-compiled script text is the one tree dotnet build "
                + "and every *.cs sweep are blind to");
            Assert.False(found.ContainsKey("OnlyProse.cs"),
                "a comment explaining the ban is not a gate — this repo writes those at length");
            Assert.False(found.ContainsKey("OnlyALiteral.cs"),
                "a string literal is not a gate");
            Assert.False(found.ContainsKey("Ignored.cs"), "bin/ must not be scanned");
        }
        finally { dir.Delete(recursive: true); }
    }

    /// <summary>The ProjectReference lines of a csproj, joined — enough to assert what a tool can reach.</summary>
    private static string ProjectReferences(string csproj) =>
        string.Join("\n", File.ReadAllLines(csproj).Where(l => l.Contains("ProjectReference")));

    private static int CountIn(string file) =>
        Gate.Matches(SourceScan.MaskCommentsAndStrings(File.ReadAllText(file))).Count;
}
