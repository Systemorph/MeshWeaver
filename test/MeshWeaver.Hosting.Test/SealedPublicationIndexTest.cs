using System;
using System.IO;
using System.Linq;
using MeshWeaver.Hosting;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// The reading behind MeshWeaver.Plugins#1430: what the registry sealed for one framework identity,
/// per source — repository and commit from the lane's markers, sealed by the same rule the boot
/// seeder judges by (sentinel present, every listed bundle on disk).
/// </summary>
public class SealedPublicationIndexTest : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "sealed-index-" + Guid.NewGuid().ToString("N"));
    private const string Identity = "s9c0b05d61cb34bbffde9ad7a32ab1a8b";

    private string Source(string name, bool sentinel, string? commit, string? repository, params string[] bundles)
    {
        var dir = Path.Combine(root, Identity, name);
        Directory.CreateDirectory(dir);
        foreach (var b in bundles)
            // A real (empty) archive: the sealed reading reads each bundle manifest for its producing
            // platform build, and bytes that are not an archive make the reading UNREADABLE.
            using (System.IO.Compression.ZipFile.Open(Path.Combine(dir, b), System.IO.Compression.ZipArchiveMode.Create)) { }
        if (commit is not null)
            File.WriteAllText(Path.Combine(dir, SealedPublicationIndex.SourceCommitMarkerFileName), commit + "\n");
        if (repository is not null)
            File.WriteAllText(Path.Combine(dir, SealedPublicationIndex.RepositoryMarkerFileName), repository + "\n");
        if (sentinel)
            File.WriteAllText(Path.Combine(dir, ShippedPrebuiltBundles.CompletionSentinelFileName), string.Join("\n", bundles) + "\n");
        return dir;
    }

    [Fact]
    public void ASealedSource_ReadsItsMarkersAndIsSealed()
    {
        Source("plugins", sentinel: true, "abcdef1234567890", "Systemorph/MeshWeaver.Plugins", "Edu.zip", "Store.zip");
        var read = SealedPublicationIndex.ReadFor(root, Identity).Single();
        read.Source.Should().Be("plugins");
        read.Repository.Should().Be("Systemorph/MeshWeaver.Plugins");
        read.SourceCommit.Should().Be("abcdef1234567890");
        read.IsSealed.Should().BeTrue();
        read.Refusal.Should().BeNull();
    }

    [Fact]
    public void NoSentinel_IsNotSealed_AndSaysSo()
    {
        Source("plugins", sentinel: false, "abcdef1234567890", null, "Edu.zip");
        var read = SealedPublicationIndex.ReadFor(root, Identity).Single();
        read.IsSealed.Should().BeFalse();
        read.Refusal.Should().Contain("no completion sentinel");
        read.Repository.Should().BeNull("the seal predates the repository marker");
    }

    [Fact]
    public void AListedBundleMissingFromDisk_IsTorn()
    {
        var dir = Source("plugins", sentinel: true, "abcdef1234567890", null, "Edu.zip", "Store.zip");
        File.Delete(Path.Combine(dir, "Store.zip"));
        var read = SealedPublicationIndex.ReadFor(root, Identity).Single();
        read.IsSealed.Should().BeFalse();
        read.Refusal.Should().Contain("Store.zip");
    }

    [Fact]
    public void AnUnknownCommitMarker_ReadsAsNoCommit()
    {
        Source("plugins", sentinel: true, "unknown", null, "Edu.zip");
        SealedPublicationIndex.ReadFor(root, Identity).Single().SourceCommit.Should().BeNull();
    }

    [Fact]
    public void AnAbsentRootOrIdentity_ReadsAsNothingSealed()
    {
        SealedPublicationIndex.ReadFor(null, Identity).Should().BeEmpty();
        SealedPublicationIndex.ReadFor(root, null).Should().BeEmpty();
        SealedPublicationIndex.ReadFor(Path.Combine(root, "absent"), Identity).Should().BeEmpty();
    }

    [Fact]
    public void EverySourceUnderTheIdentity_IsRead_InOrder()
    {
        Source("plugins", sentinel: true, "aaaaaaaaaaaaaaaa", "Systemorph/MeshWeaver.Plugins", "Edu.zip");
        Source("education", sentinel: true, "bbbbbbbbbbbbbbbb", "Systemorph/MeshWeaver.Education", "Course.zip");
        SealedPublicationIndex.ReadFor(root, Identity).Select(s => s.Source).Should().Equal("education", "plugins");
    }

    // ---- the pooled walk observes the pool's token (the #7049 shape) ---------------------------

    /// <summary>
    /// The per-source walk is a pointer, two markers, a seal, a probe and a manifest read per bundle,
    /// one share round trip each. On a pool leaf it must stop when the pool cancels, and a cancel is
    /// never reported as an UNREADABLE reading (which holds every source) or as an empty one.
    /// </summary>
    [Fact]
    public void ACancelledWalk_Throws_AndIsNeverReportedAsAReading()
    {
        Source("education", sentinel: true, "1111111111111111", "Systemorph/MeshWeaver.Education", "Edu.zip");
        Source("plugins", sentinel: true, "abcdef1234567890", "Systemorph/MeshWeaver.Plugins", "Store.zip");
        Directory.CreateDirectory(Path.Combine(root, SealedPublicationIndex.ReleaseMarkerDirectoryName));
        File.WriteAllText(Path.Combine(root, SealedPublicationIndex.ReleaseMarkerDirectoryName, "3.0.0-ci.1"), Identity + "\n");
        using var cancelled = new System.Threading.CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => SealedPublicationIndex.ReadingFor(root, Identity, cancelled.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => SealedPublicationIndex.ReadFor(root, Identity, cancelled.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => SealedPublicationIndex.ReleasesOf(root, cancelled.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => SealedPublicationIndex.NewerLineThan(root, Identity, cancelled.Token));
    }

    [Fact]
    public void AnUncancelledTokenWalk_ReadsExactlyWhatTheTokenlessWalkReads()
    {
        // The positive control: the token changes how far a cancelled walk gets, never what it reads.
        Source("education", sentinel: true, "1111111111111111", "Systemorph/MeshWeaver.Education", "Edu.zip");
        Source("plugins", sentinel: false, "abcdef1234567890", null, "Store.zip");

        var withToken = SealedPublicationIndex.ReadingFor(root, Identity, System.Threading.CancellationToken.None);
        var without = SealedPublicationIndex.ReadingFor(root, Identity);

        static string Shape(SealedSource s) => $"{s.Source}|{s.Repository}|{s.SourceCommit}|{s.IsSealed}|{s.Refusal}";
        withToken.Outcome.Should().Be(without.Outcome);
        withToken.Sources.Select(Shape).Should().Equal(without.Sources.Select(Shape));
        withToken.Sources.Count(s => s.IsSealed).Should().Be(1);
        SealedPublicationIndex.ReadFor(root, Identity, System.Threading.CancellationToken.None).Select(Shape)
            .Should().Equal(SealedPublicationIndex.ReadFor(root, Identity).Select(Shape));
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
    }
}
