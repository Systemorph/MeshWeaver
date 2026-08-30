using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 A hand-written test timeout — <c>30.Seconds()</c>, <c>TimeSpan.FromSeconds(30)</c>,
/// <c>[Fact(Timeout = 30000)]</c> — is a guess about how fast a machine is, written where it can
/// never be revisited. <see cref="MeshWeaver.Fixture.TestTimeouts"/> is the one place that decides.
///
/// <para><b>Two independent defects, not one.</b> The first is the familiar one (#2700): CI is
/// roughly 1.7× slower than the laptop the number was chosen on, so a 30 s bound leaves ~18 s of
/// headroom and under runner contention that is gone — six failures at 30–33 s in a single evening,
/// in different tests, suites and repos, because the same guess had been copied everywhere.</para>
///
/// <para>The second is sharper and is why this guard exists rather than a style note (#2819).
/// <b>30 s is not merely tight — it is the framework's own bound.</b> A mesh write is failed at
/// <c>LateResponseWatchBound</c> (30 s) + <c>VerdictBoundGrace</c> (1 s) = 31 s, and that grace is
/// deliberate so the caller's failure cannot race a still-admissible verdict. A test bounded at
/// 30 s therefore gives up ONE SECOND before the framework can explain itself — not sometimes,
/// always — so the failure reads "the observable emitted nothing at all" instead of naming
/// <c>OwnerUnreachable</c>. The literal sat at exactly the value that destroys the most
/// information.</para>
///
/// <para><b>Why a ratchet and not zero.</b> There are ~1,468 of these across the test tree in 287
/// files; converting them is a long tail, and a guard that cannot pass today would simply be
/// disabled. So the count may only ever go DOWN. 🚨 <b>Raising <see cref="Baseline"/> is not a fix
/// — it is the defect.</b> If this fails, convert the sites you added rather than re-seeding: the
/// number is in the diff precisely so that re-seeding is visible to a reviewer.</para>
///
/// <para>#2708 landed <c>TestTimeouts</c> and NOTHING adopted it — the type's only references were
/// its own test until #2829. That is what this guard is really for: a shared mechanism with no
/// adopters is inert, and inertness is invisible without a count.</para>
/// </summary>
public class TestTimeoutLiteralRatchetGuard
{
    /// <summary>Only the test tree — production timeouts are a different contract entirely.</summary>
    private static readonly string[] ScannedRoots = ["test"];

    /// <summary>
    /// The literals that mean "a guessed test wait". Deliberately narrow: it catches the copied
    /// 30-second convention, not every duration in the tree. A test that genuinely needs a
    /// different, reasoned bound writes a different number and is not the subject here.
    /// </summary>
    private static readonly Regex Literal = new(
        @"\b30\s*\.\s*Seconds\s*\(\s*\)"
        + @"|TimeSpan\s*\.\s*FromSeconds\s*\(\s*30\s*\)"
        + @"|Timeout\s*=\s*30_?000\b",
        RegexOptions.Compiled);

    /// <summary>
    /// Files that legitimately name the literal: the type that REPLACES it, and its own tests,
    /// which must be able to talk about the value they exist to eliminate.
    /// </summary>
    private static readonly string[] Exempt =
    [
        "test/MeshWeaver.Fixture/TestTimeouts.cs",
        "test/MeshWeaver.Graph.Test/TestTimeoutsTest.cs",
        "test/MeshWeaver.Documentation.Test/TestTimeoutLiteralRatchetGuard.cs",
    ];

    /// <summary>
    /// 🚨 Seeded from the tree on 2026-08-30, when it held <b>1470</b>. MAY ONLY DECREASE.
    ///
    /// <para><b>Why it is not seeded at exactly 1470.</b> The tree gained 4 literals in the hour
    /// this guard was written — several sessions merge in parallel, and a PR already in flight
    /// cannot know about a ratchet that did not exist when it was branched. A zero-slack baseline
    /// would therefore red <c>main</c> for everyone within the hour, punishing authors for a rule
    /// that post-dates their branch. The ~20 of headroom is a TRANSITIONAL allowance for exactly
    /// that, and it is the only reason this number exceeds the tree.</para>
    ///
    /// <para>🚨 It is not a budget to spend. <see cref="TheBaselineStaysCloseToTheTree"/> caps the
    /// slack, so the allowance cannot quietly become permanent; the correct next edit to this
    /// number is DOWNWARD, in whichever change first converts a batch.</para>
    /// </summary>
    private const int Baseline = 1490;

    [Fact]
    public void TheHandWrittenTimeoutCountOnlyEverGoesDown()
    {
        var root = SourceScan.FindRepoRoot();
        var perFile = SourceScan.SourceFiles(root, ScannedRoots)
            .Select(f => (Path: SourceScan.Relative(root, f), Count: CountIn(f)))
            .Where(x => x.Count > 0 && !Exempt.Contains(x.Path))
            .OrderByDescending(x => x.Count)
            .ToList();
        var total = perFile.Sum(x => x.Count);

        Assert.True(total <= Baseline,
            $"🚨 Hand-written test timeouts rose to {total} (baseline {Baseline}). Use "
            + "TestTimeouts.Convergence for a wait and TestTimeouts.TestMilliseconds for "
            + "[Fact(Timeout = …)] instead of writing a literal.\n\n"
            + "A literal 30 s is wrong twice over: CI is ~1.7× slower than the machine it was "
            + "chosen on, AND 30 s is the framework's OWN write bound "
            + "(LateResponseWatchBound 30 s + VerdictBoundGrace 1 s = 31 s), so a test that waits "
            + "30 s on a write always gives up one second before UpdateRemote can report "
            + "OwnerUnreachable — the failure can never say why (#2819).\n\n"
            + "🚨 Do NOT raise the Baseline. Convert the sites you added.\n"
            + "Heaviest files:\n"
            + string.Join("\n", perFile.Take(10).Select(x => $"  {x.Path} ({x.Count})")));
    }

    /// <summary>
    /// 🚨 The baseline must also not drift far ABOVE the tree: a ratchet seeded well above reality
    /// silently tolerates a large regression before it ever fires. If conversion has brought the
    /// real count down, bring the baseline with it — that is the one edit to this number that is
    /// always correct.
    /// </summary>
    [Fact]
    public void TheBaselineStaysCloseToTheTree()
    {
        var root = SourceScan.FindRepoRoot();
        var total = SourceScan.SourceFiles(root, ScannedRoots)
            .Where(f => !Exempt.Contains(SourceScan.Relative(root, f)))
            .Sum(CountIn);

        Assert.True(Baseline - total <= 100,
            $"The tree now holds {total} hand-written timeouts but Baseline is still {Baseline}. "
            + $"Slack of {Baseline - total} means the ratchet would tolerate that many NEW literals "
            + "before failing — a ratchet is only as strong as its distance from the tree. Lower "
            + "Baseline to the current count in the change that did the converting.");
    }

    /// <summary>
    /// 🚨 Proven by MUTATION over a planted tree, running the REAL scan — not by exercising the
    /// regex against strings. A guard whose self-test never calls the scanner can lose half its
    /// scope and stay green, which is how three guards passed here in one day while their subjects
    /// broke.
    /// </summary>
    [Fact]
    public void TheScannerSeesWhatItClaimsTo()
    {
        var dir = Directory.CreateTempSubdirectory("timeout-ratchet-selftest");
        try
        {
            var t = Directory.CreateDirectory(Path.Combine(dir.FullName, "test")).FullName;
            File.WriteAllText(Path.Combine(t, "Seconds.cs"),
                "class A { void M() { X.Should().Within(30.Seconds()).Emit(); } }");
            File.WriteAllText(Path.Combine(t, "FromSeconds.cs"),
                "class B { void M() { var t = TimeSpan.FromSeconds(30); } }");
            File.WriteAllText(Path.Combine(t, "FactTimeout.cs"),
                "class C { [Fact(Timeout = 30000)] public void M() { } }");
            File.WriteAllText(Path.Combine(t, "Underscored.cs"),
                "class D { [Fact(Timeout = 30_000)] public void M() { } }");
            File.WriteAllText(Path.Combine(t, "Prose.cs"),
                "// never write 30.Seconds() by hand; use TestTimeouts.\nclass E { }");
            File.WriteAllText(Path.Combine(t, "Literal.cs"),
                "class F { const string S = \"30.Seconds()\"; }");
            File.WriteAllText(Path.Combine(t, "OtherBound.cs"),
                "class G { void M() { var t = TimeSpan.FromSeconds(45); } }");
            File.WriteAllText(Path.Combine(t, "Adopted.cs"),
                "class H { void M() { X.Should().Within(TestTimeouts.Convergence).Emit(); } }");
            Directory.CreateDirectory(Path.Combine(t, "bin"));
            File.WriteAllText(Path.Combine(t, "bin", "Ignored.cs"),
                "class I { void M() { var t = TimeSpan.FromSeconds(30); } }");

            var found = SourceScan.SourceFiles(dir.FullName, ["test"])
                .Select(f => (Name: Path.GetFileName(f), Count: CountIn(f)))
                .Where(x => x.Count > 0)
                .ToDictionary(x => x.Name, x => x.Count);

            Assert.True(found.ContainsKey("Seconds.cs"), "30.Seconds() must be found");
            Assert.True(found.ContainsKey("FromSeconds.cs"), "TimeSpan.FromSeconds(30) must be found");
            Assert.True(found.ContainsKey("FactTimeout.cs"), "[Fact(Timeout = 30000)] must be found");
            Assert.True(found.ContainsKey("Underscored.cs"),
                "🚨 30_000 must be found — a digit separator is the cheapest way past a naive scan");
            Assert.False(found.ContainsKey("Prose.cs"),
                "a comment telling authors not to do it is not an occurrence — this repo writes those at length");
            Assert.False(found.ContainsKey("Literal.cs"), "a string literal is not a timeout");
            Assert.False(found.ContainsKey("OtherBound.cs"),
                "a different, reasoned duration is not the copied convention this guard is about");
            Assert.False(found.ContainsKey("Adopted.cs"),
                "🚨 the CONVERTED shape must not be flagged — otherwise the guard punishes the fix");
            Assert.False(found.ContainsKey("Ignored.cs"), "bin/ must not be scanned");
        }
        finally { dir.Delete(recursive: true); }
    }

    private static int CountIn(string file) =>
        Literal.Matches(SourceScan.MaskCommentsAndStrings(File.ReadAllText(file))).Count;
}
