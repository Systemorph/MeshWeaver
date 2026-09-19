using MeshWeaver.Mesh;

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
    /// <summary>
    /// 🚨 The framework's own outer bound on a caller-visible write — the instant
    /// <c>UpdateRemote</c> gives up and reports <c>OwnerUnreachable</c>. A test wait must DOMINATE
    /// it, never equal it.
    ///
    /// <para>The old baseline was a literal <c>30 s</c>, which is exactly
    /// <c>LateResponseWatchBound</c> and one second below this. A test awaiting a mesh write
    /// therefore gave up one second BEFORE the framework produced its diagnosis, every single time
    /// — so the failure always read "the observable emitted nothing at all" and never
    /// "OwnerUnreachable: the owner produced no terminal for this patch". The bound was placed at
    /// precisely the value that destroys the most information (#2819).</para>
    /// </summary>
    private static TimeSpan FrameworkWriteBound => LatePatchResponseRegistry.WriteVerdictBound;

    /// <summary>
    /// 🚨 The framework's outer bound on a write INCLUDING its re-attempts. Only
    /// <see cref="WriteConvergence"/> derives from it; <see cref="Convergence"/> deliberately does
    /// not — see the rule there.
    /// </summary>
    private static TimeSpan FrameworkWriteTotalBound => LatePatchResponseRegistry.WriteTotalBound;

    /// <summary>
    /// Local baseline for one convergence wait. Every other value is derived from it — and it is
    /// itself derived from <see cref="FrameworkWriteBound"/> rather than written as a literal, so
    /// the ordering holds by construction instead of by whoever writes the next test remembering
    /// it.
    ///
    /// <para>The slack is ADDITIVE, not a ratio: what has to be covered is the propagation of one
    /// terminal — the framework produces <c>OwnerUnreachable</c> at the write bound and it has to
    /// reach the assertion — and that cost does not scale with the bound. Five seconds is ample
    /// for it and keeps the local wait near the familiar half-minute; a multiplier would inflate
    /// every wedged test's failure time to buy the same few seconds.</para>
    /// </summary>
    private static TimeSpan LocalConvergence => FrameworkWriteBound + TimeSpan.FromSeconds(5);

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
    ///
    /// <para>🚨 <b>This is the ONE-ATTEMPT bound and it stays that way (#3477).</b> It dominates
    /// <see cref="FrameworkWriteBound"/>, which <c>UpdateRemote</c> arms per attempt. A test
    /// watching a write that can legitimately RE-ENQUEUE — an owner disposal, a recycle, a
    /// not-yet-ready activation — must use <see cref="WriteConvergence"/> instead: this bound can
    /// expire while the framework's named terminal for such a write is still due, and the failure
    /// then reads as an anonymous timeout rather than as <c>OwnerUnreachable</c>.</para>
    /// </summary>
    public static TimeSpan Convergence => LocalConvergence * Factor;

    /// <summary>
    /// 🚨 How long to wait for a write that can legitimately RE-ENQUEUE — a write whose owner may
    /// NACK it as never-applied (an owner disposal, a recycle, a not-yet-ready activation), where
    /// the re-attempt is the expected path rather than the pathological one. Use this instead of
    /// <see cref="Convergence"/> in exactly those tests, and nowhere else.
    ///
    /// <para><b>Why this exists as a SECOND bound rather than as a wider <see cref="Convergence"/>
    /// (#3477).</b> <c>WriteVerdictBound</c> is armed per ATTEMPT: a re-attempt arms a fresh one
    /// from its own post and pays its own base read outside it, so a write that re-enqueues twice
    /// can legitimately run ~3× past the bound <see cref="Convergence"/> derives from. A test
    /// watching such a write therefore expired while the framework's named terminal
    /// (<c>OwnerUnreachable</c>, carrying <c>corr=</c>) was still due, and reported an anonymous
    /// <c>TimeoutException</c> instead — the exact failure #3477 was filed on, twice.</para>
    ///
    /// <para>🚨 <b>And why <see cref="Convergence"/> was NOT simply widened to this.</b> Almost
    /// nothing re-enqueues; pointing the general wait here would multiply every WEDGED test's
    /// failure time several-fold — on CI, minutes per wedge — to buy headroom a handful of tests
    /// need. The ratchet on test bounds only moves DOWN, so that cost would be permanent. Two
    /// named bounds with a stated rule is the honest shape; one bound sized for the worst caller
    /// is not.</para>
    ///
    /// <para>🚨 <b>The slack is added to the TOTAL, not multiplied over it</b> — and that is not a
    /// saving, it is what the numbers mean. <see cref="Factor"/> exists because a convergence is
    /// machine-speed-dependent; <see cref="FrameworkWriteTotalBound"/> is not. It is composed of
    /// the framework's own wall-clock deadlines, and a slower runner does not make
    /// <c>WriteVerdictBound</c> longer — it makes the terminal take longer to PROPAGATE, which is
    /// exactly the additive cost <see cref="LocalConvergence"/> already reasons about. So this
    /// grants the total the SAME CI-scaled headroom <see cref="Convergence"/> grants one attempt
    /// (<c>Convergence - FrameworkWriteBound</c>), rather than tripling deadlines that do not
    /// scale. Multiplying instead would put this near 10 minutes on CI for no reason anyone could
    /// defend.</para>
    /// </summary>
    public static TimeSpan WriteConvergence =>
        FrameworkWriteTotalBound + (Convergence - FrameworkWriteBound);

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

    /// <summary>
    /// 🚨 The <c>[Fact(Timeout = …)]</c> value, in milliseconds, for a test whose inner wait is
    /// <see cref="WriteConvergence"/>. Same invariant as <see cref="TestMilliseconds"/> and the
    /// same reason: an xunit timeout at or below the inner wait kills the test ANONYMOUSLY, which
    /// is cause 2 of #3477 — a sighting of that shape (run 34332482683,
    /// <i>"Test execution timed out after 90000 milliseconds"</i>) carried no assertion and named
    /// no wait, and was uninterpretable by construction rather than by bad luck.
    ///
    /// <para>An attribute argument must be a compile-time constant, so a test cannot write this
    /// directly — it writes a literal and a guard asserts the literal dominates this value. That
    /// guard is the thing that keeps the pair honest; the literal alone cannot.</para>
    /// </summary>
    public static int WriteTestMilliseconds => (int)(WriteConvergence * OuterMargin).TotalMilliseconds;

    /// <summary>A convergence expected to be quick — a local projection, a cached read.</summary>
    public static TimeSpan Quick => Convergence / 3;

    /// <summary>
    /// A convergence crossing a silo or a real network hop, where the platform's own request
    /// timeout (60 s) is the thing being waited on rather than a local settle.
    /// </summary>
    public static TimeSpan CrossSilo => Convergence * 2;

    /// <summary>
    /// 🚨 THE OUTER BOUND FOR A TEST THAT DECLARES NONE — the runner-wide <c>methodTimeout</c> and
    /// <c>HubFactAttribute</c> (#4740).
    ///
    /// <para><see cref="TestMilliseconds"/> is the outer bound for a test that writes
    /// <c>[Fact(Timeout = …)]</c>. The ~5,200 that write a plain <c>[Fact]</c> are bounded by the
    /// runner instead, and that was a literal <b>30 000</b> — below every budget here. A wait that
    /// elapsed was killed before it could report, so the failure read
    /// <c>Test execution timed out after 30000 milliseconds</c> and named nothing.</para>
    ///
    /// <para>🚨 It is <see cref="CrossSilo"/> and NOT <see cref="TestMilliseconds"/> that this has
    /// to clear, and they are numerically EQUAL — both are <c>Convergence × 2</c>. Setting the cap
    /// to <see cref="TestMilliseconds"/> would leave a plain <c>[Fact]</c> waiting
    /// <see cref="CrossSilo"/> killed at exactly the instant its wait expired: the equal case this
    /// file already calls anonymous by construction, reintroduced one level up.</para>
    ///
    /// <para>The margin is ADDITIVE, for the reason <see cref="LocalConvergence"/> gives: what has
    /// to be covered is one terminal PROPAGATING to the assertion, and that cost does not scale
    /// with the bound. A ratio would put this past nine minutes on CI and every wedged test would
    /// pay it.</para>
    ///
    /// <para><b>Not a ceiling for every wait.</b> <see cref="WriteConvergence"/> is larger still
    /// (a re-enqueue legitimately costs more), and a test using it carries its own literal bounded
    /// by <see cref="WriteTestMilliseconds"/> — see that property. This bound governs the tests
    /// that declare nothing, which is what the runner's default is for.</para>
    /// </summary>
    public static TimeSpan DefaultOuterBound => CrossSilo + LocalConvergence;

    /// <summary>
    /// <see cref="DefaultOuterBound"/> in milliseconds — the value the runner configs and
    /// <c>HubFactAttribute</c> take.
    /// </summary>
    public static int DefaultOuterBoundMilliseconds => (int)DefaultOuterBound.TotalMilliseconds;
}
