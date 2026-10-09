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
/// strictly contracting. So <see cref="QueryInitialBudget"/> &lt;
/// <see cref="PermissionEstablishmentBudget"/> &lt; <see cref="NestedTimeout"/> &lt;
/// <see cref="Timeout"/> for every configuration, and the ladder cannot drift apart again because
/// there is nothing to drift against. At the production default the rungs are
/// <b>30 s / 25 s / 20 s / 15 s</b>.</para>
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
    /// How many per-node delete legs a recursive delete's COMMIT keeps in flight at once, across the
    /// whole subtree. Defaults to 64, the same runaway-fan-out stop the delete pre-flight uses. Must
    /// be at least 1.
    ///
    /// <para><b>Why a commit needs one</b> (issue #6351). Every leaf commit re-enters the delete
    /// handler at its own per-node hub and ends in ONE write on the process-wide cap-1
    /// <c>pg:{provider}</c> write pool, and each leaf's own commit stage is a no-progress watchdog
    /// that starts when its hub takes the request. Unbounded, a 1,383-path subtree queued about a
    /// thousand of its own leaf writes on that one pool at once, so the leaves at the back of the
    /// queue made "no progress" for their whole budget while the cascade was removing rows steadily
    /// — and one such leaf failed the delete. With the lane bounded, a leaf waits behind at most
    /// this many of its siblings' writes, which at the measured ~20 ms per pooled write is about a
    /// second, not the budget.</para>
    ///
    /// <para>This is a concurrency STOP, not a budget: it changes how many legs run together, never
    /// how long any of them may take, and no bound on this ladder depends on it.</para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is below 1.</exception>
    public int CascadeFanOutConcurrency
    {
        get => cascadeFanOutConcurrency;
        init => cascadeFanOutConcurrency = value >= 1
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value,
                "CascadeFanOutConcurrency must be at least 1 (issue #6351).");
    }

    private readonly int cascadeFanOutConcurrency = 64;

    /// <summary>
    /// The satellite segments whose rows are RECORDS. A record needs no per-node hub to be deleted:
    /// no type-specific validator registered on its own hub, and no post-deletion handler. A
    /// recursive delete validates rows under these segments in-process and removes them in
    /// batches (<see cref="RecordSatelliteBatchSize"/> per storage call) instead of one leaf
    /// round-trip each. The default is <c>_Activity</c> alone.
    ///
    /// <para><b>Why</b> (Systemorph/MeshWeaver, the 2026-10-09 <c>Crm/Client</c> delete on
    /// memex.systemorph.com). Every planned descendant used to cost two activations of its own
    /// per-node hub: one to answer the pre-flight <c>ValidateDeleteRequest</c> and one to commit its
    /// own <c>DeleteNodeRequest</c>. A NodeType carrying several hundred
    /// <c>_Activity/compile-*</c> records paid that for every one of them and took more than 60 s.
    /// Nothing about an activity record needs its own hub to be deleted.</para>
    ///
    /// <para><b>Which rows qualify.</b> A path qualifies when its FIRST satellite segment is listed
    /// here AND its owner (<c>SatelliteTableMapping.OwnerOfSatellitePath</c>) is the delete's root
    /// or a node in the delete's own plan. Any other row takes the ordinary per-node lane. A
    /// qualifying row is still VALIDATED: the pre-flight reads it from storage and runs the full
    /// delete-validator chain on it in-process, under the caller's identity. That chain includes
    /// the row's own access rule against its stored <c>MainNode</c>, so the verdict is the one its
    /// own hub would give, without activating that hub. The batch keeps the per-node side effects that
    /// do not need a hub: the change-feed <c>Deleted</c> event (children first), the stream-cache
    /// invalidation, the "delete wins" tombstone, and disposal of a per-node hub that happens to
    /// be activated.</para>
    ///
    /// <para>Declared, never derived. A segment whose rows gain per-node delete semantics must
    /// leave this set in the same change. Clearing the set restores the per-node lane for
    /// everything.</para>
    /// </summary>
    public System.Collections.Immutable.ImmutableHashSet<string> RecordSatelliteSegments { get; init; }
        = System.Collections.Immutable.ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "_Activity");

    /// <summary>
    /// How many record satellites (<see cref="RecordSatelliteSegments"/>) one storage
    /// <c>DeleteMany</c> call removes. Batches run one after another, and each batch is one tick of
    /// the commit's no-progress watchdog, so a batch must stay well inside it. Postgres sends one
    /// statement per window; the default adapter loops in-process. Must be at least 1.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is below 1.</exception>
    public int RecordSatelliteBatchSize
    {
        get => recordSatelliteBatchSize;
        init => recordSatelliteBatchSize = value >= 1
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value,
                "RecordSatelliteBatchSize must be at least 1.");
    }

    private readonly int recordSatelliteBatchSize = 100;

    /// <summary>
    /// <b>Rung 2 — work that runs INSIDE another operation's bounded stage.</b> Two shapes on the
    /// delete path: ONE LEG of the pre-flight <c>ValidateDeleteRequest</c> fan-out, as the caller
    /// bounds it, and a cascade leg re-entering <c>HandleDeleteNodeRequest</c> from within the
    /// root's commit stage. Both are nested by construction — neither ever runs except because a
    /// caller is already holding a bound open — so both must be strictly quicker to give up than
    /// that caller.
    ///
    /// <para>🚨 <b>A leg is what makes a silent hub ATTRIBUTABLE</b> (issue #1198). The pre-flight
    /// fan-out used to carry one bound over the whole merge, so a single unresponsive per-node hub
    /// consumed the entire subtree's budget and the delete was refused by an anonymous "7 of 83
    /// descendant(s) did not answer". With the bound on each leg instead, the silent leaf is the
    /// one that reports — by name, at a rung strictly inside the stage — and its siblings still
    /// finish normally.</para>
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
    ///
    /// <para>🚨 <b>It shares this rung with the pre-flight leaf's own storage read, and that is
    /// correct</b>: <c>HandleValidateDeleteRequest</c> bounds its node read at <c>Nest(NestedTimeout)</c>
    /// too, because it answers a rung-2 LEG. The two are SIBLINGS, not nested — the read completes
    /// before the validator chain starts, so only one of them is ever running and the value tells
    /// them apart the way the six delete STAGES are told apart by name. Equal-by-coincidence is the
    /// defect this type exists to prevent; equal-between-siblings is not that, and the distinction
    /// is what the word NESTED does all the work for here.</para>
    /// </summary>
    public TimeSpan PermissionEstablishmentBudget => Nest(NestedTimeout);

    /// <summary>
    /// <b>Rung 4 — ONE query fan-in's Initial frame, inside a rung-3 fold.</b> How long
    /// <c>MeshQuery.MergeProviderObservables</c> waits for every registered
    /// <c>IMeshQueryProvider</c> to deliver its Initial before terminating the merged query with
    /// <see cref="MeshWeaver.Mesh.QueryProviderStalledException"/>.
    ///
    /// <para>🚨 <b>It is the innermost rung because it is the innermost READ.</b> Every rung-3
    /// permission fold is a <c>CombineLatest</c> over <c>$security-*</c> queries served by this
    /// fan-in, so the fan-in's answer is what the fold is waiting for. Before this rung existed the
    /// fan-in had no terminal at all — a provider that neither emitted, completed nor errored
    /// starved the gate for ever and only produced a logged warning — so the fold's own
    /// <see cref="PermissionEstablishmentBudget"/> was the first bound to fire, and it can say no
    /// more than "the check could not be established". Which PROVIDER starved is knowable only
    /// here.</para>
    ///
    /// <para>🚨 <b>It must not read the same as the bound enclosing it</b>, which is why it is
    /// derived rather than written down: the stall probe's diagnostic delay was a hard-coded 20 s,
    /// the identical value <see cref="PermissionEstablishmentBudget"/> takes at the production
    /// default. Promoting that constant to a terminal as-is would have recreated issue #1198's
    /// defect exactly — equal is not an ordering, the outer clock starts first, so the attribution
    /// would have been lost to a coin flip. <see cref="Nest"/> makes the collision
    /// unrepresentable. At the production default the ladder now reads <b>30 s / 25 s / 20 s /
    /// 15 s</b>, and 15 s is still roughly twice the observed healthy worst case for a cold
    /// provider under suite load (single-digit seconds).</para>
    ///
    /// <para>A false positive here is self-correcting and cheap: the answer is an availability
    /// failure a caller may retry, and <c>MeshNodeStreamCache.EvictFaultedQuery</c> drops the
    /// chain so the next read re-probes the providers for real (#1316). A false NEGATIVE — the
    /// hang — is neither.</para>
    /// </summary>
    public TimeSpan QueryInitialBudget => Nest(PermissionEstablishmentBudget);

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
