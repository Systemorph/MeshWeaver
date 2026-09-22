namespace MeshWeaver.Messaging;

/// <summary>
/// 🚨 <b>THE INITIALIZATION BUDGET LADDER — the only place an initialization bound is decided.</b>
///
/// <para><b>The rule, which this repo already states twice for other paths</b> (see
/// <c>MeshOperationOptions</c> for writes and <c>ReadBudget</c> for reads): <i>a bound nested inside
/// another bound has to be able to fire FIRST, because it is the only one that knows WHICH wait
/// starved.</i> The enclosing bound can say no more than "initialization ran out of time".</para>
///
/// <para><b>What went wrong here</b> (Systemorph/MeshWeaver#1122, #1186, #2886). Initialization is a
/// nested wait — a hub's <c>DataContext</c> time-box waits on its data sources, each data source
/// waits on the <c>sync/{clientId}</c> sub-hub that serves its stream, and that sub-hub is a hub
/// with an initialization of its own. Every level of that nest took the SAME independently-written
/// constant, <b>120 s</b>: <c>MessageHub</c>'s buildup bound, <c>DataContext.InitializationTimeout</c>,
/// and the sub-hub's own copy of the first. Nothing in the code said the three were supposed to be
/// ordered, and equal-by-coincidence is not an ordering — the clocks are armed microseconds apart on
/// different action blocks, so WHICH level reports is decided by scheduling. In the event
/// consolidated on #1122 the two that fired were <b>5 ms</b> apart; when the outer wins instead, it
/// tears the inner level down as a recognized shutdown and the level that knew the answer reports
/// nothing at all.</para>
///
/// <para><b>The ladder.</b> Exactly one value is configured per hub — its
/// <c>MessageHubConfiguration.InitializationBudget</c>, which is either an explicit
/// <c>WithStartupTimeout</c> or <see cref="Root"/> for a hub with no parent. Everything nested
/// inside it is DERIVED by <see cref="Nest"/>, which is strictly contracting, so the ordering holds
/// by construction and cannot drift apart again:</para>
///
/// <code>
/// InitializationBudget            rung 1 — this hub's whole initialization, as its HOST bounds it
///   ├─ NestedInitializationBudget rung 2 — one wait inside it: the BuildupAction Concat, and the
///   │                                      DataContext time-box. Siblings over disjoint subjects.
///   └─ a hosted hub's rung 1      rung 3 — Nest(rung 2), because this hub's rung-2 waits are what
///                                          wait on the hosted hub reaching Started.
/// </code>
///
/// <para>At the default that reads <b>120 s / 115 s</b> for a hub with no parent, <b>110 s / 105 s</b>
/// for the hub it hosts, and so on down. The ROOT value is unchanged; only the derived rungs are
/// new. <b>No bound was widened and none was narrowed to make a symptom go away</b> — the change is
/// that a hang is attributable, because the level nearest the stall is now the level that reports
/// it.</para>
/// </summary>
public static class HubInitializationBudget
{
    /// <summary>
    /// Rung 1 for a hub with no parent: the upper bound on how long any initialization may run
    /// before it is declared FAILED rather than wedging forever behind a closed gate. Generous on
    /// purpose — every legitimate init, including a cold storage read, completes well inside it;
    /// only a genuine hang trips it. A hub tightens it with <c>WithStartupTimeout</c>, and every
    /// hub it hosts contracts from there through <see cref="Nest"/>.
    /// </summary>
    public static readonly TimeSpan Root = TimeSpan.FromSeconds(120);

    /// <summary>
    /// How much of an enclosing bound each nesting level hands back, so the level that encloses it
    /// has room to OBSERVE the inner failure and report it. Absolute rather than fractional because
    /// what it covers is absolute: the delay between the outer clock starting and the inner one
    /// starting (a hub construction, a post, and the inner hub's first turn reaching
    /// <c>HandleInitialize</c> on its own action block), plus the hop that carries the inner
    /// failure outward. Five seconds is far more than a healthy one of those needs.
    /// </summary>
    public static readonly TimeSpan NestingReserve = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Floor for <see cref="Nest"/>, as a fraction of the bound being nested inside. It only bites
    /// when the enclosing bound is at or below <see cref="NestingReserve"/> — the short-bound shape
    /// tests configure — where subtracting the reserve would drive a rung to zero or negative.
    /// Contracting by a fraction instead keeps the ladder positive AND strictly decreasing at any
    /// scale above <see cref="SmallestNestableBound"/>.
    /// </summary>
    public const double MinNestingFraction = 0.5;

    /// <summary>
    /// The domain on which <see cref="Nest"/> is PROVABLY contracting, and therefore the smallest
    /// bound it will nest inside. Below it the tick arithmetic stops being an ordering: halving
    /// a single tick truncates to zero, so two rungs collapse onto the same instant — the
    /// equal-bounds collision this type exists to make unrepresentable, in its worst form, since a
    /// timer at <see cref="TimeSpan.Zero"/> fires at once. It is reachable without anyone writing a
    /// silly number: the fraction floor halves per level, so a 2 s budget reaches it around the
    /// twelfth level of nesting. Mirrors <c>MeshOperationOptions.Timeout</c>'s own 1 ms domain, for
    /// the same stated reason.
    /// </summary>
    public static readonly TimeSpan SmallestNestableBound = TimeSpan.FromMilliseconds(1);

    /// <summary>
    /// The bound for an initialization wait nested one level inside <paramref name="enclosing"/>.
    /// Strictly contracting: <c>Nest(t) &lt; t</c> for every <c>t</c> in the domain, because
    /// <see cref="NestingReserve"/> is positive and <see cref="MinNestingFraction"/> is below one.
    /// That inequality — not a convention, not a comment — is what makes the inner bound the one
    /// that fires, and <see cref="SmallestNestableBound"/> is where it stops being provable.
    /// </summary>
    /// <param name="enclosing">The bound this wait runs inside.</param>
    /// <returns>A bound strictly smaller than <paramref name="enclosing"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The enclosing bound is below
    /// <see cref="SmallestNestableBound"/> — the domain on which the ladder cannot be shown to be
    /// strictly decreasing.</exception>
    public static TimeSpan Nest(TimeSpan enclosing) =>
        enclosing >= SmallestNestableBound
            ? TimeSpan.FromTicks(Math.Max(
                (enclosing - NestingReserve).Ticks,
                (long)(enclosing.Ticks * MinNestingFraction)))
            : throw new ArgumentOutOfRangeException(nameof(enclosing), enclosing,
                $"An initialization budget must be at least {SmallestNestableBound.TotalMilliseconds} ms "
                + "— the domain on which this ladder is strictly decreasing. Below it the rungs "
                + "collapse onto one instant, a nested bound can no longer fire before the bound that "
                + "encloses it, and the level that knows which wait starved never reports "
                + "(Systemorph/MeshWeaver#1122).");
}
