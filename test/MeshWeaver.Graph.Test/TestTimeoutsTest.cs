#pragma warning disable CS1591

using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using Xunit;

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

        // Both re-enqueue-covering tests carry this literal: LateNackReenqueueTest and
        // LateNackReenqueueCorrelationTest. One constant, because they must not drift apart.
        const int reEnqueueTestTimeoutMs = 600_000;
        Assert.True(
            reEnqueueTestTimeoutMs > TestTimeouts.WriteTestMilliseconds,
            $"the re-enqueue tests' [Fact(Timeout = {reEnqueueTestTimeoutMs})] must exceed "
            + $"TestTimeouts.WriteTestMilliseconds ({TestTimeouts.WriteTestMilliseconds}), or xunit "
            + "kills the test before its inner wait can report what it was waiting for. Raise the "
            + "literal in BOTH files — do not lower this bound.");
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
}
