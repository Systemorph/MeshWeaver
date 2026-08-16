#pragma warning disable CS1591

using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// The ONE module auto-update decision (#1664 Slice C), pinned pure — no registry, no filesystem,
/// no mesh. Production calls it with <see cref="ModulePlatformFloor.DeclineReason(string?)"/> as
/// the platform gate and the deployment's <see cref="IModuleUpdatePolicy"/> verdict as the policy
/// input; here the gate is the same pure function bound to a fixed running version, so each rule
/// is pinned in isolation.
///
/// <para>🚨 The platform gate is a <c>minMeshVersion</c> FLOOR, deliberately NOT MVID equality:
/// modules are plain assemblies binding by simple name, and their contract is API compatibility.
/// MVID equality is bake semantics (the NodeType lane's gate) — applied here it would force
/// rebundling every module on every CI build and forbid the ex-post Store install across platform
/// versions this lane exists for.</para>
/// </summary>
public class ModuleUpdateDecisionTest
{
    private const string Running = "3.2.0";

    /// <summary>The production gate's shape, bound to a fixed running platform version.</summary>
    private static string? Gate(string? floor) =>
        ModulePlatformFloor.DeclineReason(floor, Running);

    private static ModuleActivationEntry Landed(string? version, bool enabled = true) => new()
    {
        Name = "MeshWeaver.Social",
        FrameworkMvid = "aaaa0000aaaa0000aaaa0000aaaa0000",
        Version = version,
        Enabled = enabled,
    };

    [Fact]
    public void ANewerBundleWhoseFloorIsSatisfied_Lands()
    {
        var verdict = ModuleUpdateDecision.Decide(
            "1.3.0", "3.0.0", Gate, Landed("1.2.0"), policyDecline: null);

        Assert.Equal(ModuleUpdateAction.Land, verdict.Action);
    }

    [Fact]
    public void ABundleBuiltAgainstAnOlderPlatform_Lands_WhenItsFloorIsSatisfied()
    {
        // 🚨 THE point of the floor gate: a bundle produced against an older platform build (or
        // years of CI builds ago — its recorded MVID is irrelevant and not even an input here)
        // installs ex post as long as this platform satisfies its declared floor. Under MVID
        // equality this exact case — "install GRPC ex post through the Store" — was impossible.
        var verdict = ModuleUpdateDecision.Decide(
            "1.3.0", "1.0.0", Gate, landed: null, policyDecline: null);

        Assert.Equal(ModuleUpdateAction.Land, verdict.Action);
    }

    [Fact]
    public void ABundleWithNoDeclaredFloor_Lands_AbsenceIsNoConstraint()
    {
        var verdict = ModuleUpdateDecision.Decide(
            "1.3.0", bundleMinMeshVersion: null, Gate, Landed("1.2.0"), policyDecline: null);

        Assert.Equal(ModuleUpdateAction.Land, verdict.Action);
    }

    [Fact]
    public void TheSameLandedVersion_SkipsWithoutAnyDownloadDecision()
    {
        var verdict = ModuleUpdateDecision.Decide(
            "1.2.0", "3.0.0", Gate, Landed("1.2.0"), policyDecline: null);

        Assert.Equal(ModuleUpdateAction.SkipUpToDate, verdict.Action);
    }

    [Fact]
    public void ABundleWhoseFloorExceedsTheRunningPlatform_IsSkipped_ItBecomesInstallableAfterTheUpdate()
    {
        // The reconcile runs on every boot; a module that already requires a newer platform must
        // not error the pass and must not land bytes whose API surface does not exist here — the
        // skip is silent-with-log, and the SAME reconcile lands the bundle once the platform has
        // moved past the floor.
        var verdict = ModuleUpdateDecision.Decide(
            "1.3.0", "9.0.0", Gate, Landed("1.2.0"), policyDecline: null);

        Assert.Equal(ModuleUpdateAction.SkipPlatformBelowFloor, verdict.Action);
        Assert.Contains("9.0.0", verdict.Reason!);
        Assert.Contains(Running, verdict.Reason!);
    }

    [Fact]
    public void ANeverLandedModule_Lands()
    {
        // Covers the heal path too: a package installed before the module lane existed has content
        // but no landed module — the reconcile is what brings the binary half in.
        var verdict = ModuleUpdateDecision.Decide(
            "1.2.0", "3.0.0", Gate, landed: null, policyDecline: null);

        Assert.Equal(ModuleUpdateAction.Land, verdict.Action);
    }

    [Fact]
    public void AnOlderBundleThanWhatIsLanded_NeverRollsBackUnattended()
    {
        var verdict = ModuleUpdateDecision.Decide(
            "1.1.0", "3.0.0", Gate, Landed("1.2.0"), policyDecline: null);

        Assert.Equal(ModuleUpdateAction.SkipOlder, verdict.Action);
    }

    [Fact]
    public void VersionOrderingIsSemVer_NotStringOrder()
    {
        // "1.10.0" > "1.9.0" numerically but < as text — string ordering would read the newer
        // registry version as a rollback and silently never update (the exact defect class
        // NuGetVersionComparer exists for).
        var verdict = ModuleUpdateDecision.Decide(
            "1.10.0", "3.0.0", Gate, Landed("1.9.0"), policyDecline: null);

        Assert.Equal(ModuleUpdateAction.Land, verdict.Action);
    }

    [Fact]
    public void ThePolicyOptOut_SkipsTheUpgrade_AndNamesThePolicy()
    {
        // The gate is the deployment's EXISTING update policy (Admin/UpdatePolicy — Stable/None);
        // there is no module-specific knob. The default — no policy registered — is null here,
        // which every other test passes: auto-update is the DEFAULT, opting out is the choice.
        var verdict = ModuleUpdateDecision.Decide(
            "1.3.0", "3.0.0", Gate, Landed("1.2.0"),
            policyDecline: "the deployment's update policy is Stable");

        Assert.Equal(ModuleUpdateAction.SkipPolicy, verdict.Action);
        Assert.Contains("Stable", verdict.Reason!);
    }

    [Fact]
    public void ThePolicyIsOnlyConsultedWhenSomethingWouldLand()
    {
        // An up-to-date module under Stable must read as up-to-date, not as a policy skip — the
        // policy is the LAST gate, so its skips always mean "an update was withheld".
        var verdict = ModuleUpdateDecision.Decide(
            "1.2.0", "3.0.0", Gate, Landed("1.2.0"),
            policyDecline: "the deployment's update policy is Stable");

        Assert.Equal(ModuleUpdateAction.SkipUpToDate, verdict.Action);
    }

    [Fact]
    public void ThePolicyDoesNotBlockAFirstLanding_ItCompletesAnInstall()
    {
        // Stable/None means "no unattended UPDATES", not "installed packages arrive half-delivered":
        // the install itself was sanctioned by the operator's own surfaces (a Provision click, the
        // default-install config), and the module is part of what they asked for.
        var verdict = ModuleUpdateDecision.Decide(
            "1.2.0", "3.0.0", Gate, landed: null,
            policyDecline: "the deployment's update policy is Stable");

        Assert.Equal(ModuleUpdateAction.Land, verdict.Action);
    }

    [Fact]
    public void AnUninstalledModule_IsNeverReinstalledByTheReconcile()
    {
        var verdict = ModuleUpdateDecision.Decide(
            "1.3.0", "3.0.0", Gate, Landed("1.2.0", enabled: false), policyDecline: null);

        Assert.Equal(ModuleUpdateAction.SkipUninstalled, verdict.Action);
    }

    [Fact]
    public void ARegistryListingNoBundle_SkipsQuietly()
    {
        var verdict = ModuleUpdateDecision.Decide(
            bundleVersion: null, "3.0.0", Gate, Landed("1.2.0"), policyDecline: null);

        Assert.Equal(ModuleUpdateAction.SkipNoBundle, verdict.Action);
    }
}

/// <summary>
/// The ONE platform gate of the module lane (<see cref="ModulePlatformFloor"/>), pinned pure —
/// the semantics every serve/fetch/land/boot call site inherits.
/// </summary>
public class ModulePlatformFloorTest
{
    [Fact]
    public void AnAbsentFloor_IsNoConstraint()
    {
        Assert.Null(ModulePlatformFloor.DeclineReason(null, "3.2.0"));
        Assert.Null(ModulePlatformFloor.DeclineReason("", "3.2.0"));
        Assert.Null(ModulePlatformFloor.DeclineReason("  ", "3.2.0"));
    }

    [Fact]
    public void ASatisfiedFloor_Allows_EqualityIncluded()
    {
        Assert.Null(ModulePlatformFloor.DeclineReason("3.0.0", "3.2.0"));
        Assert.Null(ModulePlatformFloor.DeclineReason("3.2.0", "3.2.0"));
    }

    [Fact]
    public void AnExceededFloor_Declines_NamingBothVersions()
    {
        var reason = ModulePlatformFloor.DeclineReason("3.5.0", "3.2.0");

        Assert.NotNull(reason);
        Assert.Contains("3.5.0", reason);
        Assert.Contains("3.2.0", reason);
    }

    [Fact]
    public void PreReleaseOrderingIsSemVer()
    {
        // 3.0.0-rc3.ci.3758 > 3.0.0-rc3.ci.900 numerically (ci.900 sorts ABOVE as text — the
        // NuGetVersionComparer defect class), and a clean release outranks its pre-releases.
        Assert.Null(ModulePlatformFloor.DeclineReason("3.0.0-rc3.ci.900", "3.0.0-rc3.ci.3758"));
        Assert.NotNull(ModulePlatformFloor.DeclineReason("3.0.0-rc3.ci.3758", "3.0.0-rc3.ci.900"));
        Assert.Null(ModulePlatformFloor.DeclineReason("3.0.0-rc3", "3.0.0"));
    }

    [Fact]
    public void ADeclaredFloorWithAnUnknownRunningVersion_Declines_NeverLandsOnFaith()
    {
        Assert.NotNull(ModulePlatformFloor.DeclineReason("3.0.0", null));
    }

    [Fact]
    public void TheRunningVersionIsStamped_AndCarriesNoBuildMetadata()
    {
        // The production overload's anchor: MeshWeaver.Graph's informational version, +sha
        // stripped. A missing stamp would silently turn every declared floor into a refusal.
        var running = ModulePlatformFloor.RunningVersion;

        Assert.False(string.IsNullOrWhiteSpace(running));
        Assert.DoesNotContain("+", running);
    }
}
