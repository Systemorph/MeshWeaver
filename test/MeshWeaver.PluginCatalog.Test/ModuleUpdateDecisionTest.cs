#pragma warning disable CS1591

using System;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// The ONE module auto-update decision (#1664 Slice C), pinned pure — no registry, no filesystem,
/// no mesh. Production calls it with <c>PrebuiltAssemblySeeder.DeclineReason</c> as the framework
/// gate and the deployment's <see cref="IModuleUpdatePolicy"/> verdict as the policy input; here
/// both are stub functions so each rule is pinned in isolation.
/// </summary>
public class ModuleUpdateDecisionTest
{
    private const string LiveMvid = "aaaa0000aaaa0000aaaa0000aaaa0000";
    private const string ForeignMvid = "ffff9999ffff9999ffff9999ffff9999";

    /// <summary>The production gate's shape: null for the live identity, a reason otherwise.</summary>
    private static string? Gate(string? mvid) =>
        string.Equals(mvid, LiveMvid, StringComparison.Ordinal)
            ? null
            : $"built for framework {mvid ?? "(none)"}, live is {LiveMvid}";

    private static ModuleActivationEntry Landed(
        string? version, string mvid = LiveMvid, bool enabled = true) => new()
    {
        Name = "MeshWeaver.Social",
        FrameworkMvid = mvid,
        Version = version,
        Enabled = enabled,
    };

    [Fact]
    public void ANewerBundleForTheRunningFramework_Lands()
    {
        var verdict = ModuleUpdateDecision.Decide(
            "1.3.0", LiveMvid, Gate, Landed("1.2.0"), policyDecline: null);

        Assert.Equal(ModuleUpdateAction.Land, verdict.Action);
    }

    [Fact]
    public void TheSameLandedVersion_SkipsWithoutAnyDownloadDecision()
    {
        var verdict = ModuleUpdateDecision.Decide(
            "1.2.0", LiveMvid, Gate, Landed("1.2.0"), policyDecline: null);

        Assert.Equal(ModuleUpdateAction.SkipUpToDate, verdict.Action);
    }

    [Fact]
    public void ABundleForAForeignFrameworkMvid_IsSkipped_ItBecomesRelevantAfterTheImageRoll()
    {
        // 🚨 The reconcile runs on every boot; a registry that already rolled to a newer image must
        // not error the pass, and must not land bytes this process cannot load — the skip is
        // silent-with-log, and the SAME reconcile lands the bundle after this side's image roll.
        var verdict = ModuleUpdateDecision.Decide(
            "1.3.0", ForeignMvid, Gate, Landed("1.2.0"), policyDecline: null);

        Assert.Equal(ModuleUpdateAction.SkipForeignFramework, verdict.Action);
        Assert.Contains(ForeignMvid, verdict.Reason!);
    }

    [Fact]
    public void ANeverLandedModule_Lands()
    {
        // Covers the heal path too: a package installed before the module lane existed has content
        // but no landed module — the reconcile is what brings the binary half in.
        var verdict = ModuleUpdateDecision.Decide(
            "1.2.0", LiveMvid, Gate, landed: null, policyDecline: null);

        Assert.Equal(ModuleUpdateAction.Land, verdict.Action);
    }

    [Fact]
    public void ALandingWhoseRecordedMvidWentStale_ReLands_SameVersionIncluded()
    {
        // After an image roll the landed bytes are ABI-stale and boot skips them — the version
        // string being "equal" is irrelevant, because equality only counts FOR THE RUNNING
        // framework. This re-land is the module lane's whole answer to the roll.
        var verdict = ModuleUpdateDecision.Decide(
            "1.2.0", LiveMvid, Gate, Landed("1.2.0", mvid: ForeignMvid), policyDecline: null);

        Assert.Equal(ModuleUpdateAction.Land, verdict.Action);
    }

    [Fact]
    public void AnOlderBundleThanWhatIsLanded_NeverRollsBackUnattended()
    {
        var verdict = ModuleUpdateDecision.Decide(
            "1.1.0", LiveMvid, Gate, Landed("1.2.0"), policyDecline: null);

        Assert.Equal(ModuleUpdateAction.SkipOlder, verdict.Action);
    }

    [Fact]
    public void VersionOrderingIsSemVer_NotStringOrder()
    {
        // "1.10.0" > "1.9.0" numerically but < as text — string ordering would read the newer
        // registry version as a rollback and silently never update (the exact defect class
        // NuGetVersionComparer exists for).
        var verdict = ModuleUpdateDecision.Decide(
            "1.10.0", LiveMvid, Gate, Landed("1.9.0"), policyDecline: null);

        Assert.Equal(ModuleUpdateAction.Land, verdict.Action);
    }

    [Fact]
    public void ThePolicyOptOut_SkipsTheLanding_AndNamesThePolicy()
    {
        // The gate is the deployment's EXISTING update policy (Admin/UpdatePolicy — Stable/None);
        // there is no module-specific knob. The default — no policy registered — is null here,
        // which every other test passes: auto-update is the DEFAULT, opting out is the choice.
        var verdict = ModuleUpdateDecision.Decide(
            "1.3.0", LiveMvid, Gate, Landed("1.2.0"),
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
            "1.2.0", LiveMvid, Gate, Landed("1.2.0"),
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
            "1.2.0", LiveMvid, Gate, landed: null,
            policyDecline: "the deployment's update policy is Stable");

        Assert.Equal(ModuleUpdateAction.Land, verdict.Action);
    }

    [Fact]
    public void ThePolicyDoesNotBlockTheImageRollHeal()
    {
        // After a roll the OPERATOR chose, the old bytes stopped loading — refusing the re-land
        // would leave the module dead until someone notices. Keeping what was installed working is
        // not an update, so the heal is policy-exempt even at the same version.
        var verdict = ModuleUpdateDecision.Decide(
            "1.2.0", LiveMvid, Gate, Landed("1.2.0", mvid: ForeignMvid),
            policyDecline: "the deployment's update policy is Stable");

        Assert.Equal(ModuleUpdateAction.Land, verdict.Action);
    }

    [Fact]
    public void AnUninstalledModule_IsNeverReinstalledByTheReconcile()
    {
        var verdict = ModuleUpdateDecision.Decide(
            "1.3.0", LiveMvid, Gate, Landed("1.2.0", enabled: false), policyDecline: null);

        Assert.Equal(ModuleUpdateAction.SkipUninstalled, verdict.Action);
    }

    [Fact]
    public void ARegistryListingNoBundle_SkipsQuietly()
    {
        var verdict = ModuleUpdateDecision.Decide(
            bundleVersion: null, LiveMvid, Gate, Landed("1.2.0"), policyDecline: null);

        Assert.Equal(ModuleUpdateAction.SkipNoBundle, verdict.Action);
    }

    [Fact]
    public void ALandedEntryWithoutARecordedVersion_ReLandsOnce()
    {
        // Pre-versioning sidecar entries read as "unknown": not equal to the served version, not
        // comparable as older — so they land once and the version is recorded from then on.
        var verdict = ModuleUpdateDecision.Decide(
            "1.2.0", LiveMvid, Gate, Landed(version: null), policyDecline: null);

        Assert.Equal(ModuleUpdateAction.Land, verdict.Action);
    }
}
