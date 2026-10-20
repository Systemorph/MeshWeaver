using System.Collections.Generic;
using System.Runtime.InteropServices;
using MeshWeaver.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// The shared artifact store keeps one bundle store per runtime identifier
/// (Doc/Architecture/SharedArtifactStore); a record renders <c>&lt;mount&gt;/{rid}/prebuilt-bundles</c> and
/// the portal expands the placeholder at boot to ITS process's identifier. Pinned: the identifier is the
/// store's directory name (<c>linux-x64</c>, <c>osx-arm64</c>), a root without the placeholder — every
/// per-portal volume — is untouched, and the expansion is written back so every later read sees it.
/// </summary>
public class BundleRootRuntimeIdentifierTest
{
    [Theory]
    [InlineData("linux", Architecture.X64, "linux-x64")]
    [InlineData("linux", Architecture.Arm64, "linux-arm64")]
    [InlineData("osx", Architecture.Arm64, "osx-arm64")]
    [InlineData("win", Architecture.X64, "win-x64")]
    public void TheIdentifierIsTheStoresDirectoryName(string os, Architecture arch, string expected) =>
        Assert.Equal(expected, BundleRootRuntimeIdentifier.Compose(os, arch));

    [Fact]
    public void APerPortalRootIsUntouched_ASharedStoreRootIsExpanded()
    {
        Assert.Equal("/data/prebuilt-bundles", BundleRootRuntimeIdentifier.Expand("/data/prebuilt-bundles", "linux-x64"));
        Assert.Null(BundleRootRuntimeIdentifier.Expand(null, "linux-x64"));
        Assert.Equal("/artifacts/osx-arm64/prebuilt-bundles", BundleRootRuntimeIdentifier.Expand("/artifacts/{rid}/prebuilt-bundles", "osx-arm64"));
    }

    [Fact]
    public void TheExpansionIsWrittenBack_SoEveryLaterReadSeesIt()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [ShippedPrebuiltBundles.PublishedRootConfigKey] = "/artifacts/{rid}/prebuilt-bundles" })
            .Build();

        Assert.Equal("/artifacts/linux-x64/prebuilt-bundles", BundleRootRuntimeIdentifier.ExpandConfigured(configuration, "linux-x64"));
        Assert.Equal("/artifacts/linux-x64/prebuilt-bundles", configuration[ShippedPrebuiltBundles.PublishedRootConfigKey]);

        var untouched = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        Assert.Null(BundleRootRuntimeIdentifier.ExpandConfigured(untouched, "linux-x64"));
        Assert.Null(untouched[ShippedPrebuiltBundles.PublishedRootConfigKey]);
    }
}
