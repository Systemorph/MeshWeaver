using System.Collections.Immutable;
using System.Reactive.Linq;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// 🚨 <b>THE roll-selection algorithm (#3479), stated once.</b>
///
/// <blockquote>Maintainer, 2026-09-06: <i>"Whenever a new platform / plugin is published, check for
/// each environment which is the latest platform version shipping all plugins, if different from
/// current version ⇒ update."</i></blockquote>
///
/// <para><b>It is an INVERSION of the order this fleet used to roll in.</b> Before, an environment
/// took the newest published set, booted onto it, and only then discovered a plugin missing or
/// regressed — at which point the readiness gate refused and the rollout stalled. Completeness was
/// a <i>post-hoc verdict</i>. Here it is a <b>selection criterion</b>: an environment never targets
/// a release that does not ship all of its plugins, so there is nothing left to refuse.</para>
///
/// <para><b>The quantifier is PER ENVIRONMENT.</b> "Ships all plugins" is not a property of a
/// release — it is a question about <i>that environment's</i> plugin list. Two environments looking
/// at one publication may legitimately choose different targets, and one may correctly stay put.
/// </para>
///
/// <para>🚨 <b>The predicate is SHARED, never re-implemented.</b> Whether one release ships one
/// environment's plugins is <see cref="ReleaseAvailability.IsUpdatable"/> — the gate #3441/#3443
/// built and falsified. This type contributes the <i>walk</i> and the <i>denominator discipline</i>
/// around it, and takes the per-candidate verdict as a function so there can never be a second copy
/// of the rule to drift from the first.</para>
///
/// <para>🚨 <b>A selector that finds ZERO plugins fails LOUDLY.</b> "No plugins to check" and "all
/// plugins present" must never be spelled the same way — with an empty denominator every candidate
/// is vacuously complete and the algorithm degenerates into "take the newest", which is precisely
/// what it exists to replace. That is the defect #3441 removed one level down, and
/// <see cref="RollSelectionKind.NoPluginsKnown"/> is where it is refused here. The denominator is
/// PRINTED in <see cref="RollSelectionOutcome.Summary"/> on every outcome, so a reader never has to
/// infer it from a pass.</para>
///
/// <para><b>Ordering is POSITIONAL, so this assembly needs no version comparer.</b> The caller hands
/// the candidates newest-first — it already owns the SemVer rules, the tag-shape filters and the
/// update policy — and this walk reads "newer" off the index. That is what lets it name the one
/// answer nobody could get before: the newest complete release is <i>behind</i> what the environment
/// runs (<see cref="RollSelectionKind.BehindCurrent"/>). Rolling backwards is a different act from
/// declining to roll forwards, so it is REPORTED and never selected.</para>
///
/// <para>See <c>Doc/Architecture/RollSelection</c>.</para>
/// </summary>
public static class RollSelection
{
    /// <summary>
    /// The latest candidate that ships every one of this environment's plugins, walked newest-first.
    /// Cold and LAZY: <paramref name="verdictOf"/> is asked about one candidate at a time and the
    /// walk stops at the first acceptance, so the common case (the head is complete) costs exactly
    /// one verdict.
    /// </summary>
    /// <param name="inputs">The environment, what it runs, what it could run, and what it must be
    /// able to serve.</param>
    /// <param name="verdictOf">The SHARED completeness predicate for one candidate — in production
    /// <c>ReleaseAvailabilityService.IsUpdatable</c>, which is
    /// <see cref="ReleaseAvailability.IsUpdatable"/> plus the observation it needs. Must be total:
    /// every failure resolves to a verdict carrying its reason, never a fault.</param>
    /// <returns>Exactly one outcome, then completes.</returns>
    public static IObservable<RollSelectionOutcome> Select(
        RollSelectionInputs inputs,
        Func<string, IObservable<UpdatabilityVerdict>> verdictOf) =>
        Observable.Defer(() =>
        {
            // 🚨 ORDER MATTERS, and every one of these is spelled DIFFERENTLY from "all plugins
            // present". Each is a state in which the selector cannot see, and a selector that
            // cannot see must never answer "take the newest".

            // 1. We could not establish WHAT this environment carries. A HOLD, named.
            if (inputs.Inventory.Refusal is { Length: > 0 } refusal)
                return One(new RollSelectionOutcome(
                    RollSelectionKind.Indeterminate,
                    null, inputs.CurrentVersion, 0, 0, [],
                    $"{inputs.Environment}: the plugin list could not be read from "
                    + $"{inputs.Inventory.Source} ({refusal}) — cannot determine which release ships "
                    + "all plugins, which is not clearance to roll to the newest one."));

            // 2. 🚨 THE VACUITY REFUSAL. Zero plugins makes every release vacuously complete.
            if (inputs.Inventory.Plugins.IsDefaultOrEmpty)
                return One(new RollSelectionOutcome(
                    RollSelectionKind.NoPluginsKnown,
                    null, inputs.CurrentVersion, 0, 0, [],
                    $"{inputs.Environment}: 0 plugins required — {inputs.Inventory.Source} named "
                    + "none. Every release would ship all zero of them, so the selection is vacuous "
                    + "and nothing is selected. Either this environment genuinely deploys no "
                    + "packages (in which case the release-availability gate does not apply to it "
                    + "at all and should not be configured), or its plugin list is not being read — "
                    + "which is the failure this refusal exists to make visible."));

            // 3. Nothing to walk. Not an incident; still said out loud rather than completing empty.
            if (inputs.CandidatesNewestFirst.IsDefaultOrEmpty)
                return One(new RollSelectionOutcome(
                    RollSelectionKind.NoCandidates,
                    null, inputs.CurrentVersion, inputs.Inventory.Plugins.Length, 0, [],
                    $"{inputs.Environment}: {inputs.Inventory.Plugins.Length} plugin(s) required "
                    + $"(from {inputs.Inventory.Source}); no release was offered to choose from."));

            return Walk(inputs, verdictOf).Select(walk => Conclude(inputs, walk));
        });

    /// <summary>
    /// The walk itself: newest-first, one verdict at a time, stopping at the first acceptance.
    ///
    /// <para>🚨 <b>Expressed as a recursive step rather than <c>Concat</c> + a stop predicate</b>,
    /// and that is load-bearing rather than stylistic: the next candidate's verdict is only ASKED
    /// FOR inside the previous one's decline, so laziness is a property of the shape instead of a
    /// property of how promptly a downstream operator disposes its source. <c>Concat</c> over
    /// synchronous verdicts was measured walking one candidate past the acceptance
    /// (<c>TheWalkStopsAtTheFirstCompleteRelease</c>), and on a network share a candidate is a
    /// directory scan against a bounded budget — an extra one per selection is a cost, and an extra
    /// hundred is a timeout.</para>
    ///
    /// <para>Recursion depth is the number of candidates actually EXAMINED, which is one whenever
    /// the head is complete — the ordinary case — and bounded by the candidate list otherwise.</para>
    /// </summary>
    private static IObservable<WalkState> Walk(
        RollSelectionInputs inputs, Func<string, IObservable<UpdatabilityVerdict>> verdictOf) =>
        Step(inputs.CandidatesNewestFirst, 0, WalkState.Empty, verdictOf);

    private static IObservable<WalkState> Step(
        ImmutableArray<string> candidates,
        int index,
        WalkState state,
        Func<string, IObservable<UpdatabilityVerdict>> verdictOf) =>
        index >= candidates.Length
            ? Observable.Return(state)
            : Observable.Defer(() => verdictOf(candidates[index]))
                .Take(1)
                .Select(verdict => state.Add(candidates[index], verdict))
                .SelectMany(next => next.Accepted is not null
                    ? Observable.Return(next)
                    : Step(candidates, index + 1, next, verdictOf));

    /// <summary>
    /// What the walk means. The three "we chose something" answers differ only in where the choice
    /// sits relative to what is running, and that difference is the whole point of reporting it.
    /// </summary>
    private static RollSelectionOutcome Conclude(RollSelectionInputs inputs, WalkState walk)
    {
        var required = inputs.Inventory.Plugins.Length;
        var satisfied = walk.Verdict?.Packages.Count(p => p.IsAvailable) ?? 0;
        var head = $"{inputs.Environment}: {required} plugin(s) required (from "
                   + $"{inputs.Inventory.Source})";

        if (walk.Accepted is not { } selected)
            return new RollSelectionOutcome(
                RollSelectionKind.NoCompleteRelease,
                null, inputs.CurrentVersion, required, 0, walk.Declined,
                $"{head}; NO published release ships all of them. "
                + $"{walk.Declined.Length} candidate(s) were examined and every one was declined — "
                + $"{FirstReasons(walk.Declined)}. Staying on {Describe(inputs.CurrentVersion)}.");

        var kind = Position(inputs, selected);
        var tail = $"; {satisfied} of {required} satisfied by {selected}"
                   + (walk.Declined.IsEmpty
                       ? " (the newest candidate)"
                       : $", after declining {walk.Declined.Length} newer candidate(s) — "
                         + FirstReasons(walk.Declined));

        return new RollSelectionOutcome(
            kind, selected, inputs.CurrentVersion, required, satisfied, walk.Declined,
            kind switch
            {
                RollSelectionKind.AlreadyCurrent =>
                    $"{head}{tail}. That is what this environment already runs — no update.",
                RollSelectionKind.BehindCurrent =>
                    $"{head}{tail}. 🚨 That is BEHIND {Describe(inputs.CurrentVersion)}, which this "
                    + "environment is running: nothing published at or above the running version "
                    + "ships all its plugins. Rolling BACKWARDS is a separate decision and is never "
                    + "taken here — this is reported, not applied.",
                _ =>
                    $"{head}{tail}. Different from {Describe(inputs.CurrentVersion)} ⇒ update.",
            });
    }

    /// <summary>
    /// Where the selection sits relative to the running version, read off the candidate ORDER —
    /// the list is newest-first, so a smaller index is a newer release. A current version the
    /// caller did not include among the candidates (the poller filters to strictly-newer before it
    /// asks) can only be moved FORWARD from, which is exactly what that caller means.
    /// </summary>
    private static RollSelectionKind Position(RollSelectionInputs inputs, string selected)
    {
        if (string.Equals(selected, inputs.CurrentVersion, StringComparison.OrdinalIgnoreCase))
            return RollSelectionKind.AlreadyCurrent;

        var current = IndexOf(inputs.CandidatesNewestFirst, inputs.CurrentVersion);
        if (current < 0)
            return RollSelectionKind.Update;

        return IndexOf(inputs.CandidatesNewestFirst, selected) > current
            ? RollSelectionKind.BehindCurrent
            : RollSelectionKind.Update;
    }

    private static int IndexOf(ImmutableArray<string> candidates, string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return -1;
        for (var i = 0; i < candidates.Length; i++)
            if (string.Equals(candidates[i], version, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    /// <summary>The first two refusals in full, so the summary line names a PLUGIN and not merely a
    /// count. The rest stay in <see cref="RollSelectionOutcome.Declined"/>, which carries every
    /// package verdict of every candidate examined.</summary>
    private static string FirstReasons(ImmutableArray<DeclinedRelease> declined) =>
        string.Join("; ", declined.Take(2).Select(d => $"{d.Version}: {d.Reason}"))
        + (declined.Length > 2 ? $"; (+{declined.Length - 2} more)" : string.Empty);

    private static string Describe(string? version) =>
        string.IsNullOrWhiteSpace(version) ? "an unknown running version" : version;

    private static IObservable<RollSelectionOutcome> One(RollSelectionOutcome outcome) =>
        Observable.Return(outcome);

    /// <summary>The walk's accumulator: what has been declined so far, and the acceptance that ends
    /// it. Immutable, so the <c>Scan</c> can never hand two subscribers one another's state.</summary>
    private sealed record WalkState(
        ImmutableArray<DeclinedRelease> Declined, string? Accepted, UpdatabilityVerdict? Verdict)
    {
        public static WalkState Empty { get; } = new([], null, null);

        public WalkState Add(string version, UpdatabilityVerdict verdict) =>
            verdict.IsUpdatable
                ? this with { Accepted = version, Verdict = verdict }
                : this with
                {
                    Declined = Declined.Add(new DeclinedRelease(
                        version,
                        verdict.HoldReason ?? "declined without a stated reason",
                        [.. verdict.Blockers])),
                };
    }
}

/// <summary>
/// What the selector is asked. Everything version-shaped is a STRING and the order is the caller's:
/// SemVer rules, tag-shape filters and the update policy live with the caller that lists the
/// releases, so this assembly holds no second opinion about which release is newer.
/// </summary>
/// <param name="Environment">The environment being selected FOR, named in every outcome — the
/// algorithm's quantifier is per environment, so an outcome that does not say which one is not an
/// answer.</param>
/// <param name="CurrentVersion">The platform version this environment runs right now, or null when
/// it is not knowable (an unstamped build). Null never blocks a selection; it only makes the
/// "different from current ⇒ update" comparison unavailable, which is said in the summary.</param>
/// <param name="CandidatesNewestFirst">The releases this environment may choose from, NEWEST FIRST,
/// already filtered by whatever policy governs it.</param>
/// <param name="Inventory">This environment's plugins — the DENOMINATOR.</param>
public sealed record RollSelectionInputs(
    string Environment,
    string? CurrentVersion,
    ImmutableArray<string> CandidatesNewestFirst,
    PluginInventory Inventory);

/// <summary>
/// 🚨 The DENOMINATOR of the selection: what this environment must be able to serve, and WHERE that
/// list came from.
///
/// <para>The source is carried as text and reported on every outcome deliberately. An environment's
/// plugin list has several possible readings that <b>disagree in this fleet</b> — install records,
/// registry sources, per-Space sync entries, what is actually mounted — and a denominator whose
/// provenance is not stated cannot be argued with. See <c>Doc/Architecture/RollSelection</c> for
/// which reading is used and why.</para>
///
/// <para>🚨 <see cref="Unreadable"/> is NOT an empty list, and neither is an empty list a pass. A
/// failed read HOLDS; a genuinely empty list refuses as vacuous
/// (<see cref="RollSelectionKind.NoPluginsKnown"/>). Collapsing either into "nothing to check,
/// carry on" is the defect this whole type exists to prevent.</para>
/// </summary>
/// <param name="Plugins">What must survive the roll — the same
/// <see cref="RequiredPackage"/> values the release gate evaluates, so gate and selector can never
/// disagree about the set.</param>
/// <param name="Source">Where the list came from, in words a human reads in the refusal.</param>
/// <param name="Refusal">Why it could not be read, or null when it was read.</param>
public sealed record PluginInventory(
    ImmutableArray<RequiredPackage> Plugins,
    string Source,
    string? Refusal = null)
{
    /// <summary>A list that was READ — even if it turned out to be empty, which is a refusal and
    /// not a pass.</summary>
    public static PluginInventory Of(IEnumerable<RequiredPackage> plugins, string source) =>
        new([.. plugins], source);

    /// <summary>A list that could NOT be read — the fail-safe constructor.</summary>
    public static PluginInventory Unreadable(string source, string reason) =>
        new([], source, reason);
}

/// <summary>One candidate the walk refused, with the shared predicate's own words.</summary>
/// <param name="Version">The release.</param>
/// <param name="Reason">The joined hold reason — what a human reads first.</param>
/// <param name="Blockers">Every package that blocked it, so the plugin can be NAMED rather than
/// counted.</param>
public sealed record DeclinedRelease(
    string Version, string Reason, ImmutableArray<PackageAvailability> Blockers);

/// <summary>What the selection concluded. Every member is a DIFFERENT sentence on purpose: the
/// states this algorithm exists to keep apart are exactly the ones a weaker selector spells
/// identically.</summary>
public enum RollSelectionKind
{
    /// <summary>A release ships all of this environment's plugins and it is not what the
    /// environment runs ⇒ update to it.</summary>
    Update,

    /// <summary>The newest release that ships all plugins is the one already running ⇒ nothing to
    /// do. The happy steady state, and it is SAID rather than left as a silence.</summary>
    AlreadyCurrent,

    /// <summary>🚨 The newest release that ships all plugins is BEHIND the running version — so the
    /// environment is currently running something that does not ship all its plugins. Reported,
    /// never applied: rolling backwards is a separate decision with its own risks (a schema is not
    /// reversible), and taking it silently would be worse than the state it fixes.</summary>
    BehindCurrent,

    /// <summary>No candidate ships all plugins. Stay put, and name the plugin and the environment —
    /// a plugin no published release can satisfy is an alert, not a quiet hold.</summary>
    NoCompleteRelease,

    /// <summary>🚨 The environment's plugin list is EMPTY, so completeness is vacuous. Never a
    /// selection.</summary>
    NoPluginsKnown,

    /// <summary>🚨 The selector could not SEE — the plugin list, the release listing or the
    /// artifact store could not be read. A HOLD: cannot determine is not clearance. Deliberately
    /// distinct from <see cref="NoPluginsKnown"/>, which is a read that SUCCEEDED and returned
    /// nothing; one is a broken read to fix, the other a configuration statement to correct.
    /// </summary>
    Indeterminate,

    /// <summary>No candidate was offered at all.</summary>
    NoCandidates,

    /// <summary>
    /// 🚨 The selection does not APPLY to this environment — it consumes no CI bakes, so it already
    /// compiles its content at every boot and no publication can be shown to ship or not ship its
    /// plugins. Deliberately NOT a selection: the one applicability exemption, stated so that
    /// "nothing is choosing for this environment" is visible rather than inferred from a version
    /// appearing. Mirrors <see cref="UpdatabilityVerdict.NotEnforced"/> one level up.
    /// </summary>
    NotEnforced,
}

/// <summary>
/// The selection, and the evidence for it. <see cref="Summary"/> always carries the denominator —
/// <i>"N plugin(s) required (from …); M satisfied by X"</i> — because a completeness answer whose
/// expected count nobody can read is one nobody can tell from a vacuous one.
/// </summary>
/// <param name="Kind">What was concluded.</param>
/// <param name="SelectedVersion">The release that ships all plugins, or null when none does.</param>
/// <param name="CurrentVersion">What the environment runs.</param>
/// <param name="RequiredPlugins">The denominator — how many plugins had to be shipped.</param>
/// <param name="SatisfiedPlugins">How many the selected release ships. Equal to
/// <paramref name="RequiredPlugins"/> whenever a selection was made; zero when none was.</param>
/// <param name="Declined">Every candidate examined and refused, newest first, with its reasons.</param>
/// <param name="Summary">One line naming the environment, the denominator, the choice and the
/// refusals — what a log line and an operator both read.</param>
public sealed record RollSelectionOutcome(
    RollSelectionKind Kind,
    string? SelectedVersion,
    string? CurrentVersion,
    int RequiredPlugins,
    int SatisfiedPlugins,
    ImmutableArray<DeclinedRelease> Declined,
    string Summary)
{
    /// <summary>Whether this outcome asks for a roll. <b>Only</b>
    /// <see cref="RollSelectionKind.Update"/> does — every other member is a state to report, and
    /// there is deliberately no branch that turns "could not tell" into a roll.</summary>
    public bool ShouldUpdate => Kind == RollSelectionKind.Update;

    /// <summary>Whether the selector could not SEE, as opposed to having looked and found nothing
    /// adoptable. Callers surface the two differently: an unreadable inventory is an incident to
    /// fix, an incomplete publication is a release to re-bake.</summary>
    public bool IsIndeterminate =>
        Kind is RollSelectionKind.Indeterminate or RollSelectionKind.NoPluginsKnown;
}
