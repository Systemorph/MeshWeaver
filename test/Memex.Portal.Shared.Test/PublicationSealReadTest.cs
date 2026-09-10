using MeshWeaver.Hosting;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>Missing seals are decided by the file open, including removal after observation.</summary>
public class PublicationSealReadTest : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "mw-seal-read-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASealRemovedAfterObservation_IsAbsentAtTheOpen(bool removeDirectory)
    {
        var directory = Path.Combine(root, "identity", "plugins");
        Directory.CreateDirectory(directory);
        var seal = Path.Combine(directory, ShippedPrebuiltBundles.CompletionSentinelFileName);
        File.WriteAllText(seal, "Store.zip\n");
        Assert.True(File.Exists(seal));

        // Remove at the filesystem operation, not before entering the reader. Even a reader
        // that reinstates File.Exists observes a present seal before this operation removes it.
        var opened = false;
        var lines = PublishedBundleCatalogue.ReadSealLines(seal, path =>
        {
            Assert.True(File.Exists(path));
            opened = true;
            if (removeDirectory)
                Directory.Delete(Path.Combine(root, "identity"), recursive: true);
            else
                File.Delete(path);
            return File.ReadAllLines(path);
        });
        Assert.True(opened);
        Assert.Null(lines);
        Assert.Null(PublishedBundleCatalogue.SealedPublicationOf(directory).Bundles);
        Assert.Null(PublishedBundleCatalogue.SealedBundlesOf(directory));
    }

    [Fact]
    public void AReadableSeal_PreservesItsListing()
    {
        Directory.CreateDirectory(root);
        var seal = Path.Combine(root, ShippedPrebuiltBundles.CompletionSentinelFileName);
        File.WriteAllText(seal, " Store.zip \n\nEdu.zip\n");
        File.WriteAllText(Path.Combine(root, "Store.zip"), "store");
        File.WriteAllText(Path.Combine(root, "Edu.zip"), "education");

        var lines = PublishedBundleCatalogue.ReadSealLines(seal);
        Assert.NotNull(lines);
        Assert.Equal([" Store.zip ", "", "Edu.zip"], lines);
        Assert.Equal(["Store.zip", "Edu.zip"], PublishedBundleCatalogue.SealedPublicationOf(root).Bundles);
        Assert.Equal(["Store.zip", "Edu.zip"], PublishedBundleCatalogue.SealedBundlesOf(root));
    }

    [Fact]
    public void AnUnreadableExistingSeal_IsNotReportedAsAbsent()
    {
        Directory.CreateDirectory(root);
        var seal = Path.Combine(root, ShippedPrebuiltBundles.CompletionSentinelFileName);
        File.WriteAllText(seal, "Store.zip\n");
        using var held = File.Open(seal, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Throws<IOException>(() => PublishedBundleCatalogue.ReadSealLines(seal));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
