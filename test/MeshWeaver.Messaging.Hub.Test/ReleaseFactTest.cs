using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// The release fact's identity is what makes ingest idempotent, and idempotent ingest is what lets
/// the release BROADCAST stay unreliable without the truth becoming unreliable too. A webhook that
/// redelivers, a reconciler that sweeps, two silos that both observe one publication — all must land
/// on ONE node.
/// </summary>
public class ReleaseFactTest
{
    [Fact]
    public void IdFor_IsStableAcrossObservationsOfOnePublication()
    {
        // The same publication observed twice — a redelivered webhook, a reconciler sweep — must
        // resolve to the same node, or the truth accumulates duplicates exactly where it is supposed
        // to be authoritative.
        var first = Release.IdFor("Systemorph/MeshWeaver.Plugins", "MeshWeaver.Blazor.EntityViews", "3.0.0-rc8.ci.5432");
        var second = Release.IdFor("Systemorph/MeshWeaver.Plugins", "MeshWeaver.Blazor.EntityViews", "3.0.0-rc8.ci.5432");

        Assert.Equal(first, second);
    }

    [Theory]
    // GitHub repositories are case-insensitive (cf. RepoIdentity.Matches, which compares Owner and
    // Repo with OrdinalIgnoreCase), and so are NuGet package ids. Two observations of ONE
    // publication that differ only in casing must not mint two facts — that duplication is exactly
    // what the derived id exists to prevent.
    [InlineData("systemorph/meshweaver.plugins", "MeshWeaver.Blazor.EntityViews")]
    [InlineData("Systemorph/MeshWeaver.Plugins", "meshweaver.blazor.entityviews")]
    [InlineData("SYSTEMORPH/MESHWEAVER.PLUGINS", "MESHWEAVER.BLAZOR.ENTITYVIEWS")]
    public void IdFor_IsCaseInsensitiveOnRepositoryAndPackage(string repository, string packageId)
    {
        var canonical = Release.IdFor(
            "Systemorph/MeshWeaver.Plugins", "MeshWeaver.Blazor.EntityViews", "3.0.0-rc8.ci.5432");

        Assert.Equal(canonical, Release.IdFor(repository, packageId, "3.0.0-rc8.ci.5432"));
    }

    [Fact]
    public void IdFor_KeepsVersionCaseSensitive()
    {
        // SemVer states pre-release identifiers ARE case-sensitive, so 1.0.0-RC1 and 1.0.0-rc1 are
        // genuinely different versions. Folding them would merge two distinct releases into one
        // fact — the opposite failure to the one above, and the worse of the two.
        var lower = Release.IdFor("Systemorph/MeshWeaver", "MeshWeaver.Blazor", "1.0.0-rc1");
        var upper = Release.IdFor("Systemorph/MeshWeaver", "MeshWeaver.Blazor", "1.0.0-RC1");

        Assert.NotEqual(lower, upper);
    }

    [Theory]
    // one package, two versions — a new release, not a restatement of the old one
    [InlineData("Systemorph/MeshWeaver.Plugins", "MeshWeaver.Blazor.EntityViews", "3.0.0-rc8.ci.5433")]
    // same package NAME from a different repo — a distinct artefact that must not collide
    [InlineData("Systemorph/MeshWeaver", "MeshWeaver.Blazor.EntityViews", "3.0.0-rc8.ci.5432")]
    // same repo and version, different package — a repo publishes SEVERAL facts per release
    [InlineData("Systemorph/MeshWeaver.Plugins", "MeshWeaver.Blazor.Radzen", "3.0.0-rc8.ci.5432")]
    public void IdFor_SeparatesFactsThatMustNotCollide(string repository, string packageId, string version)
    {
        var baseline = Release.IdFor("Systemorph/MeshWeaver.Plugins", "MeshWeaver.Blazor.EntityViews", "3.0.0-rc8.ci.5432");

        Assert.NotEqual(baseline, Release.IdFor(repository, packageId, version));
    }

    [Fact]
    public void Platform_IsCarriedOnTheFact_NotDerivedByTheConsumer()
    {
        // Bundles are ADOPTED, not rebuilt, so a consumer can only adopt bytes built against a
        // framework identity it can resolve. Carrying it on the fact is what lets the consumer check
        // rather than re-derive — the #1814 failure was two hosts resolving two identities for one
        // commit, with nothing comparing them.
        var release = new Release
        {
            Id = Release.IdFor("Systemorph/MeshWeaver.Plugins", "MeshWeaver.Blazor.Radzen", "3.0.0-rc8.ci.5432"),
            Repository = "Systemorph/MeshWeaver.Plugins",
            PackageId = "MeshWeaver.Blazor.Radzen",
            Version = "3.0.0-rc8.ci.5432",
            Platform = "s4d400050c6d76f830c95d0a21e56febc",
            Commit = "dbdbebc4e",
            Released = new DateTime(2026, 8, 26, 8, 0, 0, DateTimeKind.Utc)
        };

        Assert.Equal("s4d400050c6d76f830c95d0a21e56febc", release.Platform);
        Assert.Equal(release.Id, Release.IdFor(release.Repository, release.PackageId, release.Version));
    }
}
