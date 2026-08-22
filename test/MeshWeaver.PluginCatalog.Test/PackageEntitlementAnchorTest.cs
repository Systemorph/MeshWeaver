#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// 🚨 <b>THE ENTITLEMENT ANCHOR</b> (#1782 gap 2) — that the REGISTRY answers, that a local install
/// record is only a cache, and that the absence of one never comes out as "not entitled".
///
/// <para>The rule these pin is the maintainer's decision on the issue:</para>
/// <code>
/// anchor:   the entitlement record at the registry
/// local:    install record = cache
/// absent:   "ask upstream" — never "not entitled"
/// </code>
///
/// <para>Pure: no hub, no HTTP, no clock. <c>PluginBundleEntitlementTest</c> pins the same rule end
/// to end over a real registration, a real grant and a real HTTP round trip; these pin the decision
/// itself, including the two branches that a live test cannot force — a registry that will not
/// answer, and a package nothing has ever observed.</para>
/// </summary>
public class PackageEntitlementAnchorTest
{
    private const string Package = "SomePaidCourse";
    private const string PlatformSource = "Plugins";
    private const string PaidSource = "Education";

    /// <summary>A caller granted exactly one source — the shape a real registration has.</summary>
    private static Func<string, bool> GrantOf(string source) =>
        candidate => string.Equals(candidate, source, StringComparison.OrdinalIgnoreCase);

    /// <summary>A caller granted nothing. "Registering is identity, not entitlement."</summary>
    private static Func<string, bool> GrantsNothing => _ => false;

    // ── the load-bearing direction: absence must not deny ──────────────────────────────────────

    /// <summary>
    /// 🚨 <b>An entitled caller with NO local install record is served.</b>
    ///
    /// <para>This is the whole issue. The binding used to come from the install record and from
    /// nowhere else, so a package the serving instance had not itself installed had nothing to match
    /// a grant against — and the answer to "I cannot tell which source this is from" was "you are
    /// not entitled to it". A paying customer on a fresh instance is denied by that rule for no
    /// reason other than that nothing has installed there yet.</para>
    /// </summary>
    [Fact]
    public void EntitledWithNoLocalRecordIsGrantedFromTheAnchor()
    {
        var decision = PackageEntitlementAnchor.Resolve(
            Package,
            anchorSource: PlatformSource,
            cachedSource: null,
            anchorAvailable: true,
            GrantOf(PlatformSource));

        decision.Outcome.Should().Be(EntitlementOutcome.Granted,
            "the registry carries the package and the caller's grant covers that source — the "
            + "absence of a local install record says nothing about entitlement");
        decision.Anchor.Should().Be(EntitlementAnchorKind.Registry);
        decision.IsAuthoritative.Should().BeTrue();
        decision.IsDegraded.Should().BeFalse();
    }

    /// <summary>
    /// 🚨 <b>An unreachable registry produces the THIRD state, not a denial.</b>
    ///
    /// <para>Nothing was ever observed here and the anchor cannot be asked, so entitlement is
    /// UNKNOWN. The bytes are still withheld — there is no answer to serve from — but the outcome
    /// must not be reported as a refusal of entitlement, because an instance being unable to ASK is
    /// not a customer failing to buy. That is the difference between a stated degradation and the
    /// silent-failure family this whole change exists to leave.</para>
    /// </summary>
    [Fact]
    public void AnUnreachableRegistryAnswersUnknownRatherThanDenied()
    {
        var decision = PackageEntitlementAnchor.Resolve(
            Package,
            anchorSource: null,
            cachedSource: null,
            anchorAvailable: false,
            GrantOf(PlatformSource));

        decision.Outcome.Should().Be(EntitlementOutcome.Indeterminate,
            "'I could not find out' and 'you did not buy it' are different answers, and only one of "
            + "them may be asserted");
        decision.Outcome.Should().NotBe(EntitlementOutcome.Denied);
        decision.Anchor.Should().Be(EntitlementAnchorKind.None);
        decision.IsDegraded.Should().BeTrue("an unanswerable question must be legible, not inferred");
        decision.Serves.Should().BeFalse("the third state still withholds the bytes — it differs in "
            + "what it CLAIMS, not in what it hands over");
        decision.Reason.Should().Contain("not a denial");
    }

    /// <summary>
    /// An unreachable registry does not block a caller whose entitlement WAS previously observed:
    /// the cached binding answers, and says it is a cache.
    /// </summary>
    [Fact]
    public void AnUnreachableRegistryFallsBackToThePreviouslyObservedBinding()
    {
        var decision = PackageEntitlementAnchor.Resolve(
            Package,
            anchorSource: null,
            cachedSource: PlatformSource,
            anchorAvailable: false,
            GrantOf(PlatformSource));

        decision.Outcome.Should().Be(EntitlementOutcome.Granted,
            "fail toward not blocking a viewer whose entitlement was previously observed");
        decision.Anchor.Should().Be(EntitlementAnchorKind.Cache);
        decision.IsAuthoritative.Should().BeFalse(
            "🚨 a cache must be able to say it is a cache — otherwise it silently becomes the anchor "
            + "again by accident");
        decision.IsDegraded.Should().BeTrue();
    }

    // ── the deny direction, which must not be lost ─────────────────────────────────────────────

    /// <summary>
    /// 🚨 A caller who is genuinely not entitled still sees nothing — the anchor carries the package,
    /// in a source their grant does not cover. This is the paid-content boundary: holding
    /// <c>Plugins/*</c> must not sweep in a 900 CHF course from <c>Education</c>.
    /// </summary>
    [Fact]
    public void NotEntitledIsStillDenied()
    {
        var decision = PackageEntitlementAnchor.Resolve(
            Package,
            anchorSource: PaidSource,
            cachedSource: null,
            anchorAvailable: true,
            GrantOf(PlatformSource));

        decision.Outcome.Should().Be(EntitlementOutcome.Denied);
        decision.Anchor.Should().Be(EntitlementAnchorKind.Registry);
        decision.Serves.Should().BeFalse();
    }

    /// <summary>A caller granted nothing is denied whatever the binding says.</summary>
    [Fact]
    public void AGrantOfNothingIsDeniedFromEitherAnchor()
    {
        PackageEntitlementAnchor.Resolve(Package, PlatformSource, null, true, GrantsNothing)
            .Outcome.Should().Be(EntitlementOutcome.Denied);
        PackageEntitlementAnchor.Resolve(Package, null, PlatformSource, false, GrantsNothing)
            .Outcome.Should().Be(EntitlementOutcome.Denied,
                "a degraded answer is still an answer — falling back to the cache does not widen a "
                + "grant, it only decides which source the grant is asked about");
    }

    /// <summary>
    /// The registry answered IN FULL and carries no such package, and nothing local ever observed
    /// one. That is a real negative, and it must stay one — a rule that never denies is not an
    /// entitlement check.
    /// </summary>
    [Fact]
    public void AFullRegistryAnswerWithNoSuchPackageIsARealDenial()
    {
        var decision = PackageEntitlementAnchor.Resolve(
            Package, anchorSource: null, cachedSource: null, anchorAvailable: true,
            GrantOf(PlatformSource));

        decision.Outcome.Should().Be(EntitlementOutcome.Denied,
            "the anchor was consulted completely — its silence about this package IS an observation");
        decision.Anchor.Should().Be(EntitlementAnchorKind.None);
        decision.IsDegraded.Should().BeFalse();
    }

    // ── provenance: the anchor outranks the cache, and staleness is visible ────────────────────

    /// <summary>
    /// 🚨 When both bind the package, the ANCHOR decides — including when they disagree. A cache
    /// that outranked the registry would be the anchor by another name, and a package moved between
    /// sources would keep resolving against a binding nobody maintains.
    /// </summary>
    [Fact]
    public void TheAnchorOverridesADisagreeingCache()
    {
        // The cache still says "Plugins" (where it was installed from); the registry now carries it
        // in the paid source. The caller holds only Plugins/*.
        var decision = PackageEntitlementAnchor.Resolve(
            Package,
            anchorSource: PaidSource,
            cachedSource: PlatformSource,
            anchorAvailable: true,
            GrantOf(PlatformSource));

        decision.Outcome.Should().Be(EntitlementOutcome.Denied,
            "a stale cached binding must not keep granting what the registry has moved behind a "
            + "source this caller does not hold");
        decision.Source.Should().Be(PaidSource);
        decision.Anchor.Should().Be(EntitlementAnchorKind.Registry);
    }

    /// <summary>
    /// A binding the anchor DID return is authoritative even when a different source failed to
    /// list: one source being down cannot make another source's answer less true. Only an ABSENCE
    /// loses its meaning.
    /// </summary>
    [Fact]
    public void APartialReadStillAnchorsWhatItDidReturn()
    {
        var decision = PackageEntitlementAnchor.Resolve(
            Package, anchorSource: PlatformSource, cachedSource: null, anchorAvailable: false,
            GrantOf(PlatformSource));

        decision.Anchor.Should().Be(EntitlementAnchorKind.Registry);
        decision.Outcome.Should().Be(EntitlementOutcome.Granted);
        decision.IsDegraded.Should().BeTrue("the READ was incomplete, and that stays reportable even "
            + "though this particular answer did not need the missing part");
    }

    // ── the ledger ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🚨 "Nothing was ever asked" and "everything was answered authoritatively" are different
    /// sentences — the same rule <c>BundleAdoptionLedger</c> follows. Reporting an instance that
    /// resolves nothing as a clean sweep makes the absence of the lane look like the success of it.
    /// </summary>
    [Fact]
    public void TheLedgerDistinguishesSilenceFromSuccess()
    {
        var ledger = new PackageEntitlementLedger();
        ledger.Describe().Should().Contain("no package entitlement has been resolved");

        ledger.Record(PackageEntitlementAnchor.Resolve(
            Package, PlatformSource, null, true, GrantOf(PlatformSource)));
        ledger.Describe().Should().Contain("all answered against the registry anchor");
        ledger.Degraded.Should().BeEmpty();

        ledger.Record(PackageEntitlementAnchor.Resolve("Other", null, null, false, GrantsNothing));
        ledger.Degraded.Should().HaveCount(1);
        ledger.Indeterminate.Should().HaveCount(1);
        ledger.Describe().Should().Contain("UNKNOWN, not denials");
    }

    /// <summary>Bounded — a diagnostic on a long-lived process answers "is the anchor working now",
    /// and an unbounded list would answer it and also leak.</summary>
    [Fact]
    public void TheLedgerIsBounded()
    {
        var ledger = new PackageEntitlementLedger();
        for (var i = 0; i < PackageEntitlementLedger.Capacity + 25; i++)
            ledger.Record(PackageEntitlementAnchor.Resolve(
                $"P{i}", PlatformSource, null, true, GrantOf(PlatformSource)));

        ledger.Decisions.Count.Should().Be(PackageEntitlementLedger.Capacity);
        ledger.Decisions[^1].PackageId.Should()
            .Be($"P{PackageEntitlementLedger.Capacity + 24}", "the NEWEST are the ones kept");
    }

    // ── the READ that feeds the anchor ─────────────────────────────────────────────────────────

    /// <summary>An instance that configures no sources is an authority on nothing — deliberately
    /// NOT "the registry carries no such package".</summary>
    [Fact]
    public async Task NoConfiguredSourcesIsUnconfiguredRatherThanAuthoritative()
    {
        var anchor = new PackageOriginAnchor(() => [], TimeSpan.Zero, () => DateTimeOffset.UnixEpoch);
        var snapshot = await anchor.Read().FirstAsync().ToTask();

        snapshot.State.Should().Be(AnchorState.Unconfigured);
        snapshot.IsComplete.Should().BeFalse(
            "🚨 'I have no sources' answering 'the registry carries no such package' is precisely "
            + "the absence-denies bug");
    }

    /// <summary>A source that lists successfully binds its packages, and the snapshot is complete —
    /// so an absence from it is a real negative.</summary>
    [Fact]
    public async Task ASuccessfulReadIsAuthoritative()
    {
        var anchor = Anchor(Listing(PlatformSource, Manifest(Package, "1.4.0")));
        var snapshot = await anchor.Read().FirstAsync().ToTask();

        snapshot.State.Should().Be(AnchorState.Authoritative);
        snapshot.IsComplete.Should().BeTrue();
        snapshot.SourceOf(Package).Should().Be(PlatformSource);
        snapshot.SourceOf("NeverPublished").Should().BeNull();
    }

    /// <summary>
    /// 🚨 A source that will not list makes the snapshot NON-authoritative — and never throws.
    /// A fault here would have to be turned into a decision by the caller, and the only safe
    /// decision from an exception is the denial this change exists to remove.
    /// </summary>
    [Fact]
    public async Task AFailingSourceDegradesRatherThanFaults()
    {
        var anchor = Anchor(Failing(PlatformSource, "GitHub said 502"));
        var snapshot = await anchor.Read().FirstAsync().ToTask();

        snapshot.State.Should().Be(AnchorState.Unreachable,
            "nothing was listed and nothing had ever been observed");
        snapshot.IsComplete.Should().BeFalse();
        snapshot.Failure.Should().Contain("GitHub said 502");
    }

    /// <summary>
    /// The previously observed bindings survive a failure — that is the mechanism behind "fail
    /// toward not blocking a viewer whose entitlement was previously observed".
    /// </summary>
    [Fact]
    public async Task APreviousObservationSurvivesTheNextFailure()
    {
        var failing = false;
        var anchor = new PackageOriginAnchor(
            () =>
            [
                new ConfiguredPackageSource(
                    new StubSource(
                        failing ? null : [Manifest(Package, "1.4.0")],
                        failing ? new InvalidOperationException("registry down") : null),
                    "HEAD", PlatformSource),
            ],
            // 🚨 No reuse window: the second read must genuinely go back to the source, or this test
            // would pass by reading the cached snapshot and prove nothing.
            TimeSpan.Zero, () => DateTimeOffset.UnixEpoch);

        (await anchor.Read().FirstAsync().ToTask()).SourceOf(Package).Should().Be(PlatformSource);

        failing = true;
        var degraded = await anchor.Read().FirstAsync().ToTask();

        degraded.State.Should().Be(AnchorState.Stale);
        degraded.IsComplete.Should().BeFalse("an ABSENCE from a partial read proves nothing");
        degraded.SourceOf(Package).Should().Be(PlatformSource,
            "what was observed before is still what was observed — the registry going down does not "
            + "un-publish a package");
    }

    /// <summary>
    /// 🚨 The freshness window reuses a LISTING; it is not an entitlement term. Entitlements are
    /// eternal, and the window's expiry triggers a read, never a refusal.
    /// </summary>
    [Fact]
    public async Task TheFreshnessWindowReusesAListingAndExpiresIntoAReadNotARefusal()
    {
        var reads = 0;
        var now = DateTimeOffset.UnixEpoch;
        var anchor = new PackageOriginAnchor(
            () =>
            {
                reads++;
                return [new ConfiguredPackageSource(
                    new StubSource([Manifest(Package, "1.4.0")], null), "HEAD", PlatformSource)];
            },
            TimeSpan.FromSeconds(60), () => now);

        await anchor.Read().FirstAsync().ToTask();
        await anchor.Read().FirstAsync().ToTask();
        reads.Should().Be(1, "an authoritative listing inside the window is reused");

        now = now.AddMinutes(5);
        var afterExpiry = await anchor.Read().FirstAsync().ToTask();
        reads.Should().Be(2, "the window's expiry asks the sources again");
        afterExpiry.IsComplete.Should().BeTrue(
            "…and the answer is an authoritative snapshot, never a refusal — a cache TTL is not an "
            + "entitlement expiry");
        afterExpiry.SourceOf(Package).Should().Be(PlatformSource);
    }

    // ── stubs ─────────────────────────────────────────────────────────────────────────────────

    private static PackageManifest Manifest(string id, string released) =>
        new() { Id = id, Name = id, ReleasedVersion = released, TargetPartition = id };

    private static PackageOriginAnchor Anchor(ConfiguredPackageSource source) =>
        new(() => [source], TimeSpan.Zero, () => DateTimeOffset.UnixEpoch);

    private static ConfiguredPackageSource Listing(string name, params PackageManifest[] packages) =>
        new(new StubSource(packages, null), "HEAD", name);

    private static ConfiguredPackageSource Failing(string name, string message) =>
        new(new StubSource(null, new InvalidOperationException(message)), "HEAD", name);

    /// <summary>A package source that lists exactly what it is told to, or refuses to list at all.</summary>
    private sealed class StubSource(IReadOnlyList<PackageManifest>? packages, Exception? failure)
        : IPackageSource
    {
        public IObservable<IReadOnlyList<PackageManifest>> ListPackages(string gitRef) =>
            failure is null
                ? Observable.Return(packages ?? [])
                : Observable.Throw<IReadOnlyList<PackageManifest>>(failure);

        public IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(
            PackageManifest package, string gitRef) =>
            Observable.Return<IReadOnlyList<PackageFile>>([]);
    }
}
