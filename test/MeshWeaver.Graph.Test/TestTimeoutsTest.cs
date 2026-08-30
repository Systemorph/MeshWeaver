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
