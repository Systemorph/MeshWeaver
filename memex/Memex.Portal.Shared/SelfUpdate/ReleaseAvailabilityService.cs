using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.SelfUpdate;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.SelfUpdate;

/// <summary>
/// 🚨 <b>The deployment gate (#1754): may this environment be rolled to that release?</b>
///
/// <para>An image is not rollable just because its version is newer — but since #3651 (maintainer
/// rule of 2026-09-07, <c>Doc/Architecture/ModuleAdoptionPolicy</c>) the ONLY thing that holds it
/// is MEASURED: a module this environment has landed, for which the target publishes no build,
/// whose bytes reference a type the target's platform surface does not carry. A missing content
/// bake is reported ("would recompile at boot: …") and the roll proceeds — the compile is what
/// every PR of that content already proved green; <c>Modules:RequirePrebuilt</c> keeps it a hold
/// for the instances that opt in. A declared <c>MinMeshVersion</c> floor is worded onto the
/// advisories and decides nothing. This service is what adds the environment's LANDED generations
/// (the activation sidecar) to the gate's inputs and runs the link measurement against the surface
/// the publication carries.</para>
///
/// <para><b>Why the service lives here, in the registry, and not in each portal's poller.</b> An
/// instance only knows itself; it cannot answer "is this release safe for environment X". Memex
/// holds the environment → instance → installed-package mapping and mounts the same published
/// bundle root every bake lane writes to, so it can answer for any environment. The verdict is
/// then readable by all three paths that roll a version — the self-update poll, CD's own
/// post-promote assertion, and a manual <c>kubectl set image</c> (through
/// <c>/api/plugins/is-updatable</c>) — because a gate only one path honours is not a gate.</para>
///
/// <para>🚨 <b>The consequence, designed for deliberately:</b> this makes the registry a dependency
/// of the environment's ability to update. When the catalogue cannot be read the answer is
/// <see cref="PackageAvailabilityKind.Indeterminate"/> — a HOLD, never a pass — and it is reported
/// as an availability failure with its own reason, never dressed up as a compatibility verdict.
/// The one applicability exemption is stated out loud by
/// <see cref="UpdatabilityVerdict.NotEnforced"/>: a deployment that consumes no CI bakes at all is
/// already compiling at every boot, so holding it could only freeze it forever.</para>
///
/// <para><b>What "deployed" means today</b> is the install records in the <c>Plugins</c> partition —
/// the same records the catalog and the bundle index read. Once #1735 lands, the per-environment
/// composition declares what an environment is SUPPOSED to have, which is strictly better to gate
/// on; this service's <see cref="RequiredPackages"/> is the one place that would change.</para>
/// </summary>
/// <remarks>
/// Not sealed, and <see cref="IsUpdatable"/> is virtual: it is the documented injection seam for the
/// poller's gate, exactly as <c>SelfUpdateHostedService.ReadPolicyStream</c>/<c>RecordAvailable</c>
/// are for its two mesh touches. A test can then pin what the POLLER does with a verdict without
/// also staging an artifact store — the verdict itself is pinned separately, against a real one.
/// </remarks>
public class ReleaseAvailabilityService(
    IMessageHub hub,
    IConfiguration configuration,
    ILogger<ReleaseAvailabilityService>? logger = null)
{
    private readonly IIoPool pool =
        hub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.FileSystem)
        ?? IoPool.Unbounded;

    /// <summary>The published bundle root this deployment mounts, or null when it consumes no CI
    /// bakes.</summary>
    public string? PublishedRoot => configuration[ShippedPrebuiltBundles.PublishedRootConfigKey];

    /// <summary>
    /// The instance's gate policy (#3651): <c>Modules:RequirePrebuilt</c> — off by default — is the
    /// one knob that turns a missing content bake from a reported cost back into a hold, because on
    /// such an instance the seeder refuses the boot compile and the type would park. Read from the
    /// SAME configuration the published root comes from — the host's — through the seeder's own
    /// parse rule, so the gate and the seeder cannot disagree about what the key says.
    /// </summary>
    public ReleaseGatePolicy Policy =>
        new(PrebuiltAssemblySeeder.RequirePrebuiltFromValue(
            configuration[PrebuiltAssemblySeeder.RequirePrebuiltConfigKey]));

    /// <summary>The module root every landed generation lives under — the same resolution the
    /// landing service and the boot loader share, so the gate measures the bytes boot would load.</summary>
    private string ModuleRootPath => ModuleRoot.Resolve(configuration);

    /// <summary>
    /// 🚨 <b>Does this gate APPLY to this deployment at all?</b> Returns the reason it does not, or
    /// <c>null</c> when it does.
    ///
    /// <para>Decided from CONFIGURATION alone, which is what makes it answerable by a caller that
    /// has no instance of this service — and that is the whole point. "Cannot verify" and "verified
    /// as nothing to verify" are different states, and only the first may hold. A deployment that
    /// consumes no CI bakes already compiles its content at every boot, so a registered gate would
    /// answer <see cref="UpdatabilityVerdict.NotEnforced"/> for it; the gate being ABSENT on such a
    /// deployment tells you nothing new, and holding on it would freeze an environment the gate was
    /// never going to protect.</para>
    ///
    /// <para>Static and shared on purpose: the poller's unwired path (#1754) and
    /// <see cref="IsUpdatable"/> must reach the same applicability answer, and a rule that only one
    /// caller honours is not a rule.</para>
    /// </summary>
    /// <param name="configuration">The host's configuration; null reads as "nothing configured".</param>
    public static string? NotApplicableReason(IConfiguration? configuration) =>
        string.IsNullOrWhiteSpace(configuration?[ShippedPrebuiltBundles.PublishedRootConfigKey])
            ? $"this deployment consumes no CI bakes ({ShippedPrebuiltBundles.PublishedRootConfigKey} "
              + "is not configured), so it already compiles its content at every boot — the "
              + "release-availability gate has nothing to enforce here"
            : null;

    /// <summary>
    /// Is <paramref name="targetVersion"/> a release this environment may be rolled to? Cold —
    /// the file-system leaves run on the <see cref="IoPoolNames.FileSystem"/> pool, never on a hub
    /// action block — and total: every failure resolves to a HOLD carrying its reason, so a
    /// caller can subscribe without a <c>Catch</c> that would turn an incident into a pass.
    /// </summary>
    public virtual IObservable<UpdatabilityVerdict> IsUpdatable(string? targetVersion) =>
        Observable.Defer(() =>
        {
            // ONE applicability rule, shared with the poller's unwired path — see NotApplicableReason.
            if (NotApplicableReason(configuration) is { } notApplicable)
                return Observable.Return(UpdatabilityVerdict.NotEnforced(notApplicable));

            var publishedRoot = PublishedRoot!;

            // 🚨 The DENOMINATOR is read FIRST, and from every identity the root holds except the
            // one under judgement — never from the target's own publication (#3441). See
            // PublishedBundleCatalogue.EverSealedBundles.
            return pool
                .InvokeBlocking(_ => PublishedBundleCatalogue.EverSealedBundles(publishedRoot, logger))
                .SelectMany(floor => Verdict(floor, publishedRoot, targetVersion));
        })
        // 🚨 The gate must ANSWER, always. Its two inputs can each stall indefinitely — a mesh
        // query that never emits its initial snapshot, an I/O pool slot that never frees — and a
        // gate that hangs is strictly worse than one that refuses: the poller's tick never
        // completes, so the update neither applies NOR records a hold, and the environment freezes
        // with nothing anywhere saying why. The timeout converts a stall into the honest answer
        // (Indeterminate ⇒ HOLD, named), which the very next tick re-evaluates from scratch.
        .Timeout(AnswerBudget)
        .Catch((Exception ex) => Observable.Return(ReleaseAvailability.IsUpdatable(
            new ReleaseTarget(targetVersion, null),
            [],
            ReleaseArtifacts.Unreadable(ex is TimeoutException
                ? $"the availability check did not answer within {AnswerBudget.TotalSeconds:0}s"
                : ex.Message))));

    /// <summary>
    /// How long the whole verdict may take. Generous — it bounds a stall, it is not a performance
    /// budget: the reads behind it are a mesh query and a handful of directory stats, so anything
    /// approaching this is wedged rather than slow. Deliberately shorter than the poll interval, so
    /// a stalled tick can never overlap the next one.
    /// </summary>
    private static readonly TimeSpan AnswerBudget = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 🚨 <b>The roll SELECTOR (#3479): which release should this environment be on?</b>
    ///
    /// <blockquote>Maintainer, 2026-09-06: <i>"Whenever a new platform / plugin is published, check
    /// for each environment which is the latest platform version shipping all plugins, if different
    /// from current version ⇒ update."</i></blockquote>
    ///
    /// <para><see cref="IsUpdatable"/> answers a question you must already know the answer to —
    /// <i>may I roll to THIS version?</i> — so every caller still has to choose the version itself,
    /// and the only choice available was "the newest". This inverts it: completeness becomes the
    /// SELECTION criterion, so an environment never targets a release that does not ship all of its
    /// plugins and there is nothing left for a readiness gate to refuse.</para>
    ///
    /// <para><b>The predicate is the same one.</b> Each candidate is judged by exactly the code
    /// <see cref="IsUpdatable"/> runs — <see cref="ReleaseAvailability.IsUpdatable(ReleaseTarget, IEnumerable{RequiredPackage}, ReleaseArtifacts)"/> over
    /// <see cref="PublishedBundleCatalogue"/>'s observation — and the walk itself lives in
    /// <see cref="RollSelection"/>. There is deliberately no second copy of "does this release ship
    /// all plugins" to drift from the first.</para>
    ///
    /// <para><b>It costs ONE inventory read per selection, not one per candidate.</b> The mesh query
    /// and the monotone denominator are read once, before the walk; only the per-candidate
    /// observation is repeated, and only until one candidate clears.</para>
    ///
    /// <para>Cold and total, like <see cref="IsUpdatable"/>: every failure resolves to an outcome
    /// carrying its reason, so a caller can subscribe without a <c>Catch</c> that would turn an
    /// incident into a selection.</para>
    /// </summary>
    /// <param name="currentVersion">What this environment runs — <c>different from current ⇒
    /// update</c> is the algorithm's own comparison, so it has to be given.</param>
    /// <param name="candidatesNewestFirst">The releases to choose from, newest first. Null ⇒ every
    /// release the published root carries a marker for, ordered by <see cref="VersionSelect"/> under
    /// <paramref name="policy"/> — which lets an environment answer for itself without listing a
    /// container registry.</param>
    /// <param name="policy">Which releases are eligible when the candidates are derived. Default
    /// <see cref="UpdatePolicyKind.Continuous"/>: the question "which release ships all plugins" is
    /// about the PUBLICATIONS, and whether this install would apply the answer is a separate
    /// decision that stays with the poller.</param>
    /// <param name="condemned">Optional second opinion applied to each candidate BEFORE it can be
    /// selected — in production the combo gate's recorded refusal, which answers the question an
    /// artifact cannot: whether the candidate's assemblies can still serve the modules this
    /// instance has landed. Returns the refusal, or null when it has nothing against the candidate.
    /// 🚨 Only a REFUSAL removes a candidate; "not verified" is neither preferred nor condemned, so
    /// an unwired verifier can never empty the candidate list.</param>
    /// <param name="requireCiGreen">Exclude unverified <c>edge</c> builds when deriving candidates.</param>
    public virtual IObservable<RollSelectionOutcome> SelectRollTarget(
        string? currentVersion,
        IReadOnlyList<string>? candidatesNewestFirst = null,
        Func<string, string?>? condemned = null,
        UpdatePolicyKind policy = UpdatePolicyKind.Continuous,
        bool requireCiGreen = true) =>
        Observable.Defer(() =>
        {
            var environment = EnvironmentName();

            // The ONE applicability rule, shared with IsUpdatable and the poller's unwired path.
            // A deployment that consumes no CI bakes has nothing to select ON: it compiles its
            // content at every boot, so every release ships all its plugins in the only sense that
            // can be observed here. Saying so is not the same as selecting the newest.
            if (NotApplicableReason(configuration) is { } notApplicable)
                return Observable.Return(NotEnforced(environment, currentVersion, notApplicable));

            var publishedRoot = PublishedRoot!;

            return pool
                .InvokeBlocking(_ => PublishedBundleCatalogue.EverSealedBundles(publishedRoot, logger))
                .SelectMany(floor =>
                    // 🚨 Tested FIRST, and separately from ServesBakes: a configured root that does
                    // not exist or faults on read produces a floor with no bundles — the SAME SHAPE
                    // as a root that genuinely serves none — and they mean opposite things. See
                    // Verdict() for the full reasoning; this is the same order, one level up.
                    floor.Refusal is { } refusal
                        ? Observable.Return(Indeterminate(environment, currentVersion, refusal))
                        : !floor.ServesBakes
                            ? Observable.Return(NotEnforced(
                                environment, currentVersion, NoPublicationsReason(publishedRoot)))
                            : Select(environment, currentVersion, publishedRoot, floor,
                                candidatesNewestFirst, condemned, policy, requireCiGreen))
                .Do(outcome => logger?.LogInformation("[RollSelect] {Summary}", outcome.Summary));
        })
        // 🚨 The selector must ANSWER, always — same reasoning as IsUpdatable's budget. A selector
        // that hangs is worse than one that declines: the tick never completes, so the environment
        // neither updates NOR records why, and freezes with nothing anywhere saying so.
        .Timeout(AnswerBudget)
        .Catch((Exception ex) => Observable.Return(Indeterminate(
            EnvironmentName(),
            currentVersion,
            ex is TimeoutException
                ? $"the roll selection did not answer within {AnswerBudget.TotalSeconds:0}s"
                : ex.Message)));

    /// <summary>
    /// The selection proper, once the gate is known to apply and the denominator's floor has been
    /// read: list the candidates, read the inventory ONCE, then walk.
    /// </summary>
    private IObservable<RollSelectionOutcome> Select(
        string environment,
        string? currentVersion,
        string publishedRoot,
        SealedBundleFloor floor,
        IReadOnlyList<string>? candidatesNewestFirst,
        Func<string, string?>? condemned,
        UpdatePolicyKind policy,
        bool requireCiGreen) =>
        Candidates(publishedRoot, candidatesNewestFirst, policy, requireCiGreen)
            .SelectMany(candidates => candidates.Refusal is { } listing
                // 🚨 An unlistable candidate universe is an "I could not look", never an empty
                // choice: reporting it as "no releases to choose from" would read as a healthy
                // up-to-date environment when the storage is simply unreachable.
                ? Observable.Return(Indeterminate(environment, currentVersion, listing))
                : RequiredPackages(floor).SelectMany(required => RollSelection.Select(
                    new RollSelectionInputs(
                        environment,
                        currentVersion,
                        candidates.Versions,
                        PluginInventory.Of(required, InventorySource)),
                    version => Observe(publishedRoot, required, version, condemned))));

    /// <summary>
    /// One candidate's verdict, produced by exactly the code <see cref="IsUpdatable"/> runs — the
    /// SHARED predicate, handed the inventory the walk already read rather than re-reading it per
    /// candidate — and then, only if it cleared, the caller's second opinion.
    /// </summary>
    private IObservable<UpdatabilityVerdict> Observe(
        string publishedRoot,
        ImmutableArray<RequiredPackage> required,
        string version,
        Func<string, string?>? condemned) =>
        Judge(publishedRoot, required, version)
            .Do(LogAdvisories)
            .Select(verdict => verdict.IsUpdatable && condemned?.Invoke(version) is { } refusal
                // Folded into the SAME verdict shape rather than kept beside it, so the walk has
                // one notion of "this candidate is out" and the refusal travels with its reason.
                ? verdict with
                {
                    IsUpdatable = false,
                    Packages = verdict.Packages.Add(new PackageAvailability(
                        "(combo)", PackageAvailabilityKind.ComboVerificationFailed, refusal)),
                    HoldReason = refusal,
                }
                : verdict);

    /// <summary>
    /// The candidate universe: what the caller supplied, or every release the published root has a
    /// marker for, ordered newest-first by <see cref="VersionSelect"/>. A caller-supplied list is
    /// taken AS ORDERED — it already applied its own policy.
    /// </summary>
    private IObservable<PublishedReleaseCatalogue> Candidates(
        string publishedRoot,
        IReadOnlyList<string>? supplied,
        UpdatePolicyKind policy,
        bool requireCiGreen) =>
        supplied is not null
            ? Observable.Return(new PublishedReleaseCatalogue([.. supplied], null))
            : PublishedBundleCatalogue
                .ObserveReleases(pool, publishedRoot, logger)
                .Select(catalogue => catalogue.Refusal is not null
                    ? catalogue
                    : catalogue with
                    {
                        Versions =
                        [
                            .. VersionSelect.PickTargets(
                                catalogue.Versions, policy, requireCiGreen),
                        ],
                    });

    /// <summary>
    /// 🚨 <b>WHAT "this environment's plugins" means, answered once and named in every outcome.</b>
    ///
    /// <para>It is the <b>install records</b> — <c>Plugins/*</c>, <c>nodeType:Package</c> — and the
    /// reason is not that they are the only reading available. They are not: an instance's modules
    /// also arrive as per-Space <c>_GitSync</c> entries, and <see cref="InstanceComboReader"/> folds
    /// both because a reader of one alone under-reports (measured on memex 2026-08-10: 42 sync
    /// entries, ZERO install records). Two things nonetheless decide it here:</para>
    /// <list type="number">
    /// <item><description>The gate already reads them, and the predicate is SHARED. A selector whose
    /// denominator differs from the gate's could choose a release the gate then holds — two
    /// completeness answers about one environment, which is the drift this change exists to
    /// prevent.</description></item>
    /// <item><description>Only an install record carries the <b>package id the bake names its bundle
    /// by</b>. A sync entry names a PARTITION; the published root holds <c>&lt;bundle&gt;.zip</c>.
    /// Folding sync entries in would add names that can never match a bundle and hold every
    /// environment forever.</description></item>
    /// </list>
    ///
    /// <para>🚨 The failure mode of that source — a read that answers EMPTY — is no longer a pass:
    /// <see cref="RollSelectionKind.NoPluginsKnown"/> refuses it by name. That is the whole point of
    /// stating the source rather than assuming it.</para>
    /// </summary>
    private const string InventorySource =
        "the install records (Plugins/*, nodeType:Package)";

    /// <summary>The environment being selected FOR. The algorithm's quantifier is per environment,
    /// so an outcome that cannot name one says so rather than pretending to be global.</summary>
    private string EnvironmentName() =>
        configuration[DeploymentReportService.DeploymentKey] is { Length: > 0 } deployment
            ? deployment
            : "this deployment (Hosting:Deployment is not configured)";

    private static RollSelectionOutcome NotEnforced(
        string environment, string? currentVersion, string reason) =>
        new(RollSelectionKind.NotEnforced, null, currentVersion, 0, 0, [],
            $"{environment}: no release is selected — {reason}");

    private static RollSelectionOutcome Indeterminate(
        string environment, string? currentVersion, string reason) =>
        new(RollSelectionKind.Indeterminate, null, currentVersion, 0, 0, [],
            $"{environment}: the roll selection could not be made ({reason}) — cannot determine "
            + "which release ships all plugins, which is not clearance to roll to the newest one.");

    /// <summary>
    /// The verdict, given what the denominator observation turned out to be. Three outcomes, and
    /// the ORDER is the whole point.
    ///
    /// <para>🚨 <b>An unreadable root is a HOLD, and it is tested FIRST.</b> A configured root that
    /// does not exist, or that faults on read, produces a floor with no bundles — the same shape as
    /// a root that genuinely serves no bakes. They mean opposite things: the first is a mis-mounted
    /// volume or a mistyped path on a deployment that DECLARES it consumes CI bakes (an availability
    /// incident: cannot determine, which is not clearance to proceed), the second is the one stated
    /// applicability exemption. Collapsing them would let a mis-mount clear the gate — the exact
    /// vacuity this change removes, reintroduced one level up.</para>
    /// </summary>
    private IObservable<UpdatabilityVerdict> Verdict(
        SealedBundleFloor floor, string publishedRoot, string? targetVersion)
    {
        // 🚨 UpdatabilityVerdict.Unavailable, not IsUpdatable(target, [], Unreadable(...)): the
        // latter answers IsUpdatable=false but IsIndeterminate=FALSE, because that property reads
        // the per-package verdicts and an empty package list has none. Callers separate "I could
        // not look" (an availability incident to fix) from "I looked and it is incompatible" (a
        // release to re-bake) on exactly that flag, so an unreadable denominator reported without
        // it would be surfaced as a compatibility verdict about the release — the conflation
        // #1754 forbids.
        if (floor.Refusal is { } refusal)
            return Observable.Return(UpdatabilityVerdict.Unavailable(refusal));

        if (!floor.ServesBakes)
            return Observable.Return(
                UpdatabilityVerdict.NotEnforced(NoPublicationsReason(publishedRoot)));

        return RequiredPackages(floor)
            .SelectMany(required => required.IsEmpty
                // 🚨 THE VACUITY REFUSAL (#3479). With no packages required, every release is
                // vacuously available and this gate answers a confident green having compared two
                // empty sets — the SAME shape #3441 removed from HasContent, reappearing at the
                // package LIST itself. It is a HOLD, and Unavailable rather than a
                // compatibility verdict because the remedy is to fix the read (or to stop
                // declaring that this deployment consumes CI bakes), never to re-bake a release.
                //
                // The case is not hypothetical: this environment's install records measured ZERO
                // on 2026-08-10 while it carried 42 modules. It is also self-clearing — the
                // verdict is re-evaluated from scratch on every tick, so a portal that has not yet
                // written its records holds for one interval and then proceeds.
                ? Observable.Return(UpdatabilityVerdict.Unavailable(VacuousDenominatorReason))
                : Judge(publishedRoot, required, targetVersion).Do(LogAdvisories));
    }

    /// <summary>
    /// 🚨 THE SHARED PREDICATE, with its two IO halves (#3651) — read the target's publication
    /// (<see cref="PublishedBundleCatalogue.Read"/>), link every landed module against the surface
    /// it carries (<see cref="ModuleLinkObservation.Measure"/>), then decide
    /// (<see cref="ReleaseAvailability.IsUpdatable(ReleaseTarget, IEnumerable{RequiredPackage}, ReleaseArtifacts, ReleaseGatePolicy)"/>).
    /// Both IO halves run on the file-system pool, never on a hub action block; the rule is pure.
    /// Every roll path — the poller's selection, the per-version verdict, the endpoints — comes
    /// through here, so no caller can judge a candidate without the measurement.
    /// </summary>
    private IObservable<UpdatabilityVerdict> Judge(
        string publishedRoot, ImmutableArray<RequiredPackage> required, string? version)
    {
        var policy = Policy;
        return pool.InvokeBlocking(_ =>
        {
            var observation = PublishedBundleCatalogue.Read(publishedRoot, version, logger);
            var measured = ModuleLinkObservation.Measure(observation.Artifacts, required, logger);
            return ReleaseAvailability.IsUpdatable(observation.Target, required, measured, policy);
        });
    }

    /// <summary>
    /// 🚨 The advisories SAY something and decide nothing, so they are logged (Information) beside
    /// the verdict rather than folded into it: the boot-compile cost and a link check that could
    /// not be made (#3651), and the declared floors (#3648). On 2026-09-07 the floor sentences were
    /// the HOLD reasons ("77 plugins required … every one declined"); now they are what an
    /// operator reads while the roll proceeds on what is measured.
    /// </summary>
    private void LogAdvisories(UpdatabilityVerdict verdict)
    {
        if (verdict.Advisories.IsDefaultOrEmpty)
            return;
        logger?.LogInformation(
            "[ReleaseGate] {Count} advisory line(s) beside the verdict — reported, never a hold "
            + "(#3651/#3648): {Advisories}",
            verdict.Advisories.Length, string.Join("; ", verdict.Advisories));
    }

    /// <summary>Why an empty denominator is a hold rather than a pass — see the call site.</summary>
    private const string VacuousDenominatorReason =
        "this deployment consumes CI bakes and its published root serves them, yet it reports ZERO "
        + "installed packages — so every release would 'ship all' of nothing and this gate would "
        + "pass having compared two empty sets. Cannot determine availability, which is not "
        + "clearance to proceed. Either the install records (Plugins/*, nodeType:Package) are not "
        + "being read, or this deployment genuinely deploys no packages — in which case "
        + $"{ShippedPrebuiltBundles.PublishedRootConfigKey} should not be configured for it.";

    /// <summary>
    /// Why the gate does not apply to a root that holds no sealed publication under ANY identity.
    ///
    /// <para>🚨 This case used to be a silent PASS, and it was the worst one. With no publication
    /// under the live identity every installed package was judged non-content-bearing, so the
    /// content half of the gate checked NOTHING and still reported updatable — a green tick over
    /// zero evidence. It is stated here instead: an instance whose root serves no bakes already
    /// compiles its content at every boot, exactly like one with no root configured, so the honest
    /// answer is the same <see cref="UpdatabilityVerdict.NotEnforced"/> — <b>updatable with a
    /// reason the caller logs</b>, never a verdict that pretends to have looked.</para>
    /// </summary>
    private static string NoPublicationsReason(string publishedRoot) =>
        $"the published bundle root '{publishedRoot}' holds no sealed publication under any "
        + "framework identity, so this deployment adopts nothing today and already compiles its "
        + "content at every boot — the release-availability gate has nothing to enforce here. "
        + "(If that is unexpected, the bake lanes are not reaching this environment's storage: "
        + "check BAKE_PUBLISH_TARGETS and the publish-bake runs.)";

    /// <summary>
    /// What this environment deploys, as the gate's inputs.
    ///
    /// <para>🚨 <b>Content is required by EVIDENCE, and the evidence may not come from the artifact
    /// under judgement (#3441).</b> A package counts as content-bearing when it has EVER been
    /// sealed under any framework identity this root holds
    /// (<see cref="PublishedBundleCatalogue.EverSealedBundles(string?, Microsoft.Extensions.Logging.ILogger?)"/>)
    /// — not, as before, when it happens to be sealed under the identity running right now.</para>
    ///
    /// <para>The old reading made the denominator a function of the very thing that breaks: a
    /// package whose bake stopped being produced silently left the set of packages the gate asks
    /// about, so every subsequent roll was green about precisely the package that had regressed —
    /// <i>"we kept rolling without edu being properly baked"</i>. Reading it monotonically makes
    /// the gate say what it is for: <b>what this environment has ever been able to adopt, it must
    /// still be able to adopt</b>.</para>
    ///
    /// <para>The exemption that made the old reading attractive is preserved exactly, and for the
    /// same reason: a module-only or NodeType-less package produces no bundle ever, has therefore
    /// never been sealed under any identity, and is still not demanded — demanding one would hold
    /// that environment forever, and an environment silently frozen for weeks is its own
    /// outage.</para>
    /// </summary>
    private IObservable<ImmutableArray<RequiredPackage>> RequiredPackages(SealedBundleFloor floor) =>
        InstalledPackages()
            // The activation sidecar is a file read, so the package list is built on the
            // file-system pool rather than on the query's hub thread.
            .SelectMany(installed => pool.InvokeBlocking(_ => BuildRequiredPackages(installed, floor)));

    /// <summary>
    /// The gate's inputs from the install records plus the ACTIVATION SIDECAR (#3651): each
    /// package's module by name (the record's <c>module</c> field) and, when a generation of it is
    /// landed on this instance, the entry DLL of the ACTIVE generation — through the one resolution
    /// rule boot and the landing service share (<see cref="ModuleActivationBoot.LandedDllPath"/>),
    /// so what the gate links against the target's surface is exactly what boot would load.
    /// </summary>
    private ImmutableArray<RequiredPackage> BuildRequiredPackages(
        ImmutableArray<PackageManifest> installed, SealedBundleFloor floor)
    {
        var moduleRoot = ModuleRootPath;
        var landed = ModuleActivationSidecar.Read(moduleRoot,
                message => logger?.LogWarning("[ReleaseGate] activation sidecar: {Message}", message))
            .Entries
            .Where(entry => entry.Enabled && !string.IsNullOrWhiteSpace(entry.Name))
            .GroupBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);

        var required = installed
            .Select(manifest => new RequiredPackage(
                manifest.Id,
                manifest.Id,
                // 🚨 #3648 — the declared floor passes through AS DECLARED. It used to
                // be filtered to "only when the running platform already satisfies it"
                // (LiveFloorOf), a regression-check reading that still held every
                // production portal on 2026-09-07: the predicate treated an unmet floor
                // as a BLOCKER. It is an advisory now (UpdatabilityVerdict.Advisories),
                // so there is nothing to protect the verdict from.
                manifest.MinMeshVersion,
                floor.Bundles.Contains(manifest.Id))
            {
                ModuleName = string.IsNullOrWhiteSpace(manifest.Module) ? null : manifest.Module,
                LandedModulePath = LandedPathOf(moduleRoot, manifest.Module, landed),
            })
            .ToImmutableArray();
        // 🚨 PRINT THE DENOMINATOR. A completeness gate whose expected count nobody can
        // read is one nobody can tell from a vacuous one — the count being non-zero is
        // the claim, so it is logged rather than inferred from a pass.
        logger?.LogInformation(
            "[ReleaseGate] denominator: {ContentBearing} of {Installed} installed package(s) "
            + "carry content ({Landed} with a landed module generation to link), taken from "
            + "{Identities} framework identity(ies) this root has published: {Packages}",
            required.Count(p => p.HasContent),
            required.Length,
            required.Count(p => p.LandedModulePath is not null),
            floor.Identities,
            string.Join(", ", required.Where(p => p.HasContent).Select(p => p.Name).Order(StringComparer.Ordinal)));
        return required;
    }

    /// <summary>The ACTIVE landed generation's entry DLL for a package's module, or null when the
    /// package ships no module, none is landed, or the landed bytes are gone (then nothing would
    /// load across the roll, and there is nothing to measure).</summary>
    private static string? LandedPathOf(
        string moduleRoot, string? moduleName, IReadOnlyDictionary<string, ModuleActivationEntry> landed)
    {
        if (string.IsNullOrWhiteSpace(moduleName) || !landed.TryGetValue(moduleName, out var entry))
            return null;
        return ModuleActivationBoot.LandedModuleDllExists(moduleRoot, entry)
            ? ModuleActivationBoot.LandedDllPath(moduleRoot, entry)
            : null;
    }

    /// <summary>
    /// This environment's install records — the same query the bundle index serves from, so the
    /// gate and the catalogue can never disagree about what is installed.
    /// </summary>
    private IObservable<ImmutableArray<PackageManifest>> InstalledPackages() =>
        hub.ServiceProvider.GetRequiredService<IMeshService>()
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"namespace:{PackageInstaller.InstalledPartition} "
                + $"nodeType:{PackageInstaller.PackageNodeType}"))
            .Where(change => change.ChangeType == QueryChangeType.Initial)
            .Take(1)
            .Select(change => change.Items
                .Select(node => node.ContentAs<PackageManifest>(hub.JsonSerializerOptions))
                .Where(manifest => manifest is { Id.Length: > 0 })
                .Select(manifest => manifest!)
                .ToImmutableArray());
}
