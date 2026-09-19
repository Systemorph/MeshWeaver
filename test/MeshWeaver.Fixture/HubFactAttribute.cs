using Xunit;

namespace MeshWeaver.Fixture;

#if DEBUG
/// <summary>
/// Marks a hub test method. In DEBUG builds no timeout is applied, allowing
/// unattended breakpoint debugging of hub message flow.
/// </summary>
public class HubFactAttribute : FactAttribute;
#else
/// <summary>
/// Marks a hub test method. In non-DEBUG builds a timeout is applied so that a wedged hub fails
/// the test instead of hanging the run.
///
/// <para>🚨 The budget must exceed what the MESH FIXTURE costs on a loaded CI runner, or the test
/// measures the runner instead of the code. It was 5s, and 5s is not achievable there: a CI log for
/// <c>MeshNodeVersionSyncTest</c> (2026-07-27) shows class init at 19:59:22.595 and dispose at
/// 19:59:30.720 — <b>8.1s of fixture</b> before the body runs at all. That produced a recurring
/// main-red flake with no defect behind it, and the same trap bit a heartbeat test written the same
/// night ("Test execution timed out after 5000 milliseconds", passing locally where no budget is
/// enforced).</para>
///
/// <para>🚨 <b>It is DERIVED from <see cref="TestTimeouts.DefaultOuterBound"/>, not restated as a
/// literal (#4740).</b> It used to read <c>Timeout = 30000</c> under a comment saying "30s matches
/// the runner-wide <c>methodTimeout</c>" — a number kept in step by a sentence. Both were below
/// every shared budget a test actually waits on (<c>Convergence</c> is 36 s locally and 108 s on
/// CI), so a <c>[HubFact]</c> whose wait elapsed was killed by its own attribute BEFORE the wait
/// could report what it was waiting for. Every such failure read
/// <c>Test execution timed out after 30000 milliseconds</c> and named nothing — three different
/// bugs wearing one message.</para>
///
/// <para>This is the invariant <see cref="TestTimeouts.TestMilliseconds"/> already states for an
/// explicit <c>[Fact(Timeout = …)]</c> — the outer bound must DOMINATE the inner wait — applied to
/// the place that never had it. An attribute ARGUMENT must be a compile-time constant, but this is
/// set in the constructor BODY, so it can take the scaled value: 108 s locally, 252 s on CI.</para>
/// </summary>
public sealed class HubFactAttribute : FactAttribute
{
    /// <summary>
    /// Initializes a new instance and sets the test timeout to
    /// <see cref="TestTimeouts.DefaultOuterBound"/> — the bound for a test that declares none, so
    /// it dominates every budget a plain <c>[Fact]</c> can wait on, at every scale.
    /// </summary>
    /// <param name="sourceFilePath">Supplied by the compiler; xunit v3 records it on the test case.</param>
    /// <param name="sourceLineNumber">Supplied by the compiler; xunit v3 records it on the test case.</param>
    public HubFactAttribute(
        [System.Runtime.CompilerServices.CallerFilePath] string? sourceFilePath = null,
        [System.Runtime.CompilerServices.CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Timeout = TestTimeouts.DefaultOuterBoundMilliseconds;
    }
};
#endif
