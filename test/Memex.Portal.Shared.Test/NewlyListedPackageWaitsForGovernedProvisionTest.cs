using MeshWeaver.PluginCatalog;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Pins the GOVERNED-PROVISION HOLD of the boot default install: once an instance has been seeded,
/// a package newly listed in a source it seeds with a whole-source pattern (<c>Plugins/*</c>) is
/// AVAILABLE but not installed. Measured 2026-09-29: a package merged into MeshWeaver.Plugins
/// <c>main</c> was installed on two instances within 33 minutes because "never seeded" read as
/// "install it". It now lands through a governed <c>package.provision</c> or an explicit entry.
/// </summary>
public class NewlyListedPackageWaitsForGovernedProvisionTest
{
    [Fact]
    public void ANewPackageCoveredOnlyByAWildcard_IsHeld_OnASeededInstance() =>
        InstanceAutoRegistrationService.HoldsForGovernedProvision(
            wildcardOnly: true, reconciled: false, recordedInLedger: false, freshInstance: false).Should().BeTrue();

    /// <summary>A fresh deployment still seeds everything its patterns cover — that is what the seed is for.</summary>
    [Fact]
    public void AFreshInstance_StillSeedsItsWildcards() =>
        InstanceAutoRegistrationService.HoldsForGovernedProvision(
            wildcardOnly: true, reconciled: false, recordedInLedger: false, freshInstance: true).Should().BeFalse();

    /// <summary>An entry that names the package exactly is a reviewed decision (a deployment PR) — PartnerRe's explicit list.</summary>
    [Fact]
    public void AnExactlyNamedPackage_Installs() =>
        InstanceAutoRegistrationService.HoldsForGovernedProvision(
            wildcardOnly: false, reconciled: false, recordedInLedger: false, freshInstance: false).Should().BeFalse();

    /// <summary>The platform baseline and this environment's flags are reconciled lanes and re-assert as before.</summary>
    [Fact]
    public void AReconciledLane_Installs() =>
        InstanceAutoRegistrationService.HoldsForGovernedProvision(
            wildcardOnly: true, reconciled: true, recordedInLedger: false, freshInstance: false).Should().BeFalse();

    /// <summary>Nothing already seeded changes: the ledger filter owns that case and is untouched.</summary>
    [Fact]
    public void AnAlreadySeededPackage_IsNotTheHoldsBusiness() =>
        InstanceAutoRegistrationService.HoldsForGovernedProvision(
            wildcardOnly: true, reconciled: false, recordedInLedger: true, freshInstance: false).Should().BeFalse();
}
