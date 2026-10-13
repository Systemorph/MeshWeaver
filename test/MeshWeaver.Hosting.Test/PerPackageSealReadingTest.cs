using System;
using System.IO;
using System.Linq;
using MeshWeaver.Hosting;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// The publication seals PER PACKAGE (MeshWeaver.Plugins#3507): the reader adopts the per-package
/// listing a publication carries — each sealed bundle at the commit IT was baked from, carried or
/// fresh — and reads a publication sealed before that listing existed exactly as before: every
/// bundle at <c>source-commit.txt</c>, nothing carried.
/// </summary>
public class PerPackageSealReadingTest : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "per-package-seal-" + Guid.NewGuid().ToString("N"));
    private const string Identity = "s9c0b05d61cb34bbffde9ad7a32ab1a8b";
    private const string Publication = "1111111111111111111111111111111111111111";
    private const string Older = "2222222222222222222222222222222222222222";

    private string Seal(string? packagesJson, params string[] bundles)
    {
        var dir = Path.Combine(root, Identity, "plugins");
        Directory.CreateDirectory(dir);
        foreach (var b in bundles)
            using (System.IO.Compression.ZipFile.Open(Path.Combine(dir, b), System.IO.Compression.ZipArchiveMode.Create)) { }
        File.WriteAllText(Path.Combine(dir, SealedPublicationIndex.SourceCommitMarkerFileName), Publication + "\n");
        File.WriteAllText(Path.Combine(dir, SealedPublicationIndex.RepositoryMarkerFileName), "Systemorph/MeshWeaver.Plugins\n");
        if (packagesJson is not null)
            File.WriteAllText(Path.Combine(dir, SealedPublicationIndex.PackagesListingFileName), packagesJson);
        File.WriteAllText(Path.Combine(dir, ShippedPrebuiltBundles.CompletionSentinelFileName), string.Join("\n", bundles) + "\n");
        return dir;
    }

    private const string Listing = $$"""
        {"schema":"mw-sealed-packages/1","publicationCommit":"{{Publication}}","packages":[
          {"package":"Store","bundle":"Store.zip","sourceCommit":"{{Publication}}","version":"1.31.0","requires":[],"carried":false},
          {"package":"Essentials","bundle":"Essentials.zip","sourceCommit":"{{Older}}","version":"1.12.4","requires":["Store@^1.0.0"],"carried":true}
        ]}
        """;

    [Fact]
    public void APerPackageListing_IsAdopted_EachPackageAtItsOwnCommit()
    {
        Seal(Listing, "Store.zip", "Essentials.zip");
        var read = SealedPublicationIndex.ReadFor(root, Identity).Single();

        read.IsSealed.Should().BeTrue();
        read.PackagesListed.Should().BeTrue();
        read.SourceCommit.Should().Be(Publication, "the publication commit stays the newest — never-seal-backwards reads it");
        read.CommitOf("Store").Should().Be(Publication);
        read.CommitOf("Essentials").Should().Be(Older, "a carried package's bytes were baked from ITS sealed commit");
        read.PackageOf("Essentials")!.Version.Should().Be("1.12.4");
        read.PackageOf("Essentials")!.Requires.Should().Equal("Store@^1.0.0");
        read.Carried.Select(p => p.Package).Should().Equal("Essentials");
    }

    [Fact]
    public void APublicationWithoutAListing_ReadsAsTheOldSingleCommitListing()
    {
        Seal(null, "Store.zip", "Essentials.zip");
        var read = SealedPublicationIndex.ReadFor(root, Identity).Single();

        read.IsSealed.Should().BeTrue();
        read.PackagesListed.Should().BeFalse();
        read.Packages.Select(p => p.Package).OrderBy(p => p, StringComparer.Ordinal).Should().Equal("Essentials", "Store");
        read.CommitOf("Essentials").Should().Be(Publication, "before the listing every bundle was baked from one commit");
        read.Carried.Should().BeEmpty();
    }

    [Fact]
    public void APackageTheSealDoesNotList_HasNoCommit()
    {
        Seal(Listing, "Store.zip", "Essentials.zip");
        SealedPublicationIndex.ReadFor(root, Identity).Single().CommitOf("Maps").Should().BeNull();
    }

    [Fact]
    public void AnUnreadableListing_IsAnUnreadableReading_NeverTheSingleCommitFallback()
    {
        Seal("{ not json", "Store.zip");
        var (sources, outcome) = SealedPublicationIndex.ReadingFor(root, Identity);

        outcome.Should().Be(SealedReadOutcome.Unreadable,
            "which build each package seals is unknown — attributing every bundle to the publication commit would be a guess");
        sources.Single().IsSealed.Should().BeFalse();
        sources.Single().Refusal.Should().Contain(SealedPublicationIndex.PackagesListingFileName);
    }

    [Fact]
    public void AListingThatOmitsASealedBundle_IsRefused()
    {
        Seal(Listing, "Store.zip", "Essentials.zip", "Maps.zip");
        var (sources, outcome) = SealedPublicationIndex.ReadingFor(root, Identity);

        outcome.Should().Be(SealedReadOutcome.Unreadable);
        sources.Single().Refusal.Should().Contain("Maps.zip");
    }

    // ── the reconciler adopts it: a CARRIED package's types are declined on their fingerprint BY
    // ── DESIGN (its source at the publication commit is newer than its bytes), so re-importing the
    // ── same commit on every seal arrival would change nothing. A fresh package's decline is still
    // ── the drift it always was.
    private static readonly MeshWeaver.GitSync.RepoIdentity Plugins = new("Systemorph", "MeshWeaver.Plugins");

    private static MeshWeaver.GitSync.GitHubSyncConfig AtPublication(string folder) => new()
    {
        RepositoryUrl = "https://github.com/Systemorph/MeshWeaver.Plugins",
        Branch = "main",
        Subdirectory = folder,
        LastSyncCommitSha = Publication,
    };

    [Fact]
    public void ACarriedPackagesDeclinedTypes_AreSteadyState_NotAReimport()
    {
        Seal(Listing, "Store.zip", "Essentials.zip");
        var read = SealedPublicationIndex.ReadFor(root, Identity).Single();

        var plan = MeshWeaver.GitSync.SealedSyncReconcile.Decide(
            read, Plugins, "Essentials", AtPublication("Essentials"), [read], Identity, ["Essentials/Note"]);

        plan.Action.Should().Be(MeshWeaver.GitSync.SealedSyncReconcile.Action.None);
        plan.SteadyState.Should().BeTrue();
        plan.Reason.Should().Contain("carries package 'Essentials'");
    }

    [Fact]
    public void AFreshPackagesDeclinedTypes_AreStillReconciled()
    {
        Seal(Listing, "Store.zip", "Essentials.zip");
        var read = SealedPublicationIndex.ReadFor(root, Identity).Single();

        var plan = MeshWeaver.GitSync.SealedSyncReconcile.Decide(
            read, Plugins, "Store", AtPublication("Store"), [read], Identity, ["Store/Catalog"]);

        plan.Action.Should().Be(MeshWeaver.GitSync.SealedSyncReconcile.Action.ReconcileAtSealedCommit,
            "a package sealed fresh at the commit the source claims still means the live sources drifted");
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
