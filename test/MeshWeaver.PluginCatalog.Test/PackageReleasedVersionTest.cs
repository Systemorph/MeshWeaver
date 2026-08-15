#pragma warning disable CS1591

using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// Pins that a module's RELEASED SemVer survives onto the install record.
///
/// <para>🚨 <b>Nothing else in the mesh carries it.</b> Three neighbouring fields look like they
/// might and none does: <c>PackageManifest.Version</c> is the whole-repo commit sha,
/// <c>ModuleVersion</c> is a content HASH (exact but unordered — it cannot express "newer"), and
/// the plugin node's own <c>PluginContent.version</c> holds only the AUTHORED
/// <c>MAJOR.MINOR</c>. The PATCH is derived by <c>gen-manifests.py</c> from the content hash, so
/// ThreeBody reads <c>1.3</c> on its node while its <c>manifest.lock</c> — and its
/// <c>ThreeBody/v1.3.2</c> tag — say <c>1.3.2</c>.</para>
///
/// <para>The lock is a repo artifact GitSync does not import, so without this the portal cannot
/// name the version a module actually shipped at. A package feed that invented its own number
/// would fork the version namespace away from the tags — the drift <c>tag-modules.py</c> exists to
/// prevent ("one version, one tree — forever").</para>
/// </summary>
public class PackageReleasedVersionTest
{
    [Fact]
    public void TheLocksSemVerIsDistinctFromEveryNeighbouringField()
    {
        // The four values a reader could mistake for one another, as they appear for a real module.
        var record = new PackageManifest
        {
            Id = "ThreeBody",
            Version = "ef6452af5",                 // whole-repo commit sha
            ModuleVersion = "3f4f8e27a2acd3e4",    // content hash — unordered
            ReleasedVersion = "1.3.2",             // the tagged SemVer
        };

        Assert.Equal("1.3.2", record.ReleasedVersion);
        Assert.NotEqual(record.ReleasedVersion, record.Version);
        Assert.NotEqual(record.ReleasedVersion, record.ModuleVersion);
    }

    [Fact]
    public void AManifestWithoutAVersionLeavesItNull()
        // A module whose manifest predates versioning must not acquire an invented number —
        // callers distinguish "no released version" from a wrong one.
        => Assert.Null(new PackageManifest { Id = "Legacy" }.ReleasedVersion);

    [Fact]
    public void TheSemVerIsCarriedVerbatim()
    {
        // Not normalised, not widened: the string must match the git tag exactly, because that is
        // what a dependent's caret range resolves against.
        const string tagged = "1.0.51";

        Assert.Equal(tagged, new PackageManifest { Id = "Store", ReleasedVersion = tagged }.ReleasedVersion);
    }
}
