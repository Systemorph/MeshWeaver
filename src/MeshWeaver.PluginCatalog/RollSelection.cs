using System.Collections.Immutable;
using System.Reactive.Linq;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// 🚨 <b>THE roll-selection algorithm, stated once: an environment selects the NEWEST release its
/// update policy admits.</b> The platform rolls independently of its packages
/// (policy <c>platform-rolls-independently-of-packages</c>).
///
/// <para><b>Two questions, and only the first decides.</b> <i>Which release?</i> — the head of
/// the caller's newest-first candidate list, always. <i>What do this environment's packages say
/// about it?</i> — asked ONCE, of that release, through the shared predicate, and REPORTED on the
/// outcome: a refusal (a missing bake under the strict mode, an unresolvable framework identity, a
/// torn sealed set, a landed module that cannot load) is waived by
/// <see cref="UpdatabilityVerdict.ForPlatformRoll"/> into
/// <see cref="RollSelectionOutcome.Advisories"/>, led by the reason it would have held on, and
/// <see cref="RollSelectionOutcome.Summary"/> names what does not follow the roll. No package
/// answer walks past a release; a package that is not available for it keeps its previous
/// generation or parks, and catches up through its own update policy.</para>
///
/// <para><b>History.</b> The selection was built (#3479) as "the latest platform version shipping
/// all plugins" and walked past every release that did not; #3651 narrowed what declined a
/// candidate to a module measured unloadable. That made the platform wait for its packages while
/// every package update waited for its platform floor, and the two waits met — the deadlock the
/// policy removes. Nothing declines a candidate now, so <see cref="RollSelectionKind.NoCompleteRelease"/>,
/// <see cref="RollSelectionKind.BehindCurrent"/> and <see cref="RollSelectionKind.NoPluginsKnown"/>
/// are no longer produced.</para>
///
/// <para><b>The package answer is PER ENVIRONMENT.</b> It is a statement about <i>that
/// environment's</i> plugin list, so two environments selecting one release may carry different
/// advisories.</para>
///
/// <para>🚨 <b>The predicate is SHARED, never re-implemented.</b> Whether one release serves one
/// environment's packages is <see cref="ReleaseAvailability.IsUpdatable(ReleaseTarget, IEnumerable{RequiredPackage}, ReleaseArtifacts)"/> — the gate #3441/#3443
/// built and falsified, unchanged, and still read as a yes or no by the CI build gate and the
/// package surfaces. This type takes the verdict as a function, so what the outcome reports can
/// never drift from the rule.</para>
///
/// <para>🚨 <b>A selection that checked ZERO plugins says so.</b> "No plugins were checked" and "all
/// plugins present" must never be spelled the same way. An unread or empty plugin list no longer
/// stops the selection — it is a statement about packages — but the newest candidate is then
/// selected through <see cref="RollSelection.Unjudged"/>, with zero satisfied and an advisory that names the
/// read that failed or came back empty. The denominator is PRINTED in
/// <see cref="RollSelectionOutcome.Summary"/> on every outcome, so a reader never has to infer it.</para>
///
/// <para><b>Ordering is POSITIONAL, so this assembly needs no version comparer.</b> The caller hands
/// the candidates newest-first — it already owns the SemVer rules, the tag-shape filters and the
/// update policy — and the selection takes index 0. Nothing behind the head is examined, so the
/// selection cannot land behind a running version that is among the candidates; keeping a roll
/// from going backwards is the candidate list's job.</para>
///
/// <para>See <c>Doc/Architecture/RollSelection</c>.</para>
/// </summary>
public static class RollSelection
{
    /// <summary>
    /// The newest candidate, with what this environment's packages say about it. Cold and LAZY:
    /// <paramref name="verdictOf"/> is asked about the head only, so a selection costs exactly one
    /// verdict — and none at all when no candidate was offered or the plugin list is unread or
    /// empty (<see cref="Unjudged"/>).
    /// </summary>
    /// <param name="inputs">The environment, what it runs, what it could run, and the packages
    /// the verdict is asked about.</param>
    /// <param name="verdictOf">The SHARED package predicate for one candidate — in production
    /// <c>ReleaseAvailabilityService.IsUpdatable</c>, which is
    /// <see cref="ReleaseAvailability.IsUpdatable(ReleaseTarget, IEnumerable{RequiredPackage}, ReleaseArtifacts)"/> plus the observation it needs. Must be total:
    /// every failure resolves to a verdict carrying its reason, never a fault. A refusing verdict
    /// does not change the selection; it becomes the outcome's advisories.</param>
    /// <returns>Exactly one outcome, then completes.</returns>
    public static IObservable<RollSelectionOutcome> Select(
        RollSelectionInputs inputs,
        Func<string, IObservable<UpdatabilityVerdict>> verdictOf) =>
        Observable.Defer(() =>
        {
            // 🚨 Nothing to walk. Not an incident; still said out loud rather than completing empty.
            // Checked FIRST under policy <c>platform-rolls-independently-of-packages</c>: with the
            // platform rolling independently of its
            // packages, the candidate list is the only thing that can leave nothing to select.
            if (inputs.CandidatesNewestFirst.IsDefaultOrEmpty)
                return One(new RollSelectionOutcome(
                    RollSelectionKind.NoCandidates,
                    null, inputs.CurrentVersion, inputs.Inventory.Plugins.Length, 0, [],
                    $"{inputs.Environment}: {inputs.Inventory.Plugins.Length} plugin(s) required "
                    + $"(from {inputs.Inventory.Source}); no release was offered to choose from."));

            // 🚨 The plugin list could not be read, or named none. Both are statements about the
            // PACKAGES, so neither holds the platform (policy
            // <c>platform-rolls-independently-of-packages</c>): the newest admitted candidate is
            // selected, and the summary and the advisory SAY that no package was checked — an
            // unread denominator is reported, never dressed up as "all plugins present".
            if (inputs.Inventory.Refusal is { Length: > 0 } refusal)
                return One(Unjudged(inputs,
                    $"the plugin list could not be read from {inputs.Inventory.Source} ({refusal}) — "
                    + "package availability for this release is unknown, which decides nothing about "
                    + "the platform roll; the packages catch up through their own update policy"));

            if (inputs.Inventory.Plugins.IsDefaultOrEmpty)
                return One(Unjudged(inputs,
                    $"0 plugins required — {inputs.Inventory.Source} named none, so no package was "
                    + "checked against this release (either this environment genuinely deploys no "
                    + "packages, or its plugin list is not being read — which is what this line exists "
                    + "to make visible)"));

            return Walk(inputs, verdictOf).Select(walk => Conclude(inputs, walk));
        });

    /// <summary>
    /// The walk itself: the head's verdict, accepted whatever it says.
    ///
    /// <para>Every verdict is accepted (policy <c>platform-rolls-independently-of-packages</c>, see
    /// <see cref="WalkState.Add"/>), so the walk ends after one step and asks exactly one verdict.
    /// It keeps its recursive shape — the next candidate's verdict is only ASKED FOR inside the
    /// previous step's non-acceptance — so laziness stays a property of the shape rather than of
    /// how promptly a downstream operator disposes its source: on a network share a candidate is a
    /// directory scan against a bounded budget, and one that is never asked for costs nothing.</para>
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
    /// What the walk means: the selected release, where it sits relative to what is running
    /// (<see cref="RollSelectionKind.AlreadyCurrent"/> or <see cref="RollSelectionKind.Update"/>),
    /// and — on the same line as the choice — the denominator, the boot-compile cost and what the
    /// package verdict would have held on. The walk always accepts its head, so the branches below
    /// for "nothing accepted", for declined candidates and for a selection behind the running
    /// version are not reached from <see cref="Select"/>.
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
                $"{head}; NO published release can serve all of them. "
                + $"{walk.Declined.Length} candidate(s) were examined and every one was declined — "
                + $"{FirstReasons(walk.Declined)}. Staying on {Describe(inputs.CurrentVersion)}.");

        var kind = Position(inputs, selected);
        var advisories = walk.Verdict?.Advisories ?? [];
        var bootCompiles = walk.Verdict?.BootCompiles ?? [];
        var tail = $"; {satisfied} of {required} satisfied by {selected}"
                   + (walk.Declined.IsEmpty
                       ? " (the newest candidate)"
                       : $", after declining {walk.Declined.Length} newer candidate(s) — "
                         + FirstReasons(walk.Declined))
                   // 🚨 The COST is said on the same line as the choice (#3651): a reader must
                   // never learn from a boot log that the roll it was told about compiled content.
                   + (bootCompiles.IsDefaultOrEmpty
                       ? string.Empty
                       : $"; {bootCompiles.Length} package(s) would recompile at boot: "
                         + string.Join(", ", bootCompiles))
                   // 🚨 The platform rolls independently of its packages (policy
                   // <c>platform-rolls-independently-of-packages</c>): what the
                   // package verdict would have held on is said on the same line as the choice.
                   + (walk.Verdict?.WaivedHold is { } waived
                       ? $"; the platform rolls independently of packages — not following this roll: {waived}"
                       : string.Empty);

        return new RollSelectionOutcome(
            kind, selected, inputs.CurrentVersion, required, satisfied, walk.Declined,
            kind switch
            {
                RollSelectionKind.AlreadyCurrent =>
                    $"{head}{tail}. That is what this environment already runs — no update.",
                RollSelectionKind.BehindCurrent =>
                    $"{head}{tail}. 🚨 That is BEHIND {Describe(inputs.CurrentVersion)}, which this "
                    + "environment is running: on every release at or above the running version an "
                    + "installed module is unloadable. Rolling BACKWARDS is a separate decision and "
                    + "is never taken here — this is reported, not applied.",
                _ =>
                    $"{head}{tail}. Different from {Describe(inputs.CurrentVersion)} ⇒ update.",
            })
        {
            Advisories = advisories,
            BootCompiles = bootCompiles,
        };
    }

    /// <summary>
    /// 🚨 The newest admitted candidate, selected WITHOUT a package verdict, with
    /// <paramref name="advisory"/> saying why none was asked — the platform rolls independently of
    /// its packages (policy <c>platform-rolls-independently-of-packages</c>), so a package question
    /// that could not be asked is
    /// reported, never a reason to stay. Used by this walk for an unread or empty plugin list, and
    /// by the caller (<c>ReleaseAvailabilityService.SelectRollTarget</c>) when the published root
    /// could not be read.
    /// </summary>
    /// <param name="inputs">The environment, what it runs, and the candidates (non-empty).</param>
    /// <param name="advisory">Why no package verdict was asked, in one sentence.</param>
    public static RollSelectionOutcome Unjudged(RollSelectionInputs inputs, string advisory)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.CandidatesNewestFirst.IsDefaultOrEmpty)
            return new RollSelectionOutcome(
                RollSelectionKind.NoCandidates, null, inputs.CurrentVersion,
                inputs.Inventory.Plugins.IsDefault ? 0 : inputs.Inventory.Plugins.Length, 0, [],
                $"{inputs.Environment}: no release was offered to choose from ({advisory}).");

        var selected = inputs.CandidatesNewestFirst[0];
        var kind = Position(inputs, selected);
        var required = inputs.Inventory.Plugins.IsDefault ? 0 : inputs.Inventory.Plugins.Length;
        var head = $"{inputs.Environment}: {required} plugin(s) required (from "
                   + $"{inputs.Inventory.Source}); {selected} is the newest admitted release; "
                   + $"the platform rolls independently of packages — {advisory}";
        return new RollSelectionOutcome(
            kind, selected, inputs.CurrentVersion, required, 0, [],
            kind == RollSelectionKind.AlreadyCurrent
                ? $"{head}. That is what this environment already runs — no update."
                : $"{head}. Different from {Describe(inputs.CurrentVersion)} ⇒ update.")
        {
            Advisories = [$"the platform rolls independently of packages — {advisory}"],
        };
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

        /// <summary>
        /// 🚨 Every candidate verdict is ACCEPTED (policy
        /// <c>platform-rolls-independently-of-packages</c>): a package-availability refusal becomes
        /// the accepted verdict's advisories (<see cref="UpdatabilityVerdict.ForPlatformRoll"/>),
        /// so the walk selects the newest admitted candidate and asks exactly one verdict.
        /// </summary>
        public WalkState Add(string version, UpdatabilityVerdict verdict) =>
            this with { Accepted = version, Verdict = verdict.ForPlatformRoll() };
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
/// 🚨 The DENOMINATOR of the package answer: what this environment must be able to serve, and
/// WHERE that list came from.
///
/// <para>The source is carried as text and reported on every outcome deliberately. An environment's
/// plugin list has several possible readings that <b>disagree in this fleet</b> — install records,
/// registry sources, per-Space sync entries, what is actually mounted — and a denominator whose
/// provenance is not stated cannot be argued with. See <c>Doc/Architecture/RollSelection</c> for
/// which reading is used and why.</para>
///
/// <para>🚨 <see cref="Unreadable"/> is NOT an empty list, and neither is an empty list "all
/// plugins present". Neither stops the selection (policy <c>platform-rolls-independently-of-packages</c>):
/// the newest candidate is selected through <see cref="RollSelection.Unjudged"/>. Each is REPORTED
/// in its own words — a read that failed, or a read that named no plugin — with zero satisfied, so
/// a selection that checked nothing never reads as one that found everything.</para>
/// </summary>
/// <param name="Plugins">The packages the verdict is asked about — the same
/// <see cref="RequiredPackage"/> values the release gate evaluates, so gate and selector can never
/// disagree about the set.</param>
/// <param name="Source">Where the list came from, in words a human reads in the advisory.</param>
/// <param name="Refusal">Why it could not be read, or null when it was read.</param>
public sealed record PluginInventory(
    ImmutableArray<RequiredPackage> Plugins,
    string Source,
    string? Refusal = null)
{
    /// <summary>A list that was READ — even if it turned out to be empty, which is reported as
    /// "no package was checked" and never as a pass.</summary>
    public static PluginInventory Of(IEnumerable<RequiredPackage> plugins, string source) =>
        new([.. plugins], source);

    /// <summary>A list that could NOT be read — reported as such on the selected release.</summary>
    public static PluginInventory Unreadable(string source, string reason) =>
        new([], source, reason);
}

/// <summary>
/// One candidate the walk refused, with the shared predicate's own words. The walk refuses none
/// under policy <c>platform-rolls-independently-of-packages</c>, so
/// <see cref="RollSelectionOutcome.Declined"/> is empty; the type is kept because it is part of the
/// outcome's positional shape and of the <c>/api/plugins/roll-target</c> answer.
/// </summary>
/// <param name="Version">The release.</param>
/// <param name="Reason">The joined hold reason — what a human reads first.</param>
/// <param name="Blockers">Every package that blocked it, so the plugin can be NAMED rather than
/// counted.</param>
public sealed record DeclinedRelease(
    string Version, string Reason, ImmutableArray<PackageAvailability> Blockers);

/// <summary>What the selection concluded. Every member is a DIFFERENT sentence on purpose: "we
/// chose", "there was nothing to choose from", "we could not look" and "this does not apply here"
/// are exactly the states a weaker selector spells identically.</summary>
public enum RollSelectionKind
{
    /// <summary>The newest admitted release is not what the environment runs ⇒ update to it. What
    /// its packages say about that release is on <see cref="RollSelectionOutcome.Advisories"/>.</summary>
    Update,

    /// <summary>The newest admitted release is the one already running ⇒ nothing to do. The happy
    /// steady state, and it is SAID rather than left as a silence.</summary>
    AlreadyCurrent,

    /// <summary>No longer produced by the walk under
    /// policy <c>platform-rolls-independently-of-packages</c>; kept for compatibility. It said the
    /// newest release that shipped all plugins was BEHIND the running version, reported and never
    /// applied. The walk now takes the head of the candidate list and examines nothing behind it.</summary>
    BehindCurrent,

    /// <summary>No longer produced by the walk under
    /// policy <c>platform-rolls-independently-of-packages</c>; kept for compatibility. It said every
    /// candidate was declined because an installed module was unloadable on it (or its sealed set
    /// was torn). Such a module is now named in the selected release's advisories.</summary>
    NoCompleteRelease,

    /// <summary>No longer produced by the walk under
    /// policy <c>platform-rolls-independently-of-packages</c>; kept for compatibility. It refused a
    /// selection over an EMPTY plugin list. The newest candidate is now selected and the outcome
    /// says that no package was checked (<see cref="RollSelection.Unjudged"/>).</summary>
    NoPluginsKnown,

    /// <summary>🚨 The selector could not SEE what to choose FROM — the release listing could not be
    /// read, or the selection did not answer within its budget. Nothing is selected. It is no
    /// longer the answer to an unread plugin list or published root when candidates are in hand:
    /// that is a package question, reported on the selected release.</summary>
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
/// The selection, and what the packages said about it. <see cref="Summary"/> always carries the
/// denominator — <i>"N plugin(s) required (from …); M of N satisfied by X"</i> — because a package
/// answer whose expected count nobody can read is one nobody can tell from a vacuous one.
/// </summary>
/// <param name="Kind">What was concluded.</param>
/// <param name="SelectedVersion">The newest admitted release, or null when nothing was selected
/// (no candidates, an unlistable candidate universe, or a selection that does not apply).</param>
/// <param name="CurrentVersion">What the environment runs.</param>
/// <param name="RequiredPlugins">The denominator — how many packages the verdict was asked about.</param>
/// <param name="SatisfiedPlugins">How many of them are available on the selected release. It can be
/// LESS than <paramref name="RequiredPlugins"/>: the difference is the packages that do not follow
/// the roll, named in <see cref="Advisories"/>. Zero when nothing was selected or no package was
/// checked.</param>
/// <param name="Declined">Candidates examined and refused. Empty from the walk, which refuses
/// none; kept as part of the positional shape.</param>
/// <param name="Summary">One line naming the environment, the denominator, the choice, the
/// boot-compile cost and what does not follow the roll — what a log line and an operator both read.</param>
public sealed record RollSelectionOutcome(
    RollSelectionKind Kind,
    string? SelectedVersion,
    string? CurrentVersion,
    int RequiredPlugins,
    int SatisfiedPlugins,
    ImmutableArray<DeclinedRelease> Declined,
    string Summary)
{
    /// <summary>
    /// What the selected release's verdict SAID without deciding on it
    /// (<see cref="UpdatabilityVerdict.Advisories"/>): first, when the package verdict refused or
    /// could not be made, the waived reason (<see cref="UpdatabilityVerdict.ForPlatformRoll"/>) or
    /// why no package was checked (<see cref="RollSelection.Unjudged"/>); then the boot-compile
    /// cost, a link check that could not be made, a declared floor the target does not rank above
    /// (#3651). Empty when nothing was selected or there was nothing to say. Init-only: the
    /// positional constructor is binary API.
    /// </summary>
    public ImmutableArray<string> Advisories { get; init; } = [];

    /// <summary>The packages the selected release would Roslyn-compile at boot (#3651,
    /// <see cref="UpdatabilityVerdict.BootCompiles"/>), by name; empty when nothing was selected or
    /// every content-bearing package has a sealed bake for it.</summary>
    public ImmutableArray<string> BootCompiles { get; init; } = [];

    /// <summary>Whether this outcome asks for a roll. <b>Only</b>
    /// <see cref="RollSelectionKind.Update"/> does — every other member is a state to report. An
    /// outcome that could not list its candidates selects nothing here; a caller that holds its
    /// own candidate list (the poller) still takes its head.</summary>
    public bool ShouldUpdate => Kind == RollSelectionKind.Update;

    /// <summary>Whether the selector could not SEE what to choose from
    /// (<see cref="RollSelectionKind.Indeterminate"/>). <see cref="RollSelectionKind.NoPluginsKnown"/>
    /// is still matched for compatibility and is no longer produced. A package answer that could
    /// not be made is NOT this: it is an advisory on a selected release.</summary>
    public bool IsIndeterminate =>
        Kind is RollSelectionKind.Indeterminate or RollSelectionKind.NoPluginsKnown;
}
