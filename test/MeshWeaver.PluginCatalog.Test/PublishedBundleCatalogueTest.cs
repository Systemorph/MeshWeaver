#pragma warning disable CS1591

using MeshWeaver.Hosting;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// The OBSERVATION half of the release gates: reading the published bundle root the bake lane
/// writes and every portal mounts. Staged as real directories on disk, because the whole contract
/// under test is a file-system layout shared with a bash publisher — asserting it against an
/// abstraction would prove nothing about the thing the publisher writes.
/// </summary>
public class PublishedBundleCatalogueTest : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-bundle-catalogue-" + Guid.NewGuid().ToString("N"));

    public PublishedBundleCatalogueTest() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { Directory.Delete(root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    /// <summary>Stages what publish-bake-bundles.sh writes: the bundles, then the seal LAST.</summary>
    private void Publish(string identity, string source, params string[] bundles)
    {
        var dir = Path.Combine(root, identity, source);
        Directory.CreateDirectory(dir);
        foreach (var bundle in bundles)
            File.WriteAllText(Path.Combine(dir, bundle), "bytes");
        File.WriteAllLines(
            Path.Combine(dir, ShippedPrebuiltBundles.CompletionSentinelFileName), bundles);
    }

    /// <summary>A publication that died before the seal — no sentinel.</summary>
    private void PublishTorn(string identity, string source, params string[] bundles)
    {
        var dir = Path.Combine(root, identity, source);
        Directory.CreateDirectory(dir);
        foreach (var bundle in bundles)
            File.WriteAllText(Path.Combine(dir, bundle), "bytes");
    }

    private void MarkRelease(string version, string identity)
    {
        var dir = Path.Combine(root, PublishedBundleCatalogue.ReleaseMarkerDirectoryName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, version), identity + "\n");
    }

    [Fact]
    public void AMarkedRelease_ResolvesItsIdentity_AndTheSealedBundlesAcrossEverySource()
    {
        MarkRelease("3.0.0-rc4.ci.4049", "sabc");
        Publish("sabc", "meshweaver-content", "Doc.zip", "Northwind.zip");
        Publish("sabc", "plugins", "Store.zip");

        var observation = PublishedBundleCatalogue.Read(root, "3.0.0-rc4.ci.4049");

        Assert.Equal("sabc", observation.Target.FrameworkIdentity);
        Assert.Null(observation.Artifacts.ReadFailure);
        // Bundles from every producing source count, and the .zip is stripped so the set compares
        // against package ids.
        Assert.Equal(
            ["Doc", "Northwind", "Store"],
            observation.Artifacts.SealedBundles.OrderBy(b => b, StringComparer.Ordinal));
    }

    [Fact]
    public void AReleaseWithNoMarker_ResolvesNoIdentity_WhichTheGateReadsAsAHold()
    {
        Publish("sabc", "meshweaver-content", "Doc.zip");

        var observation = PublishedBundleCatalogue.Read(root, "3.0.0-rc9.ci.1");

        // No marker means that release published no platform content bake — the identity is
        // genuinely unknown, and guessing it is what the marker exists to prevent.
        Assert.Null(observation.Target.FrameworkIdentity);
        Assert.False(ReleaseAvailability.IsUpdatable(
                observation.Target, [new RequiredPackage("Doc", "Doc")], observation.Artifacts)
            .IsUpdatable);
    }

    [Fact]
    public void ATornPublication_DoesNotCount_BecauseTheBootSeederWouldSkipItToo()
    {
        MarkRelease("3.0.0", "sabc");
        Publish("sabc", "meshweaver-content", "Doc.zip");
        PublishTorn("sabc", "plugins", "Store.zip");

        // Counting an unsealed source would clear a release the portal then recompiles.
        Assert.Equal(["Doc"], PublishedBundleCatalogue.Read(root, "3.0.0").Artifacts.SealedBundles);
    }

    [Fact]
    public void ASealedDirectoryMissingAListedBundle_DoesNotCount()
    {
        MarkRelease("3.0.0", "sabc");
        Publish("sabc", "plugins", "Store.zip", "Edu.zip");
        File.Delete(Path.Combine(root, "sabc", "plugins", "Edu.zip"));

        // A torn-beyond-the-seal source is skipped WHOLE, exactly as the seeder skips it — half a
        // source is not half available.
        Assert.Empty(PublishedBundleCatalogue.Read(root, "3.0.0").Artifacts.SealedBundles);
    }

    [Fact]
    public void AnIdentityWithNothingPublished_IsResolvedButEmpty()
    {
        MarkRelease("3.0.0", "snothing");

        var observation = PublishedBundleCatalogue.Read(root, "3.0.0");

        Assert.Equal("snothing", observation.Target.FrameworkIdentity);
        Assert.Empty(observation.Artifacts.SealedBundles);
        // "We looked and there is nothing" is a DIFFERENT answer from "we could not look".
        Assert.Null(observation.Artifacts.ReadFailure);
    }

    [Fact]
    public void NoBundleRootConfigured_IsUnreadable_NotEmpty()
    {
        var observation = PublishedBundleCatalogue.Read(null, "3.0.0");

        Assert.False(string.IsNullOrEmpty(observation.Artifacts.ReadFailure));
        Assert.True(ReleaseAvailability.IsUpdatable(
                observation.Target, [new RequiredPackage("Doc", "Doc")], observation.Artifacts)
            .IsIndeterminate);
    }

    [Fact]
    public void SealedBundlesForIdentity_ReadsTheRunningIdentitysAdoptedSet()
    {
        Publish("srunning", "meshweaver-content", "Doc.zip");
        Publish("sother", "meshweaver-content", "Doc.zip", "Other.zip");

        Assert.Equal(["Doc"], PublishedBundleCatalogue.SealedBundlesForIdentity(root, "srunning"));
        Assert.Empty(PublishedBundleCatalogue.SealedBundlesForIdentity(root, "sabsent"));
    }

    [Fact]
    public void SealedSources_NamesTheProducingRepos_WhichIsTheBuildGatesQuestion()
    {
        Publish("sabc", "plugins", "Store.zip");
        PublishTorn("sabc", "education", "Course.zip");

        // An upstream whose publication is torn has NOT arrived — a downstream build that
        // proceeded on it would gate against a half-published upstream.
        Assert.Equal(["plugins"], PublishedBundleCatalogue.SealedSources(root, "sabc"));
    }

    [Fact]
    public void SealedSources_RejectsATornBeyondTheSealSource_NotJustAnUnsealedOne()
    {
        // 🚨 The regression Copilot caught on #1761: keying on the sentinel ALONE would call this
        // ready, while the portal reading the same directory skips it whole. The optimistic answer
        // is exactly the direction a gate must never be wrong in — a downstream repo would build
        // against an upstream whose bytes are not all there.
        Publish("sabc", "plugins", "Store.zip", "Edu.zip");
        File.Delete(Path.Combine(root, "sabc", "plugins", "Edu.zip"));

        Assert.Empty(PublishedBundleCatalogue.SealedSources(root, "sabc"));
        // …and the two readings agree about that directory, which is the actual invariant.
        Assert.Empty(PublishedBundleCatalogue.SealedBundlesForIdentity(root, "sabc"));
    }

    [Fact]
    public void BundleNamesAreMatchedCaseInsensitively()
    {
        // The sentinel carries file names off a store that is not everywhere case-sensitive, while
        // packages are named by id. A case-sensitive set would report ContentBakeMissing for a
        // package that is in fact published — a hold nobody could act on because nothing is wrong.
        MarkRelease("3.0.0", "sabc");
        Publish("sabc", "plugins", "store.zip");

        var observation = PublishedBundleCatalogue.Read(root, "3.0.0");

        Assert.True(ReleaseAvailability.IsUpdatable(
                observation.Target, [new RequiredPackage("Store", "Store")], observation.Artifacts)
            .IsUpdatable);
    }
}
