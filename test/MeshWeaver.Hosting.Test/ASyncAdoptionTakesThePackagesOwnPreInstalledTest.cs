using System;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>An adopted install record says what the package declares NOW</b> (MeshWeaver.Plugins#4962).
/// The CLI harness packages dropped <c>preInstalled</c> from their roots, but their install records
/// kept <c>preInstalled: true</c> through every later sync adoption, because
/// <see cref="PackageUpdateReconciler.AdoptedFromSync"/> kept "every other install-time field". The
/// record then claimed the package was part of the platform baseline while the package itself said it
/// is sold through its tier. Pure: the adoption is a function of the record and the candidate.
/// </summary>
public class ASyncAdoptionTakesThePackagesOwnPreInstalledTest
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    private static PackageManifest Record(bool preInstalled) => new()
    {
        Id = "Codex",
        Source = "Plugins",
        Version = "1.2.19",
        ModuleVersion = "old",
        PreInstalled = preInstalled,
        AuthorizedBy = "system-security",
    };

    private static PackageManifest Candidate(bool preInstalled) => new()
    {
        Id = "Codex",
        Source = "Plugins",
        Version = "1.2.244",
        ModuleVersion = "new",
        PreInstalled = preInstalled,
    };

    /// <summary>The incident: a record stamped while the package was pre-installed adopts a version
    /// whose root no longer declares it. The record must stop claiming it.</summary>
    [Fact]
    public void ARecordStampedPreInstalled_AdoptingACandidateThatIsNot_IsNoLongerPreInstalled()
    {
        var adopted = PackageUpdateReconciler.AdoptedFromSync(Record(preInstalled: true), Candidate(preInstalled: false), Now);

        Assert.False(adopted.PreInstalled);
        Assert.Equal("new", adopted.ModuleVersion);
    }

    /// <summary>The other direction: a package that becomes pre-installed is recorded as such.</summary>
    [Fact]
    public void ARecordNotPreInstalled_AdoptingACandidateThatIs_IsPreInstalled()
    {
        var adopted = PackageUpdateReconciler.AdoptedFromSync(Record(preInstalled: false), Candidate(preInstalled: true), Now);

        Assert.True(adopted.PreInstalled);
    }

    /// <summary>Only the declaration follows the candidate: the install-time fields the adoption
    /// promises to keep (who authorized the install) are still the record's.</summary>
    [Fact]
    public void TheAdoption_KeepsTheRecordsAuthorizingPrincipal()
    {
        var adopted = PackageUpdateReconciler.AdoptedFromSync(Record(preInstalled: true), Candidate(preInstalled: false), Now);

        Assert.Equal("system-security", adopted.AuthorizedBy);
        Assert.Equal(Now, adopted.InstalledAtUtc);
    }
}
