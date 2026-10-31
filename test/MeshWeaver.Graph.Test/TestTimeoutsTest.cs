#pragma warning disable CS1591

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.Testing.Xunit;
using Xunit;
using Xunit.v3;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// <see cref="TestTimeouts"/> exists so a waiting time is decided in one place instead of written
/// out as a literal ~2,679 times. These pin the two properties that make it an improvement rather
/// than a rename.
/// </summary>
public class TestTimeoutsTest
{
    /// <summary>
    /// 🚨 THE INVARIANT. 24 files carry `[Fact(Timeout = 30000)]` AND a 30 s internal wait, so
    /// xunit kills the test at the exact moment the wait expires: the assertion never fires and
    /// the failure is an anonymous timeout instead of naming what did not converge. The outer
    /// bound must therefore be strictly greater than the inner one — at EVERY scale factor, since
    /// a rule that holds only locally is one CI run away from being useless.
    /// </summary>
    [Theory]
    [InlineData(null)]      // local
    [InlineData("1")]
    [InlineData("3")]
    [InlineData("10")]
    public void TheOuterBoundAlwaysExceedsTheInner(string? factor)
    {
        using var _ = new EnvironmentVariable("GITHUB_ACTIONS", factor is null ? null : "true");
        using var __ = new EnvironmentVariable("MW_TEST_TIMEOUT_FACTOR", factor);

        Assert.True(
            TestTimeouts.TestMilliseconds > TestTimeouts.Convergence.TotalMilliseconds,
            $"the [Fact(Timeout)] value ({TestTimeouts.TestMilliseconds} ms) must exceed the "
            + $"convergence wait ({TestTimeouts.Convergence.TotalMilliseconds} ms), or an inner "
            + "wait can never lose first and the failure cannot say what it was waiting for.");
    }

    /// <summary>
    /// 🚨 THE SECOND INVARIANT, and the one that was violated for as long as the bound was a
    /// literal: a test wait must DOMINATE the framework's own outer write bound.
    ///
    /// <para><c>UpdateRemote</c> fails a silent write at
    /// <c>LateResponseWatchBound + VerdictBoundGrace</c> = 31 s, and the grace exists precisely so
    /// the framework's terminal arrives AFTER the registry stops honouring a verdict. The test
    /// convention was a hand-written 30 s — the same number as <c>LateResponseWatchBound</c>, one
    /// second below the terminal. So a test awaiting a write gave up before the framework could
    /// answer, every time, and the failure read "the observable emitted nothing at all" instead of
    /// naming <c>OwnerUnreachable</c>. The bound sat at exactly the value that destroys the most
    /// information (#2819).</para>
    ///
    /// <para>Checked at every factor, including the local 1.0: a rule that holds only on CI leaves
    /// the laptop — where the diagnosis is actually read — reporting nothing.</para>
    /// </summary>
    [Theory]
    [InlineData(null)]      // local
    [InlineData("1")]
    [InlineData("3")]
    [InlineData("10")]
    public void AConvergenceWaitDominatesTheFrameworkWriteBound(string? factor)
    {
        using var _ = new EnvironmentVariable("GITHUB_ACTIONS", factor is null ? null : "true");
        using var __ = new EnvironmentVariable("MW_TEST_TIMEOUT_FACTOR", factor);

        Assert.True(
            TestTimeouts.Convergence > LatePatchResponseRegistry.WriteVerdictBound,
            $"a convergence wait ({TestTimeouts.Convergence.TotalSeconds:0.##}s) must exceed the "
            + $"framework's write verdict bound "
            + $"({LatePatchResponseRegistry.WriteVerdictBound.TotalSeconds:0.##}s), or a test "
            + "awaiting a mesh write gives up before UpdateRemote can report OwnerUnreachable and "
            + "the failure can never say why.");
    }

    /// <summary>
    /// 🚨 <b>THE CONTROL FOR #3477. A wait that can cover a RE-ENQUEUE must dominate what a
    /// re-enqueue actually costs — not what ONE attempt costs.</b>
    ///
    /// <para><b>What was wrong, and why it could not be seen.</b> <c>WriteVerdictBound</c> is armed
    /// PER ATTEMPT and its doc said it was the outer bound on a write, full stop. Every waiter in
    /// the fleet derived from it through <c>TestTimeouts.Convergence</c>. But a write the owner
    /// NACKs as never-applied is re-enqueued up to
    /// <c>MeshNodeStreamHandle.MaxOwnerDisposingReenqueues</c> times, each re-attempt arming a FRESH
    /// deadline from its own post and paying a base read (and, on a phantom base, an authoritative
    /// re-read) OUTSIDE it. So a legitimate write could run to <c>WriteTotalBound</c> while every
    /// waiter had already given up — and the failure then read
    /// <c>System.TimeoutException : The operation has timed out.</c>, verbatim what #3477 was filed
    /// on, with <c>OwnerUnreachable … corr=</c> discarded unread.</para>
    ///
    /// <para><b>Why this assertion is the control rather than a config echo.</b> It does not
    /// restate a number: it recomputes the worst case from the SAME constants the re-enqueue path
    /// spends and asserts the wait dominates it. Pointing <c>WriteConvergence</c> back at
    /// <c>WriteVerdictBound</c> — the pre-#3477 derivation — fails it at every factor, because 208
    /// s of legitimate write does not fit inside a 36 s wait. And it fails for the RIGHT reason: the
    /// message prints both numbers and names the re-enqueue chain.</para>
    ///
    /// <para>🚨 The composition is ADDITIVE, which is the mistake this issue made about itself: the
    /// terms were once read as alternatives (take the maximum) and the region between the two
    /// bounds then looks impossible rather than merely silent.</para>
    /// </summary>
    [Theory]
    [InlineData(null)]      // local
    [InlineData("1")]
    [InlineData("3")]
    [InlineData("10")]
    public void AWriteWaitDominatesWhatAReEnqueueActuallyCosts(string? factor)
    {
        using var _ = new EnvironmentVariable("GITHUB_ACTIONS", factor is null ? null : "true");
        using var __ = new EnvironmentVariable("MW_TEST_TIMEOUT_FACTOR", factor);

        // Recomposed from the constants the path spends, NOT copied from WriteTotalBound — a
        // control that reads the value under test cannot fail when that value is wrong.
        var perAttemptVerdict = LatePatchResponseRegistry.WriteVerdictBound;
        var worstCase = MeshNodeStreamHandle.BaseStateWaitBound + perAttemptVerdict
            + MeshNodeStreamHandle.MaxOwnerDisposingReenqueues
              * (MeshNodeStreamHandle.BaseStateWaitBound
                 + MeshNodeStreamHandle.DefaultNodeReadBudget
                 + perAttemptVerdict);

        Assert.True(
            LatePatchResponseRegistry.WriteTotalBound >= worstCase,
            $"WriteTotalBound ({LatePatchResponseRegistry.WriteTotalBound.TotalSeconds:0.##}s) must "
            + $"cover what the re-enqueue path actually spends ({worstCase.TotalSeconds:0.##}s): "
            + "attempt 0's base read + verdict, then one base read + authoritative re-read + a FRESH "
            + "verdict per re-attempt. If this fails, the published total has drifted from the path "
            + "— fix the derivation, never the number.");

        Assert.True(
            TestTimeouts.WriteConvergence > worstCase,
            $"a wait on a write that can RE-ENQUEUE ({TestTimeouts.WriteConvergence.TotalSeconds:0.##}s) "
            + $"must exceed what such a write legitimately costs ({worstCase.TotalSeconds:0.##}s), or "
            + "it expires while UpdateRemote's own terminal is still due and the failure reads as an "
            + "anonymous TimeoutException instead of OwnerUnreachable — #3477. 🚨 Convergence "
            + $"({TestTimeouts.Convergence.TotalSeconds:0.##}s) is the PER-ATTEMPT wait and is NOT a "
            + "substitute here; that substitution is the defect this test exists to refuse.");

        Assert.True(
            TestTimeouts.WriteConvergence > TestTimeouts.Convergence,
            "the re-enqueue wait must be the longer of the two — if they are equal, one of them is "
            + "not derived from the bound it claims to be derived from");
    }

    /// <summary>
    /// 🚨 The xunit kill must dominate the inner wait for the WRITE scale too — the same invariant
    /// <c>TestMilliseconds</c> states, at the scale a re-enqueueing test uses. An attribute argument
    /// must be a compile-time constant, so <c>LateNackReenqueueTest</c> writes a literal; this is
    /// what stops that literal from silently falling below the wait again. It has now done so twice:
    /// at 90_000 (below Convergence) and at 240_000 (below WriteConvergence), and both times the
    /// result was a kill with no assertion and no named wait.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("1")]
    [InlineData("3")]
    public void TheOuterKillDominatesTheInnerWait(string? factor)
    {
        using var _ = new EnvironmentVariable("GITHUB_ACTIONS", factor is null ? null : "true");
        using var __ = new EnvironmentVariable("MW_TEST_TIMEOUT_FACTOR", factor);

        // Every re-enqueue-covering test carries this literal: LateNackReenqueueTest,
        // LateNackReenqueueCorrelationTest and AWriteDuringItsOwnersRecycleLandsTest (its terminal
        // wait is TestTimeouts.WriteConvergence). One constant, because they must not drift apart.
        const int reEnqueueTestTimeoutMs = 600_000;
        Assert.True(
            reEnqueueTestTimeoutMs > TestTimeouts.WriteTestMilliseconds,
            $"the re-enqueue tests' [Fact(Timeout = {reEnqueueTestTimeoutMs})] must exceed "
            + $"TestTimeouts.WriteTestMilliseconds ({TestTimeouts.WriteTestMilliseconds}), or xunit "
            + "kills the test before its inner wait can report what it was waiting for. Raise the "
            + "literal in EVERY such file — do not lower this bound.");
    }

    /// <summary>The ordering of the three convergence scales holds wherever it runs.</summary>
    [Fact]
    public void TheScalesAreOrdered()
    {
        Assert.True(TestTimeouts.Quick < TestTimeouts.Convergence);
        Assert.True(TestTimeouts.Convergence < TestTimeouts.CrossSilo);
    }

    /// <summary>CI is slower than the machine the 30 s literal was chosen on; that is the point.</summary>
    [Fact]
    public void CiWaitsLongerThanLocal()
    {
        TimeSpan local, ci;
        using (var _ = new EnvironmentVariable("CI", null))
        using (var __ = new EnvironmentVariable("GITHUB_ACTIONS", null))
            local = TestTimeouts.Convergence;
        using (var _ = new EnvironmentVariable("GITHUB_ACTIONS", "true"))
        using (var __ = new EnvironmentVariable("MW_TEST_TIMEOUT_FACTOR", null))
            ci = TestTimeouts.Convergence;

        Assert.True(ci > local, $"CI ({ci}) must wait longer than local ({local}).");
    }

    /// <summary>
    /// 🚨 A malformed override must NOT silently become 1.0 — that would quietly restore the exact
    /// bound this type exists to widen, and it would do so only on CI, where nobody would see it.
    /// </summary>
    [Theory]
    [InlineData("not-a-number")]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-2")]
    public void AMalformedFactorFallsBackToTheDefaultRatherThanToOne(string bad)
    {
        using var _ = new EnvironmentVariable("GITHUB_ACTIONS", "true");
        using var __ = new EnvironmentVariable("MW_TEST_TIMEOUT_FACTOR", bad);

        TimeSpan local;
        using (var ___ = new EnvironmentVariable("GITHUB_ACTIONS", null))
        using (var ____ = new EnvironmentVariable("CI", null))
            local = TestTimeouts.Convergence;

        using var _____ = new EnvironmentVariable("GITHUB_ACTIONS", "true");
        Assert.True(
            TestTimeouts.Convergence > local,
            $"a malformed MW_TEST_TIMEOUT_FACTOR ('{bad}') must not collapse the CI bound back to "
            + "the local one — that restores the defect silently, and only on CI.");
    }

    /// <summary>Sets an environment variable for a scope and restores whatever was there.</summary>
    private sealed class EnvironmentVariable : IDisposable
    {
        private readonly string name;
        private readonly string? previous;

        public EnvironmentVariable(string name, string? value)
        {
            this.name = name;
            previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(name, previous);
    }

    /// <summary>
    /// 🚨 THE SAME INVARIANT, FOR THE TESTS THAT DECLARE NO BOUND OF THEIR OWN (#4740).
    ///
    /// <para><see cref="TheOuterBoundAlwaysExceedsTheInner"/> pins it for a test that writes
    /// <c>[Fact(Timeout = …)]</c>. The other 5,202 write a plain <c>[Fact]</c> and are bounded by
    /// the runner-wide <c>methodTimeout</c> — which was a literal <b>30 000</b> in both config
    /// files, BELOW every shared budget: <c>Convergence</c> is 36 s locally and 108 s on CI. So a
    /// wait that elapsed was killed by the runner before it could report what it was waiting for,
    /// and every such failure read <c>Test execution timed out after 30000 milliseconds</c> with
    /// no other line. Three different bugs — the disposal never completed, the refusal never
    /// arrived, the fixture wedged — wearing one message.</para>
    ///
    /// <para><b>Why the literal is pinned to the DEFAULT CI factor and not to whatever
    /// <c>MW_TEST_TIMEOUT_FACTOR</c> says.</b> A JSON literal cannot scale; the scaled value is
    /// what <see cref="TestTimeouts.TestMilliseconds"/> already computes. So the config is checked
    /// against the factor CI actually runs with, and raising <c>MW_TEST_TIMEOUT_FACTOR</c> above
    /// it is a change that has to move these files too — which is what this assertion says when it
    /// fails, rather than leaving the pair to drift the way the comment in
    /// <c>HubFactAttribute</c> did.</para>
    ///
    /// <para>EVERY config in the tree is checked, not just this assembly's: a project shipping its
    /// own <c>xunit.runner.json</c> to opt into parallelism (see <c>test/Directory.Build.props</c>)
    /// takes its <c>methodTimeout</c> from that copy, so a guard reading only the shared default
    /// would pass while a project with its own file stayed at 30 s.</para>
    /// </summary>
    [Fact]
    public void EveryRunnerConfigBoundsAWaitThatCanActuallyReport()
    {
        using var _ = new EnvironmentVariable("GITHUB_ACTIONS", "true");
        using var __ = new EnvironmentVariable("MW_TEST_TIMEOUT_FACTOR", null);
        var required = TestTimeouts.DefaultOuterBoundMilliseconds;

        // 🚨 EVERY shared budget, with no exception — including WriteConvergence, the largest.
        // Leaving it out was the #4740 review's finding: six plain [Fact] tests await it with no
        // timeout of their own (DeletePreflightNamesTheSilentDescendantTest among them), so a cap
        // that cleared only CrossSilo still killed them before their assertion fired. A budget
        // absent from this list is a budget nothing is checking.
        foreach (var (name, budget) in new (string, TimeSpan)[]
                 {
                     (nameof(TestTimeouts.Quick), TestTimeouts.Quick),
                     (nameof(TestTimeouts.Convergence), TestTimeouts.Convergence),
                     (nameof(TestTimeouts.CrossSilo), TestTimeouts.CrossSilo),
                     (nameof(TestTimeouts.WriteConvergence), TestTimeouts.WriteConvergence),
                 })
            Assert.True(TestTimeouts.DefaultOuterBound > budget,
                $"{name} ({budget}) is not STRICTLY dominated by the default outer bound "
                + $"({TestTimeouts.DefaultOuterBound}) — a cap equal to a budget kills the wait at "
                + "the instant it expires, which reports nothing.");

        var root = RepoPaths.Root;
        var configs = Directory
            .GetFiles(Path.Combine(root, "test"), "xunit.runner.json", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        // The denominator, stated: a sweep that found no config would otherwise pass having
        // checked nothing — the shape this whole file exists to stop.
        Assert.True(configs.Count > 0, $"no xunit.runner.json found under {root}/test — this guard "
                                       + "would pass having checked nothing.");

        foreach (var config in configs)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(config));
            Assert.True(
                doc.RootElement.TryGetProperty("methodTimeout", out var declared),
                $"{config} declares no methodTimeout, so xUnit's own default governs it and this "
                + "guard cannot say what bounds those tests.");
            Assert.True(
                declared.GetInt32() >= required,
                $"{config} caps a test at {declared.GetInt32()} ms while the largest budget a "
                + $"plain [Fact] waits on is {required} ms on CI — so the wait is killed before it "
                + "can name what it was waiting for, and the failure is anonymous by construction.");
        }
    }

    /// <summary>
    /// 🚨 <c>HubFactAttribute</c> carries its OWN <c>Timeout</c>, so raising the runner-wide
    /// <c>methodTimeout</c> alone would have changed nothing for a <c>[HubFact]</c> — which is
    /// exactly the test #4741 is about. It used to restate <c>30000</c> under a comment reading
    /// "30s matches the runner-wide <c>methodTimeout</c>": a number kept in step by a sentence,
    /// which is the thing this file exists to replace.
    ///
    /// <para>In DEBUG the attribute deliberately applies NO timeout (unattended breakpoint
    /// debugging), and that is asserted here too rather than compiled away — a <c>#if</c> that
    /// skipped the case would leave the guard vacuous in exactly the configuration a developer
    /// runs locally.</para>
    /// </summary>
    [Theory]
    [InlineData(null)]      // local
    [InlineData("1")]
    [InlineData("3")]
    [InlineData("10")]
    public void HubFactTakesItsOuterBoundFromTheSameSource(string? factor)
    {
        using var _ = new EnvironmentVariable("GITHUB_ACTIONS", factor is null ? null : "true");
        using var __ = new EnvironmentVariable("MW_TEST_TIMEOUT_FACTOR", factor);

#if DEBUG
        Assert.True(new HubFactAttribute().Timeout == 0,
            "in DEBUG the attribute must apply no timeout at all, so a breakpoint can be held.");
#else
        Assert.Equal(TestTimeouts.DefaultOuterBoundMilliseconds, new HubFactAttribute().Timeout);
        Assert.True(new HubFactAttribute().Timeout > TestTimeouts.Convergence.TotalMilliseconds,
            "a [HubFact] whose own Timeout is at or below the convergence wait can only ever fail "
            + "anonymously — the attribute kills the method before the wait reports.");
#endif
    }
    /// <summary>
    /// 🚨 THE BOUND IS REAL, not a config literal (MeshWeaver.Plugins#4424). xunit.v3 has no
    /// <c>methodTimeout</c> setting and dropped the key silently, so every plain <c>[Fact]</c> ran
    /// unbounded and the guard above checked a number nothing read. The execution host
    /// (<see cref="MethodBound"/>) now applies it. Here, in THIS run, a plain <c>[Fact]</c> carries
    /// the configured bound as its timeout (it was 0 before).
    /// </summary>
    [Fact]
    public void APlainFactRunsUnderTheConfiguredBound()
    {
        var test = TestContext.Current.Test as IXunitTest;
        Assert.NotNull(test);
        var configured = MethodBound.Resolve(test.TestCase.TestMethod.TestClass.TestCollection.TestAssembly);
        Assert.True(configured >= TestTimeouts.DefaultOuterBoundMilliseconds,
            $"the bound resolved to {configured} ms, below the default outer bound: the config this run reads lost its methodTimeout");
        Assert.Equal(configured, test.Timeout);
    }

    private const int ProbeBoundMilliseconds = 1500;

    /// <summary>
    /// 🚨 THE BOUND, MEASURED END TO END. This assembly is run again as a child process with a 1.5 s
    /// bound (<see cref="MethodBound.EnvironmentOverride"/>), only the explicit cases of
    /// <see cref="MethodBoundProbe"/>:
    /// <list type="bullet">
    ///   <item>a plain <c>[Fact]</c> that AWAITS past the bound fails BY NAME at the bound. Before the
    ///         host applied the bound it passed, two minutes later (the failing-first case);</item>
    ///   <item>a plain <c>[Fact]</c> that HOLDS ITS THREAD synchronously (the #4424 shape) fails at
    ///         the bound too, and leaves a dump and a <c>TEST_TIMEOUT</c> trace line naming it, because
    ///         a timeout can abandon that thread but not unblock it;</item>
    ///   <item>a fast test PASSES: the negative control, the bound fails only what outlives it.</item>
    /// </list>
    /// The child must end long before the two minutes the stuck probes ask for.
    /// </summary>
    [Fact]
    public void ATestThatOutlivesTheBound_FailsByNameAtTheBound_AndLeavesADump()
    {
        var run = RunProbes(typeof(MethodBoundProbe));
        var stdout = run.Stdout;
        var transcript = run.Transcript;

        Assert.True(run.Exited, $"the probe run was still going after {run.Elapsed.TotalSeconds:F0} s: the stuck probes were NOT ended at the {ProbeBoundMilliseconds} ms bound.\n{transcript}");
        Assert.NotEqual(0, run.ExitCode);
        foreach (var stuck in new[] { nameof(MethodBoundProbe.AwaitsPastTheBound), nameof(MethodBoundProbe.HoldsItsThreadSynchronously) })
            Assert.True(stdout.Contains($"{nameof(MethodBoundProbe)}.{stuck} [FAIL]", StringComparison.Ordinal),
                $"{stuck} must fail BY NAME at the bound.\n{transcript}");
        Assert.True(stdout.Split($"Test execution timed out after {ProbeBoundMilliseconds} milliseconds").Length - 1 == 2,
            $"both stuck probes must fail with xunit's timeout at {ProbeBoundMilliseconds} ms.\n{transcript}");
        Assert.False(stdout.Contains($"{nameof(MethodBoundProbe.ReturnsAtOnce)} [FAIL]", StringComparison.Ordinal),
            $"the fast probe must pass.\n{transcript}");
        Assert.True(stdout.Contains("Total: 3, Errors: 0, Failed: 2", StringComparison.Ordinal), transcript);

        var lines = run.TimeoutLines();
        Assert.True(lines.Length == 2, $"expected two {TimeoutWitness.Phase} lines.\n{string.Join('\n', lines)}\n{transcript}");
        Assert.Contains(lines, l => l.Contains(
            $"[{nameof(MethodBoundProbe)}] {TimeoutWitness.Phase} {nameof(MethodBoundProbe.HoldsItsThreadSynchronously)} bound={ProbeBoundMilliseconds}ms dump={run.Dumps.FullName}",
            StringComparison.Ordinal));
        var dumpLines = File.ReadAllLines(run.Trace)
            .Where(l => l.Contains($"] {TimeoutWitness.DumpPhase} ", StringComparison.Ordinal)).ToArray();
        Assert.True(dumpLines.Length == 2 && dumpLines.All(l => l.Contains($" dump={run.Dumps.FullName}", StringComparison.Ordinal)),
            $"each timed-out probe must report its dump as written on a {TimeoutWitness.DumpPhase} line.\n{string.Join('\n', dumpLines)}\n{transcript}");
        var written = run.Dumps.GetFiles("test-timeout-*.dmp");
        Assert.True(written.Length == 2 && written.All(f => f.Length > 0),
            $"each timed-out probe must leave a non-empty dump in {run.Dumps.FullName}; found {written.Length}.\n{string.Join('\n', lines)}");

        run.Scratch.Delete(recursive: true);
    }

    /// <summary>
    /// 🚨 THE BOUND REACHES EVERY CASE SHAPE, AND A DECLARED TIMEOUT IS KEPT. The same child run over
    /// <see cref="MethodBoundCaseShapeProbe"/>:
    /// <list type="bullet">
    ///   <item>a PRE-ENUMERATED theory row (<c>[InlineData]</c>) that awaits past the bound fails at the
    ///         bound, while its fast sibling row passes (each row keeps its own verdict);</item>
    ///   <item>a DELAY-ENUMERATED theory row (<c>DisableDiscoveryEnumeration</c>, built at run time from
    ///         the one case <see cref="MethodBound"/> bounded) fails at the bound, its fast row passes;</item>
    ///   <item>a <c>[Fact(Timeout = …)]</c> keeps ITS timeout, longer than the bound: it fails at its own
    ///         value, and the <see cref="TimeoutWitness"/> line reports that value, not the assembly bound.</item>
    /// </list>
    /// </summary>
    [Fact]
    public void EveryCaseShapeIsBounded_AndADeclaredTimeoutIsKept()
    {
        var run = RunProbes(typeof(MethodBoundCaseShapeProbe));
        var stdout = run.Stdout;
        var transcript = run.Transcript;
        var failing = stdout.Split('\n').Where(l => l.Contains("[FAIL]", StringComparison.Ordinal)).ToArray();

        Assert.True(run.Exited, $"the probe run was still going after {run.Elapsed.TotalSeconds:F0} s: a probe was NOT ended at its timeout.\n{transcript}");
        Assert.True(stdout.Contains("Total: 5, Errors: 0, Failed: 3", StringComparison.Ordinal), transcript);
        foreach (var row in new[] { nameof(MethodBoundCaseShapeProbe.PreEnumeratedRow), nameof(MethodBoundCaseShapeProbe.DelayEnumeratedRow) })
            Assert.True(failing.Count(l => l.Contains($"{nameof(MethodBoundCaseShapeProbe)}.{row}(", StringComparison.Ordinal)) == 1,
                $"exactly the slow {row} row must fail BY NAME.\n{transcript}");
        Assert.True(failing.Count(l => l.Contains($"{nameof(MethodBoundCaseShapeProbe)}.{nameof(MethodBoundCaseShapeProbe.DeclaresItsOwnTimeout)} [FAIL]", StringComparison.Ordinal)) == 1,
            $"the probe with its own timeout must fail BY NAME.\n{transcript}");
        Assert.True(stdout.Split($"Test execution timed out after {ProbeBoundMilliseconds} milliseconds").Length - 1 == 2,
            $"both slow theory rows must fail at the {ProbeBoundMilliseconds} ms bound.\n{transcript}");
        Assert.True(stdout.Split($"Test execution timed out after {OwnTimeoutMilliseconds} milliseconds").Length - 1 == 1,
            $"the declared {OwnTimeoutMilliseconds} ms timeout must be kept, not replaced by the bound.\n{transcript}");

        var lines = run.TimeoutLines();
        Assert.True(lines.Length == 3, $"expected three {TimeoutWitness.Phase} lines.\n{string.Join('\n', lines)}\n{transcript}");
        Assert.Contains(lines, l => l.Contains(
            $"[{nameof(MethodBoundCaseShapeProbe)}] {TimeoutWitness.Phase} {nameof(MethodBoundCaseShapeProbe.DeclaresItsOwnTimeout)} bound={OwnTimeoutMilliseconds}ms ",
            StringComparison.Ordinal));
        Assert.True(lines.Count(l => l.Contains($" bound={ProbeBoundMilliseconds}ms ", StringComparison.Ordinal)) == 2,
            $"the two theory rows must be reported at the {ProbeBoundMilliseconds} ms bound.\n{string.Join('\n', lines)}");

        run.Scratch.Delete(recursive: true);
    }

    /// <summary>The declared timeout of <see cref="MethodBoundCaseShapeProbe.DeclaresItsOwnTimeout"/>, above the probe bound.</summary>
    public const int OwnTimeoutMilliseconds = 4000;

    private sealed record ProbeRun(bool Exited, int ExitCode, TimeSpan Elapsed, string Stdout, string Transcript,
        DirectoryInfo Scratch, DirectoryInfo Dumps, string Trace)
    {
        public string[] TimeoutLines()
        {
            Assert.True(File.Exists(Trace), $"no {TimeoutWitness.Phase} trace at {Trace}.\n{Transcript}");
            return File.ReadAllLines(Trace).Where(l => l.Contains($" {TimeoutWitness.Phase} ", StringComparison.Ordinal)).ToArray();
        }
    }

    /// <summary>
    /// Runs this assembly again as a child process, only the explicit cases of <paramref name="probes"/>,
    /// under the <see cref="ProbeBoundMilliseconds"/> bound, with its own dump directory and trace.
    /// </summary>
    private static ProbeRun RunProbes(Type probes)
    {
        var self = typeof(TestTimeoutsTest).Assembly.Location;
        var scratch = Directory.CreateTempSubdirectory("mw-bound-");
        var dumps = Directory.CreateDirectory(Path.Combine(scratch.FullName, "dumps"));
        var trace = Path.Combine(scratch.FullName, "trace.log");
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(self)!,
        };
        foreach (var arg in new[] { self, "-class", probes.FullName!, "-explicit", "only", "-noColor" })
            psi.ArgumentList.Add(arg);
        psi.Environment[MethodBound.EnvironmentOverride] = ProbeBoundMilliseconds.ToString(CultureInfo.InvariantCulture);
        psi.Environment[TimeoutWitness.DumpDirectoryOverride] = dumps.FullName;
        psi.Environment[TimeoutWitness.TraceOverride] = trace;

        var output = new ConcurrentQueue<string>();
        var errors = new ConcurrentQueue<string>();
        using var child = new Process { StartInfo = psi };
        child.OutputDataReceived += (_, e) => { if (e.Data is not null) output.Enqueue(e.Data); };
        child.ErrorDataReceived += (_, e) => { if (e.Data is not null) errors.Enqueue(e.Data); };
        var clock = Stopwatch.StartNew();
        child.Start();
        child.BeginOutputReadLine();
        child.BeginErrorReadLine();
        // Far below the probes' two minutes: a child still running here waited them out, i.e. no bound.
        var exited = child.WaitForExit(TimeSpan.FromSeconds(60));
        if (!exited)
            child.Kill(entireProcessTree: true); // this test's own child, ended so it does not outlive the verdict
        child.WaitForExit(); // drains the redirected streams
        var stdout = string.Join('\n', output);
        return new ProbeRun(exited, child.ExitCode, clock.Elapsed, stdout,
            $"stdout:\n{stdout}\nstderr:\n{string.Join('\n', errors)}", scratch, dumps, trace);
    }
}

/// <summary>
/// The cases <see cref="TestTimeoutsTest.ATestThatOutlivesTheBound_FailsByNameAtTheBound_AndLeavesADump"/>
/// runs in a child process. EXPLICIT, so an ordinary run (CI's included) never executes them.
/// </summary>
public class MethodBoundProbe
{
    /// <summary>Awaits two minutes, far past the probe bound, ignoring the test's token.</summary>
    [Fact(Explicit = true)]
    public async Task AwaitsPastTheBound() => await Task.Delay(TimeSpan.FromMinutes(2), CancellationToken.None);

    /// <summary>Holds its thread for two minutes inside a synchronous call: the #4424 shape.</summary>
    [Fact(Explicit = true)]
    public void HoldsItsThreadSynchronously() => Thread.Sleep(TimeSpan.FromMinutes(2));

    /// <summary>The negative control.</summary>
    [Fact(Explicit = true)]
    public void ReturnsAtOnce() => Assert.Equal(2, 1 + 1);
}

/// <summary>
/// The cases <see cref="TestTimeoutsTest.EveryCaseShapeIsBounded_AndADeclaredTimeoutIsKept"/> runs in a
/// child process: both theory shapes, and a test with its own timeout. EXPLICIT, so an ordinary run
/// never executes them.
/// </summary>
public class MethodBoundCaseShapeProbe
{
    /// <summary>The rows of <see cref="DelayEnumeratedRow"/>: 0 returns at once, 120 awaits two minutes.</summary>
    public static TheoryData<int> Seconds => new(0, 120);

    /// <summary>A pre-enumerated theory: one case per <c>[InlineData]</c> row.</summary>
    /// <param name="seconds">How long the row awaits.</param>
    [Theory(Explicit = true)]
    [InlineData(0)]
    [InlineData(120)]
    public async Task PreEnumeratedRow(int seconds) => await Task.Delay(TimeSpan.FromSeconds(seconds), CancellationToken.None);

    /// <summary>A delay-enumerated theory: ONE case, whose rows xunit builds only when it runs.</summary>
    /// <param name="seconds">How long the row awaits.</param>
    [Theory(Explicit = true, DisableDiscoveryEnumeration = true)]
    [MemberData(nameof(Seconds))]
    public async Task DelayEnumeratedRow(int seconds) => await Task.Delay(TimeSpan.FromSeconds(seconds), CancellationToken.None);

    /// <summary>Declares a timeout longer than the probe bound, and awaits past it.</summary>
    [Fact(Explicit = true, Timeout = TestTimeoutsTest.OwnTimeoutMilliseconds)]
    public async Task DeclaresItsOwnTimeout() => await Task.Delay(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
}
