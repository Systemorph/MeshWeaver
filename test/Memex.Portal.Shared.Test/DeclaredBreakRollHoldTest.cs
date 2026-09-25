using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>The ladder's roll rule</b> (policy <c>platform-backwards-compatibility</c>,
/// <c>Doc/Architecture/DeployingAcrossPlatformVersions</c>): a normal platform build rolls with NO
/// seal for the new build — the install keeps the plugin bytes it runs — and only behind a DECLARED
/// break (another compatibility key, or a ceiling the installed build declares) does a platform
/// roll wait for a replacement sealed for the target. Then the hold NAMES the plugin and both
/// versions.
///
/// <para>Each case is the pure gate (<see cref="ReleaseAvailability.IsUpdatable(ReleaseTarget, System.Collections.Generic.IEnumerable{RequiredPackage}, ReleaseArtifacts, ReleaseGatePolicy)"/>)
/// on one package — <c>Plugins/Store</c>, content-bearing — against one target. The negative
/// controls are the two HOLD cases: the rule that lets the ordinary roll through must still stop
/// a declared break, or the "open ceiling rolls" case would be passing on a gate that holds
/// nothing.</para>
/// </summary>
public class DeclaredBreakRollHoldTest
{
    private const string Key1 = "c003e001";
    private const string Key2 = "c003e002";
    private const string Running = "3.0.0-ci.9321";
    private const string Target = "3.0.0-ci.9400";

    private static RequiredPackage Store(string? installedKey = Key1, string? ceiling = null) =>
        new("Plugins/Store", "Store", HasContent: true)
        {
            InstalledKey = installedKey,
            InstalledCeiling = ceiling,
        };

    /// <summary>Nothing sealed for the target at all — the state right after a platform build.</summary>
    private static ReleaseArtifacts NothingSealed() => ReleaseArtifacts.Of([]);

    /// <summary>The package's bundle sealed for the target, with the given declared range.</summary>
    private static ReleaseArtifacts Replacement(string? floor, string? ceiling = null) =>
        ReleaseArtifacts.Of(["Store.zip"]) with
        {
            BundleRanges = ImmutableDictionary<string, BundlePlatformRange>.Empty
                .WithComparers(StringComparer.OrdinalIgnoreCase)
                .Add("Store", new BundlePlatformRange(floor, ceiling)),
        };

    private static UpdatabilityVerdict Gate(ReleaseTarget target, RequiredPackage package, ReleaseArtifacts artifacts) =>
        ReleaseAvailability.IsUpdatable(target, [package], artifacts, ReleaseGatePolicy.Default);

    // ─────────────────────────────── the ordinary path: no seal, ever

    /// <summary>
    /// 🚨 RATCHET: a platform roll inside one compatibility key, with an open ceiling, is updatable
    /// with NOTHING sealed for the new build. A missing bake is the boot-compile COST it always was
    /// (an advisory), never a seal requirement. Re-introducing "a platform roll waits for a plugin
    /// seal for the new build" reds exactly this.
    /// </summary>
    [Fact]
    public void AnOpenCeiling_SameKey_RollsWithoutAnySeal()
    {
        var verdict = Gate(new ReleaseTarget(Target, Key1), Store(), NothingSealed());

        verdict.IsUpdatable.Should().BeTrue(
            "a normal platform build changes nothing for plugins — no seal is on a platform roll's path");
        verdict.Packages.Should().ContainSingle().Which.Kind.Should().Be(PackageAvailabilityKind.ContentBakeMissing,
            "the missing bake is still REPORTED as the boot-compile cost");
        verdict.BootCompiles.Should().Contain("Plugins/Store");
    }

    /// <summary>
    /// A target whose key carries the package sealed by an EARLIER build of the same key: the roll
    /// ADOPTS the older compatible set — said as such, never as "would recompile at boot".
    /// </summary>
    [Fact]
    public void ASetSealedByAnEarlierBuildOfTheKey_ReadsAsAdopted_NotRecompiled()
    {
        var verdict = Gate(new ReleaseTarget(Target, Key1), Store(), Replacement(floor: "3.0.0-ci.9321"));

        verdict.IsUpdatable.Should().BeTrue();
        verdict.BootCompiles.Should().BeEmpty("the key's publication is adopted — nothing recompiles");
        verdict.Advisories.Should().Contain(a => a.Contains("adopts the older compatible set")
                                                 && a.Contains("Plugins/Store @ 3.0.0-ci.9321"));
    }

    /// <summary>A legacy installed identity states no epoch, so it declares no range — the first
    /// roll onto the keyed platform is an ordinary roll, not a held one.</summary>
    [Fact]
    public void ALegacyInstalledIdentity_IsNeverHeldByTheRange()
    {
        var verdict = Gate(new ReleaseTarget(Target, Key1), Store(installedKey: "s47313cc0000000000000000000000000"), NothingSealed());

        verdict.IsUpdatable.Should().BeTrue();
        verdict.Packages.Should().NotContain(p => p.Kind == PackageAvailabilityKind.PlatformRangeExceeded);
    }

    // ─────────────────────────────── behind a declared break

    /// <summary>
    /// Another compatibility key (an epoch bump) with no replacement sealed for it: HELD, and the
    /// reason names the plugin, the installed key, the target version and the target key.
    /// </summary>
    [Fact]
    public void AnEpochBump_WithNoReplacement_IsHeld_AndNamesThePluginAndBothVersions()
    {
        var verdict = Gate(new ReleaseTarget(Target, Key2), Store(), NothingSealed());

        verdict.IsUpdatable.Should().BeFalse();
        var hold = verdict.Blockers.Should().ContainSingle().Which;
        hold.Kind.Should().Be(PackageAvailabilityKind.PlatformRangeExceeded);
        hold.Package.Should().Be("Plugins/Store");
        hold.Reason.Should().Contain(Key1).And.Contain(Target).And.Contain(Key2)
            .And.Contain("no replacement is sealed");
        verdict.HoldReason.Should().StartWith("Plugins/Store:");
    }

    /// <summary>A ceiling the installed build declares, below the target, on the SAME key: held.</summary>
    [Fact]
    public void ACeilingBelowTheTarget_WithNoReplacement_IsHeld()
    {
        var verdict = Gate(new ReleaseTarget(Target, Key1), Store(ceiling: "3.0.0-ci.9350"), NothingSealed());

        verdict.IsUpdatable.Should().BeFalse();
        verdict.Blockers.Should().ContainSingle().Which.Reason.Should()
            .Contain("3.0.0-ci.9350").And.Contain(Target);
    }

    /// <summary>…and a ceiling AT or above the target does not hold.</summary>
    [Fact]
    public void ACeilingAtTheTarget_DoesNotHold()
    {
        Gate(new ReleaseTarget(Target, Key1), Store(ceiling: Target), NothingSealed())
            .Packages.Should().NotContain(p => p.Kind == PackageAvailabilityKind.PlatformRangeExceeded);
    }

    /// <summary>
    /// A replacement sealed under the target's key whose floor is at or below the target: the roll
    /// proceeds — the break is covered.
    /// </summary>
    [Fact]
    public void AnEpochBump_WithASealedReplacementWhoseFloorIsBelowTheTarget_Rolls()
    {
        var verdict = Gate(new ReleaseTarget(Target, Key2), Store(), Replacement(floor: "3.0.0-ci.9390"));

        verdict.IsUpdatable.Should().BeTrue();
        verdict.Packages.Should().ContainSingle().Which.Kind.Should().Be(PackageAvailabilityKind.Available);
    }

    /// <summary>A replacement built by a platform NEWER than the target does not cover it (the
    /// forbidden rung, platform 1 + plugin 2): still held, naming why.</summary>
    [Fact]
    public void AReplacementWhoseFloorIsAboveTheTarget_StillHolds()
    {
        var verdict = Gate(new ReleaseTarget(Target, Key2), Store(), Replacement(floor: "3.0.0-ci.9410"));

        verdict.IsUpdatable.Should().BeFalse();
        verdict.Blockers.Should().ContainSingle().Which.Reason.Should()
            .Contain("3.0.0-ci.9410").And.Contain("does not admit the target");
    }

    // ─────────────────────────────── the walk: a held newest release is walked past

    /// <summary>
    /// The selection walks newest-first, so a declared break held on the newest candidate lands the
    /// environment on the newest release its plugins still cover — the last build of the old epoch —
    /// rather than staying put, and the declined candidate names the plugin.
    /// </summary>
    [Fact]
    public async Task TheWalk_StopsAtTheNewestCoveredRelease_AndNamesTheBlockingPlugin()
    {
        const string LastOfEpoch1 = "3.0.0-ci.9399";
        var inputs = new RollSelectionInputs(
            "memex",
            Running,
            [Target, LastOfEpoch1],
            PluginInventory.Of([Store()], "test"));

        var outcome = await RollSelection.Select(inputs, version => Observable.Return(version == Target
                ? Gate(new ReleaseTarget(Target, Key2), Store(), NothingSealed())
                : Gate(new ReleaseTarget(LastOfEpoch1, Key1), Store(), NothingSealed())))
            .Should().Within(TestTimeouts.Convergence).Emit();

        outcome.SelectedVersion.Should().Be(LastOfEpoch1);
        outcome.Declined.Should().ContainSingle().Which.Reason.Should().Contain("Plugins/Store").And.Contain(Key2);
    }
}
