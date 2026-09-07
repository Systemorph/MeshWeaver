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
            File.WriteAllText(Path.Combine(dir, b), "zip");
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

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
    }
}
