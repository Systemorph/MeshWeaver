#pragma warning disable CS1591

using System;
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

    /// <summary>
    /// 🚨 The presence probe (#2417). Production binds this to
    /// <c>ModuleActivationBoot.LandedModuleDllExists</c>; here it is a constant, so every case
    /// below has to STATE whether the bytes are on disk. That statement is the point: the
    /// "already landed" rule was decided from a version string alone, and a version string is a
    /// claim about the disk rather than the disk.
    /// </summary>
    private static bool BytesPresent(ModuleActivationEntry _) => true;

    /// <summary>The state #2417 is about: the sidecar records the module, the assembly is gone.</summary>
    private static bool BytesGone(ModuleActivationEntry _) => false;

    [Fact]
    public void ANewerBundleWhoseFloorIsSatisfied_Lands()
    {
        var verdict = ModuleUpdateDecision.Decide(
            "1.3.0", "3.0.0", Gate, Landed("1.2.0"), policyDecline: null, BytesPresent);

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
            "1.3.0", "1.0.0", Gate, landed: null, policyDecline: null, BytesPresent);

        Assert.Equal(ModuleUpdateAction.Land, verdict.Action);
    }

    [Fact]
    public void ABundleWithNoDeclaredFloor_Lands_AbsenceIsNoConstraint()
    {
        var verdict = ModuleUpdateDecision.Decide(
            "1.3.0", bundleMinMeshVersion: null, Gate, Landed("1.2.0"), policyDecline: null, BytesPresent);

        Assert.Equal(ModuleUpdateAction.Land, verdict.Action);
    }

    [Fact]
    public void TheSameLandedVersion_SkipsWithoutAnyDownloadDecision()
    {
        var verdict = ModuleUpdateDecision.Decide(
            "1.2.0", "3.0.0", Gate, Landed("1.2.0"), policyDecline: null, BytesPresent);

        Assert.Equal(ModuleUpdateAction.SkipUpToDate, verdict.Action);
    }

    [Fact]
    public void VersionEqualityIsSemVer_TwoPartAndThreePartFormsAreTheSameVersion()
    {
        // Manifests legitimately carry two-part versions ("1.2"; NuGet widens to "1.2.0"), so
        // equality must go through the SAME comparer as the downgrade check — string equality
        // would read this as an update and re-land the module on every reconcile, forever.
        Assert.Equal(ModuleUpdateAction.SkipUpToDate,
            ModuleUpdateDecision.Decide("1.2", "3.0.0", Gate, Landed("1.2.0"), null, BytesPresent).Action);
        Assert.Equal(ModuleUpdateAction.SkipUpToDate,
            ModuleUpdateDecision.Decide("1.2.0", "3.0.0", Gate, Landed("1.2"), null, BytesPresent).Action);
    }

    [Fact]
    public void ABundleWhoseFloorExceedsTheRunningPlatform_IsSkipped_ItBecomesInstallableAfterTheUpdate()
    {
        // The reconcile runs on every boot; a module that already requires a newer platform must
        // not error the pass and must not land bytes whose API surface does not exist here — the
        // skip is silent-with-log, and the SAME reconcile lands the bundle once the platform has
        // moved past the floor.
        var verdict = ModuleUpdateDecision.Decide(
            "1.3.0", "9.0.0", Gate, Landed("1.2.0"), policyDecline: null, BytesPresent);

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
            "1.2.0", "3.0.0", Gate, landed: null, policyDecline: null, BytesPresent);

        Assert.Equal(ModuleUpdateAction.Land, verdict.Action);
    }

    [Fact]
    public void AnOlderBundleThanWhatIsLanded_NeverRollsBackUnattended()
    {
        var verdict = ModuleUpdateDecision.Decide(
            "1.1.0", "3.0.0", Gate, Landed("1.2.0"), policyDecline: null, BytesPresent);

        Assert.Equal(ModuleUpdateAction.SkipOlder, verdict.Action);
    }

    [Fact]
    public void VersionOrderingIsSemVer_NotStringOrder()
    {
        // "1.10.0" > "1.9.0" numerically but < as text — string ordering would read the newer
        // registry version as a rollback and silently never update (the exact defect class
        // NuGetVersionComparer exists for).
        var verdict = ModuleUpdateDecision.Decide(
            "1.10.0", "3.0.0", Gate, Landed("1.9.0"), policyDecline: null, BytesPresent);

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
            policyDecline: "the deployment's update policy is Stable", BytesPresent);

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
            policyDecline: "the deployment's update policy is Stable", BytesPresent);

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
            policyDecline: "the deployment's update policy is Stable", BytesPresent);

        Assert.Equal(ModuleUpdateAction.Land, verdict.Action);
    }

    [Fact]
    public void AnUninstalledModule_IsNeverReinstalledByTheReconcile()
    {
        var verdict = ModuleUpdateDecision.Decide(
            "1.3.0", "3.0.0", Gate, Landed("1.2.0", enabled: false), policyDecline: null, BytesPresent);

        Assert.Equal(ModuleUpdateAction.SkipUninstalled, verdict.Action);
    }

    [Fact]
    public void ARegistryListingNoBundle_SkipsQuietly()
    {
        var verdict = ModuleUpdateDecision.Decide(
            bundleVersion: null, "3.0.0", Gate, Landed("1.2.0"), policyDecline: null, BytesPresent);

        Assert.Equal(ModuleUpdateAction.SkipNoBundle, verdict.Action);
    }

    /// <summary>
    /// 🚨 <b>#2417 — the self-sealing state, and the whole reason this parameter exists.</b>
    ///
    /// <para>A record saying "module X at version V" with no assembly behind it answered
    /// <c>SkipUpToDate</c> — forever, on every deployment, on every reconcile. Nothing else in the
    /// system would ever look again: the version comparison is the only gate the healing lane has,
    /// and it was satisfied. So every way a landed binary can go missing — a recreated
    /// <c>Modules:Root</c> volume, a half-completed landing, a generation directory collected out
    /// from under its entry — was PERMANENT and produced a cheerful "up to date" line.</para>
    ///
    /// <para>Fails on pre-fix code, which has no way to be told the bytes are gone and answers
    /// <c>SkipUpToDate</c>.</para>
    /// </summary>
    [Fact]
    public void ARecordedLandingWhoseAssemblyIsGone_Lands_NeverSkipsAsUpToDate()
    {
        var verdict = ModuleUpdateDecision.Decide(
            "1.3.0", "3.0.0", Gate, Landed("1.3.0"), policyDecline: null, BytesGone);

        Assert.Equal(ModuleUpdateAction.Land, verdict.Action);
        Assert.Contains("ABSENT", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The control, and it is not a formality: if a present assembly ever started re-landing, the
    /// reconcile would re-download every module on every boot forever. "Up to date" must still be
    /// reachable — the fix narrows the skip, it does not remove it.
    /// </summary>
    [Fact]
    public void ARecordedLandingWhoseAssemblyIsThere_StillSkipsAsUpToDate()
    {
        var verdict = ModuleUpdateDecision.Decide(
            "1.3.0", "3.0.0", Gate, Landed("1.3.0"), policyDecline: null, BytesPresent);

        Assert.Equal(ModuleUpdateAction.SkipUpToDate, verdict.Action);
    }

    /// <summary>
    /// 🚨 A DELIBERATE UNINSTALL IS NOT DAMAGE. A disabled entry has no assembly on disk by
    /// construction, so the presence probe would say "gone" for every uninstalled module — and a
    /// repair that re-installs what an operator removed is worse than the defect it fixes. The
    /// uninstall check sits ABOVE the presence question for exactly this reason, and this test is
    /// what stops a later reordering from turning the repair into a resurrection.
    /// </summary>
    [Fact]
    public void AnUninstalledModuleWithNoAssembly_StaysUninstalled_TheProbeDoesNotResurrectIt()
    {
        var verdict = ModuleUpdateDecision.Decide(
            "1.3.0", "3.0.0", Gate, Landed("1.3.0", enabled: false), policyDecline: null, BytesGone);

        Assert.Equal(ModuleUpdateAction.SkipUninstalled, verdict.Action);
    }

    /// <summary>
    /// A missing assembly is a REPAIR, not an upgrade, so the unattended-update policy must not
    /// hold it — the same reasoning that already exempts a first landing: it completes an install
    /// the operator's own surfaces sanctioned, and gating it ships a package whose binary half
    /// never arrives. (The policy check runs after the version comparison, so a re-land decided
    /// here never reaches it.)
    /// </summary>
    [Fact]
    public void ARepairOfAMissingAssembly_IsNotHeldByTheUnattendedPolicy()
    {
        var verdict = ModuleUpdateDecision.Decide(
            "1.3.0", "3.0.0", Gate, Landed("1.3.0"),
            policyDecline: "this deployment declines unattended landings", BytesGone);

        Assert.Equal(ModuleUpdateAction.Land, verdict.Action);
    }

    /// <summary>
    /// The probe must never be consulted for an entry that does not exist — there is nothing to
    /// stat, and a caller binding it to a filesystem read would be handed a null. A never-landed
    /// module lands on its own rule.
    /// </summary>
    [Fact]
    public void ANeverLandedModule_DecidesWithoutAskingTheProbe()
    {
        var asked = false;
        var verdict = ModuleUpdateDecision.Decide(
            "1.3.0", "3.0.0", Gate, landed: null, policyDecline: null,
            _ => { asked = true; return true; });

        Assert.Equal(ModuleUpdateAction.Land, verdict.Action);
        Assert.False(asked, "there is no entry to probe, so nothing may be stat'd");
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
