#pragma warning disable CS1591

using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// The shared release predicate (#1754 deployment gate, #1755 build gate). Pure, so it is pinned
/// here without a mesh — which is the point of it being pure: an in-process service, an HTTP
/// endpoint and a CI step all reach these exact verdicts.
///
/// <para>The assertions that matter most are the NEGATIVE ones: that "cannot determine" HOLDS and
/// is reported as its own kind, and that a hold always carries a reason. A gate whose refusal is
/// indistinguishable from its pass is the trapdoor both issues exist to close.</para>
/// </summary>
public class ReleaseAvailabilityTest
{
    private static readonly ReleaseTarget Target = new("3.0.0-rc4.ci.4049", "sabc123");

    private static ReleaseArtifacts Sealed(params string[] bundles) => ReleaseArtifacts.Of(bundles);

    [Fact]
    public void EveryPackageSealedForTheTargetIdentity_IsUpdatable()
    {
        var verdict = ReleaseAvailability.IsUpdatable(
            Target,
            [new RequiredPackage("Doc", "Doc"), new RequiredPackage("Store", "Store")],
            Sealed("Doc.zip", "Store.zip"));

        Assert.True(verdict.IsUpdatable);
        Assert.Null(verdict.HoldReason);
        Assert.All(verdict.Packages, p => Assert.Equal(PackageAvailabilityKind.Available, p.Kind));
    }

    [Fact]
    public void APackageWithNoSealedBakeForTheTarget_HoldsTheRoll_AndIsNamed()
    {
        var verdict = ReleaseAvailability.IsUpdatable(
            Target,
            [new RequiredPackage("Doc", "Doc"), new RequiredPackage("Store", "Store")],
            Sealed("Doc.zip"));

        Assert.False(verdict.IsUpdatable);
        var blocker = Assert.Single(verdict.Blockers);
        Assert.Equal("Store", blocker.Package);
        Assert.Equal(PackageAvailabilityKind.ContentBakeMissing, blocker.Kind);
        // The refusal must say WHICH package blocks it — an unnamed hold is unactionable.
        Assert.Contains("Store", verdict.HoldReason);
        // A gate that reports only failures cannot show that it looked at anything.
        Assert.Equal(2, verdict.Packages.Length);
    }

    [Fact]
    public void TheSentinelListsFileNames_ButPackagesAreNamedById_AndTheyStillMatch()
    {
        // The publisher writes `Store.zip` into `_complete`; the install record says `Store`.
        // One place converts, so no caller has to remember which side it is holding.
        Assert.True(ReleaseAvailability
            .IsUpdatable(Target, [new RequiredPackage("Store", "Store")], Sealed("Store.zip"))
            .IsUpdatable);
    }

    [Fact]
    public void AModuleFloorTheTargetDoesNotSatisfy_IsAnIncompatibility_NotAMissingBake()
    {
        var verdict = ReleaseAvailability.IsUpdatable(
            new ReleaseTarget("3.0.0", "sabc123"),
            [new RequiredPackage("Social", "Social", MinMeshVersion: "4.0.0", HasContent: false)],
            Sealed());

        Assert.False(verdict.IsUpdatable);
        // A declared floor the target cannot meet is a definite incompatibility; reporting it as
        // an absent bake would send the operator to re-bake something that is fine.
        Assert.Equal(
            PackageAvailabilityKind.ModuleFloorExceedsTarget,
            Assert.Single(verdict.Blockers).Kind);
    }

    [Fact]
    public void AModuleWhoseFloorTheTargetSatisfies_Passes_WithoutRequiringABake()
    {
        // The module lane's gate is the semver FLOOR, never MVID equality (#1664) — a module built
        // against another platform build lands fine, so demanding a bake under the target identity
        // would forbid every ex-post Store install.
        Assert.True(ReleaseAvailability.IsUpdatable(
                new ReleaseTarget("3.0.0-rc4.ci.4049", "sabc123"),
                [new RequiredPackage("Social", "Social", MinMeshVersion: "2.0.0", HasContent: false)],
                Sealed())
            .IsUpdatable);
    }

    [Fact]
    public void APrereleaseTargetIsBELOWTheMatchingStableFloor_WhichIsWhyTheCallerPassesOnlyLiveFloors()
    {
        // 🚨 The trap this pins: SemVer puts `3.0.0-rc4.ci.4049` BELOW `3.0.0`, so a module
        // declaring minMeshVersion 3.0.0 is below floor on every -rc platform — including the one
        // prod runs. Judged absolutely, that module would hold its environment on EVERY release
        // forever, which is the silent-freeze outage the gate exists to avoid rather than cause.
        //
        // The predicate stays honest (the floor genuinely is not met); the CALLER is what makes
        // the gate a regression check, by passing a floor only when the RUNNING platform satisfies
        // it today — see ReleaseAvailabilityService.RequiredPackages. Since self-update only ever
        // rolls FORWARD, a floor met today is met by the target too, so this can fire only on a
        // rollback — which is exactly when it should.
        var absolute = ReleaseAvailability.IsUpdatable(
            new ReleaseTarget("3.0.0-rc4.ci.4049", "sabc123"),
            [new RequiredPackage("Social", "Social", MinMeshVersion: "3.0.0", HasContent: false)],
            Sealed());
        Assert.False(absolute.IsUpdatable);

        // The same package, with the caller having dropped a floor the running platform does not
        // meet either: no regression, no hold.
        Assert.True(ReleaseAvailability.IsUpdatable(
                new ReleaseTarget("3.0.0-rc4.ci.4049", "sabc123"),
                [new RequiredPackage("Social", "Social", MinMeshVersion: null, HasContent: false)],
                Sealed())
            .IsUpdatable);
    }

    [Fact]
    public void APackageThatShipsNoContent_IsNotHeldForAMissingBake()
    {
        // A package with no compilable NodeTypes produces no bundle EVER. Requiring one would hold
        // its environment forever — an environment silently frozen for weeks is its own outage,
        // and worse than the bug this gate closes.
        Assert.True(ReleaseAvailability
            .IsUpdatable(Target, [new RequiredPackage("Tools", "Tools", HasContent: false)], Sealed())
            .IsUpdatable);
    }

    [Fact]
    public void AnUnresolvableFrameworkIdentity_Holds_AsIndeterminate_NotAsIncompatible()
    {
        var verdict = ReleaseAvailability.IsUpdatable(
            new ReleaseTarget("3.0.0-rc4.ci.4049", FrameworkIdentity: null),
            [new RequiredPackage("Doc", "Doc")],
            Sealed("Doc.zip"));

        // Cannot determine is NOT clear to proceed.
        Assert.False(verdict.IsUpdatable);
        Assert.True(verdict.IsIndeterminate);
        // An availability failure must never be dressed up as a compatibility verdict.
        Assert.All(verdict.Packages,
            p => Assert.Equal(PackageAvailabilityKind.Indeterminate, p.Kind));
        Assert.Contains("cannot determine", verdict.HoldReason);
    }

    [Fact]
    public void AnUnreadableCatalogue_Holds_AndSaysSoInThoseWords()
    {
        var verdict = ReleaseAvailability.IsUpdatable(
            Target,
            [new RequiredPackage("Doc", "Doc")],
            ReleaseArtifacts.Unreadable("the share timed out"));

        Assert.False(verdict.IsUpdatable);
        Assert.True(verdict.IsIndeterminate);
        // The operator must be told the catalogue was unreachable, not that a package is stale.
        Assert.Contains("the share timed out", verdict.HoldReason);
    }

    [Fact]
    public void AnEmptyPackageSet_IsUpdatable_ButAnEmptySetWithAnUnreadableCatalogueIsNot()
    {
        Assert.True(ReleaseAvailability.IsUpdatable(Target, [], Sealed()).IsUpdatable);
        // An unreadable catalogue is a hold even when we do not yet know what it would have said —
        // that is what fail-safe means.
        Assert.False(ReleaseAvailability
            .IsUpdatable(Target, [], ReleaseArtifacts.Unreadable("boom")).IsUpdatable);
    }

    [Fact]
    public void ARefusalAlwaysCarriesAReason()
    {
        UpdatabilityVerdict[] refusals =
        [
            ReleaseAvailability.IsUpdatable(Target, [new RequiredPackage("A", "A")], Sealed()),
            ReleaseAvailability.IsUpdatable(
                new ReleaseTarget(null, null), [new RequiredPackage("A", "A")], Sealed()),
            ReleaseAvailability.IsUpdatable(
                Target, [new RequiredPackage("A", "A")], ReleaseArtifacts.Unreadable("x")),
        ];

        Assert.All(refusals, v =>
        {
            Assert.False(v.IsUpdatable);
            Assert.False(string.IsNullOrEmpty(v.HoldReason));
        });
    }

    [Fact]
    public void NotEnforced_IsUpdatable_ButCarriesItsReason_SoItCanNeverReadAsAPass()
    {
        var verdict = UpdatabilityVerdict.NotEnforced("no bundle root configured");

        Assert.True(verdict.IsUpdatable);
        // "Nothing is gating this environment" must be visible, never inferred from a green tick.
        Assert.False(string.IsNullOrEmpty(verdict.NotEnforcedReason));
        Assert.False(verdict.IsIndeterminate);
    }

    /// <summary>
    /// 🚨 The gate could not RUN — it is not wired in at all — is a HOLD, and is deliberately not
    /// <see cref="UpdatabilityVerdict.NotEnforced"/>. That answer is the ONE stated applicability
    /// exemption (a deployment consuming no CI bakes); reusing it for a wiring failure is how a
    /// gate that cannot run comes to look like a gate that passed.
    /// </summary>
    [Fact]
    public void Unavailable_IsAHold_AndReadsAsAnAvailabilityFailure_NotAnIncompatibility()
    {
        var verdict = UpdatabilityVerdict.Unavailable("no gate is registered on this host");

        Assert.False(verdict.IsUpdatable);
        Assert.False(string.IsNullOrEmpty(verdict.HoldReason));
        // Indeterminate, so every surface that already separates "I could not look" from "I looked
        // and it is incompatible" keeps doing so here without changing.
        Assert.True(verdict.IsIndeterminate);
        // NOT an applicability exemption: NotEnforcedReason is what makes a verdict updatable.
        Assert.Null(verdict.NotEnforcedReason);
        Assert.Single(verdict.Blockers);
    }
}
