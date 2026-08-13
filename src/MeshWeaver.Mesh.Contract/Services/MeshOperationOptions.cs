namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Options governing mesh-level persistence operations (create, update, delete, move).
/// Registered as a singleton via <c>MeshExtensions.WithMeshOperationTimeout</c>.
///
/// <para>🚨 <b>This type owns the whole BUDGET LADDER, and it is the only place a mesh-operation
/// bound may be configured.</b> A bound nested inside another bound has to be able to fire FIRST —
/// it is the only one that knows WHICH read starved, and the enclosing bound can say no more than
/// "the operation ran out of time". Before #1198 the three levels of the delete path were three
/// independently-configured constants that all happened to read 30 s
/// (<c>MeshOperationOptions.Timeout</c> twice and
/// <c>RowLevelSecurityOptions.PermissionEstablishmentBudget</c> once), so the innermost one could
/// never win: equal budgets, and the outer clock always starts first. Equal-by-coincidence is not
/// an ordering, and nothing in the code said the three were supposed to be ordered at all.</para>
///
/// <para><b>The rule, and it holds by construction:</b> exactly ONE value is configured
/// (<see cref="Timeout"/>); every bound nested inside it is DERIVED by <see cref="Nest"/>, which is
/// strictly contracting. So <see cref="PermissionEstablishmentBudget"/> &lt;
/// <see cref="NestedTimeout"/> &lt; <see cref="Timeout"/> for every configuration, and the ladder
/// cannot drift apart again because there is nothing to drift against. At the production default
/// the rungs are <b>30 s / 25 s / 20 s</b>.</para>
/// </summary>
public sealed record MeshOperationOptions
{
    /// <summary>
    /// <b>Rung 1 — the whole mesh operation, as its CALLER bounds it.</b> Maximum wall-clock time
    /// any single mesh operation (save, delete, move) may take before the handler returns a failure
    /// response to the caller. Defaults to 30 s, comfortably below the 60 s hub
    /// <c>RequestTimeout</c> that would otherwise be the only terminal.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The timeout is below a millisecond — the
    /// domain on which <see cref="Nest"/> is provably contracting. Sub-millisecond budgets are
    /// nonsense for a mesh operation and would let integer truncation collapse two rungs onto the
    /// same tick.</exception>
    public TimeSpan Timeout
    {
        get => timeout;
        init => timeout = value >= TimeSpan.FromMilliseconds(1)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value,
                "MeshOperationOptions.Timeout must be at least 1 ms — the domain on which the "
                + "nested-budget ladder is strictly decreasing (issue #1198).");
    }

    private readonly TimeSpan timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How much of an enclosing bound each nesting level hands back, so the level that encloses it
    /// has room to OBSERVE the inner failure and report it. Must be &gt; zero.
    ///
    /// <para>The reserve is absolute rather than fractional because what it has to cover is
    /// absolute: the hop that carries the inner failure outward (a hub round-trip — milliseconds),
    /// plus the delay between the outer clock starting and the inner one starting (the post,
    /// routing, and a warm per-node-hub activation). Five seconds is far more than a healthy hop
    /// needs and is deliberately generous about activation.</para>
    ///
    /// <para>🚨 <b>What happens when even that is not enough</b> — a genuinely cold per-node hub
    /// can take longer than the reserve to activate — is not a hole, it is the correct answer: the
    /// outer bound fires and reports that the hub never answered, which is exactly what went wrong.
    /// The inner bound exists to attribute a starved READ, not a slow START.</para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The reserve is not positive — a zero or
    /// negative reserve would make <see cref="Nest"/> non-contracting, which is the defect this
    /// type exists to make unrepresentable.</exception>
    public TimeSpan NestingReserve
    {
        get => nestingReserve;
        init => nestingReserve = value > TimeSpan.Zero
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value,
                "NestingReserve must be positive: a nested bound that does not contract cannot "
                + "fire before the bound that encloses it, which is issue #1198.");
    }

    private readonly TimeSpan nestingReserve = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Floor for <see cref="Nest"/>, as a fraction of the bound being nested inside. Must be in
    /// (0, 1). It only bites when <see cref="Timeout"/> is configured at or below
    /// <see cref="NestingReserve"/> — the short-timeout shape tests use — where subtracting the
    /// reserve would drive a rung to zero or negative. Contracting by a fraction instead keeps the
    /// ladder positive AND strictly decreasing at any scale.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The fraction is outside (0, 1) — at or above
    /// one it stops contracting, at or below zero it collapses the rung to nothing.</exception>
    public double MinNestingFraction
    {
        get => minNestingFraction;
        init => minNestingFraction = value is > 0 and < 1
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value,
                "MinNestingFraction must be strictly between 0 and 1 so that every nested bound is "
                + "strictly smaller than the bound enclosing it (issue #1198).");
    }

    private readonly double minNestingFraction = 0.5;

    /// <summary>
    /// <b>Rung 2 — a handler that runs INSIDE another operation's bounded stage.</b> Two shapes on
    /// the delete path: the descendant hub answering the pre-flight
    /// <c>ValidateDeleteRequest</c> fan-out, and a cascade leg re-entering
    /// <c>HandleDeleteNodeRequest</c> from within the root's commit stage. Both are nested by
    /// construction — the callee only ever runs because a caller is already holding a bound open —
    /// so both must be strictly quicker to give up than that caller.
    /// </summary>
    public TimeSpan NestedTimeout => Nest(Timeout);

    /// <summary>
    /// <b>Rung 3 — a single authorization fold inside a rung-2 handler.</b> How long
    /// <c>RlsNodeValidator</c> waits for the effective-permission fold to reach a verdict before
    /// reporting that the check could not be ESTABLISHED (#1446).
    ///
    /// <para>🚨 This is not a ceiling that turns a slow check into a denial — it is what gives the
    /// check a TERMINAL at all. The fold is a <c>CombineLatest</c> over the grant and policy reads
    /// of the target's scope and every ancestor scope; a leg that starves (the cross-silo case,
    /// where the owning activation lives on a peer silo) never emits, never completes and never
    /// errors, so the fold cannot produce any outcome, and the <c>.Take(1)</c> around it bounds the
    /// number of emissions rather than the wait. Past this budget the validator answers
    /// <see cref="NodeRejectionReason.Unavailable"/> — neither a grant nor a denial — so the
    /// operation reports its own availability failure instead of sitting until its CALLER gives
    /// up.</para>
    ///
    /// <para>It sits two rungs down because that is where it actually runs: the deepest shape is
    /// the recursive delete's cascade leg (rung 2) running its own validator chain (rung 3). The
    /// validator is a singleton and cannot know its depth per call, so it always takes the DEEPEST
    /// rung — which is below every enclosing bound on every path, and therefore safe on all of
    /// them.</para>
    /// </summary>
    public TimeSpan PermissionEstablishmentBudget => Nest(NestedTimeout);

    /// <summary>
    /// The bound for work nested one level inside <paramref name="enclosing"/>. Strictly
    /// contracting: <c>Nest(t) &lt; t</c> for every <c>t &gt; 0</c>, because
    /// <see cref="NestingReserve"/> is positive and <see cref="MinNestingFraction"/> is below one.
    /// That inequality — not a convention, not a comment — is what makes the inner bound the one
    /// that fires.
    /// </summary>
    public TimeSpan Nest(TimeSpan enclosing) => TimeSpan.FromTicks(Math.Max(
        (enclosing - NestingReserve).Ticks,
        (long)(enclosing.Ticks * MinNestingFraction)));
}
