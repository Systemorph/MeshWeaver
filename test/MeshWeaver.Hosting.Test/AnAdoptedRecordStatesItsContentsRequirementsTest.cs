using System;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>An install record states the requirements of the content it is at.</b> Measured on the plugin
/// registry during the 3 → 4 roll: the Governance record had been adopted from its sync source to
/// 4.0.3 (floor 4.0.13516) while it still listed the September release's <c>AI@^1.0.0</c>, because
/// <see cref="PackageUpdateReconciler.AdoptedFromSync"/> moved the version, the hash and the floor and
/// kept the requirements. The dependency-floor check judges a record of floor 4.x on line 4, so that
/// one stale line refused every line-4 module-set proposal against the landed AI 4.0.3 — the first
/// line-4 replica stayed on an empty module set and its readiness gates refused it. Pure.
/// </summary>
public class AnAdoptedRecordStatesItsContentsRequirementsTest
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 1, 0, 0, TimeSpan.Zero);

    private static PackageManifest SeptemberRecord(string moduleVersion) => new()
    {
        Id = "Governance",
        Source = "Plugins",
        Version = "0.2.10",
        ModuleVersion = moduleVersion,
        MinMeshVersion = "3.0.0-ci.7845",
        Requires = ["Store@^1.0.0", "AI@^1.0.0", "Signature@^1.2.0"],
        AuthorizedBy = "system-security",
    };

    private static PackageManifest Candidate() => new()
    {
        Id = "Governance",
        Source = "Plugins",
        Version = "4.0.3",
        ModuleVersion = "531fdb35d7d08c01",
        MinMeshVersion = "4.0.13516",
        Requires = ["Store@^4.0.0", "AI@^4.0.0", "Signature@^4.0.0", "Essentials@^4.0.0"],
    };

    /// <summary>The incident's write: adopting a candidate takes its requirements with its floor.</summary>
    [Fact]
    public void AdoptingACandidate_TakesItsRequirements()
    {
        var adopted = PackageUpdateReconciler.AdoptedFromSync(SeptemberRecord("old"), Candidate(), Now);

        Assert.Equal(Candidate().Requires, adopted.Requires);
        Assert.Equal("4.0.13516", adopted.MinMeshVersion);
        Assert.Equal("system-security", adopted.AuthorizedBy);
    }

    /// <summary>The record the old adoption left behind — at the candidate's content, with the old
    /// requirements — is recognised as stating other declarations…</summary>
    [Fact]
    public void ARecordAtTheCandidatesContent_WithOldRequirements_DoesNotDeclareAsTheCandidate()
    {
        var stale = SeptemberRecord(Candidate().ModuleVersion!) with { MinMeshVersion = "4.0.13516", Version = "4.0.3" };

        Assert.False(PackageUpdateReconciler.DeclaresAsTheCandidate(stale, Candidate()));
        Assert.True(PackageUpdateReconciler.DeclaresAsTheCandidate(
            PackageUpdateReconciler.WithCandidateDeclarations(stale, Candidate()), Candidate()));
    }

    /// <summary>…and corrected to the content's own requirements, keeping every install-time field.</summary>
    [Fact]
    public void HealingTheDeclarations_KeepsEverythingInstallTime()
    {
        var stale = SeptemberRecord(Candidate().ModuleVersion!) with { MinMeshVersion = "4.0.13516", Version = "4.0.3" };

        var healed = PackageUpdateReconciler.WithCandidateDeclarations(stale, Candidate());

        Assert.Equal(Candidate().Requires, healed.Requires);
        Assert.Equal(stale.ModuleVersion, healed.ModuleVersion);
        Assert.Equal(stale.AuthorizedBy, healed.AuthorizedBy);
        Assert.Equal(stale.InstalledAtUtc, healed.InstalledAtUtc);
    }

    /// <summary>A record already stating the candidate's declarations needs no write.</summary>
    [Fact]
    public void ARecordStatingTheCandidatesDeclarations_NeedsNoHeal()
        => Assert.True(PackageUpdateReconciler.DeclaresAsTheCandidate(Candidate(), Candidate()));
}
