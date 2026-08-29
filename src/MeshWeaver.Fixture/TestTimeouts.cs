namespace MeshWeaver.Fixture;

/// <summary>
/// The one place a test waiting time is decided.
///
/// <para>🚨 <b>A convergence has no deadline of its own.</b> A reconnect-and-drain, a projection
/// settling, a cancellation restarting pending rounds — none of these promises to finish in N
/// seconds. Every bound on one is a guess about how fast the machine is, and a guess written as a
/// literal is a guess that cannot be revisited.</para>
///
/// <para>The literal that was written is <c>30 s</c>, roughly 2,679 times across the two
/// repositories. On 2026-08-29 six failures landed at 30–33 s in a single evening, in different
/// tests, different suites and different repos — because the same guess had been copied everywhere
/// and CI is slower than the laptop it was made on. There is a documented ~1.7× CI/local ratio,
/// so a 30 s local bound leaves about 18 s of CI headroom, and under runner contention that is
/// gone.</para>
///
/// <para>🚨 <b>The inner bound must be strictly less than the outer one, and that is why both live
/// here.</b> 24 files carry <c>[Fact(Timeout = 30000)]</c> AND a 30 s internal wait: xunit kills
/// the test at the exact moment the wait would have expired, so the assertion never fires and the
/// failure is reported as an anonymous timeout instead of naming what did not converge. Scaling
/// only the inner bound changes nothing in precisely the files that need it. Deriving both from
/// one factor keeps the ordering by construction rather than by whoever writes the next test
/// remembering it.</para>
/// </summary>
public static class TestTimeouts
{
    /// <summary>Local baseline for one convergence wait. Every other value is derived from it.</summary>
    private static readonly TimeSpan LocalConvergence = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How much slower CI is assumed to be. Overridable with <c>MW_TEST_TIMEOUT_FACTOR</c> so the
    /// number can be tuned against evidence — a shared bound is only an improvement on a literal
    /// if it can actually be changed in one place.
    /// </summary>
    private const double DefaultCiFactor = 3.0;

    /// <summary>
    /// True on a CI runner. Both variables are checked: <c>CI</c> is the convention, and
    /// <c>GITHUB_ACTIONS</c> is what this fleet's runners actually set.
    /// </summary>
    public static bool IsContinuousIntegration =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"))
        || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"));

    private static double Factor
    {
        get
        {
            if (!IsContinuousIntegration)
                return 1.0;
            var raw = Environment.GetEnvironmentVariable("MW_TEST_TIMEOUT_FACTOR");
            // A malformed override must not silently become 1.0 — that would quietly restore the
            // very bound this type exists to widen. Fall back to the default instead.
            return double.TryParse(raw, out var parsed) && parsed > 0 ? parsed : DefaultCiFactor;
        }
    }

    /// <summary>
    /// How long to wait for a convergence that has no deadline of its own. Use this instead of
    /// writing a literal.
    /// </summary>
    public static TimeSpan Convergence => LocalConvergence * Factor;

    /// <summary>
    /// The value for <c>[Fact(Timeout = …)]</c>, in milliseconds — deliberately LARGER than
    /// <see cref="Convergence"/> so an inner wait can lose first and report what it was waiting
    /// for. A test whose xunit timeout equals its internal wait can only ever fail anonymously.
    /// </summary>
    public static int TestMilliseconds => (int)(Convergence * OuterMargin).TotalMilliseconds;

    /// <summary>
    /// The gap between the inner and outer bound. 2× is deliberate rather than tight: the outer
    /// bound exists to stop a WEDGE, not to police a slow convergence, so it should be nowhere
    /// near the inner one.
    /// </summary>
    private const double OuterMargin = 2.0;

    /// <summary>A convergence expected to be quick — a local projection, a cached read.</summary>
    public static TimeSpan Quick => Convergence / 3;

    /// <summary>
    /// A convergence crossing a silo or a real network hop, where the platform's own request
    /// timeout (60 s) is the thing being waited on rather than a local settle.
    /// </summary>
    public static TimeSpan CrossSilo => Convergence * 2;
}
