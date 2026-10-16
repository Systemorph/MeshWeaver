using MeshWeaver.Deployment;
using Xunit;

namespace MeshWeaver.Deployment.Contract.Test;

/// <summary>
/// The record's <c>bundles</c> block (MeshWeaver#6825): where an instance pulls its sealed NodeType
/// publications from before the portal starts — the chart's <c>bundles.registry</c> /
/// <c>bundles.sources</c> / <c>bundles.identity</c>. Before it existed an estate's bundle source
/// lived only in a hand-written values layer, so a record-driven roll either dropped it (every pod
/// compiling NodeTypes itself again) or was refused for dropping it. Pinned here: the block reads
/// from the record's JSON in the chart's own shape, and the pre-warm root renders from the same
/// declaration — only when a registry is stated — so the shelf the init container fills is the
/// directory the portal reads.
/// </summary>
public class BundlesRecordTest
{
    private const string BundlesJson = """
        {
          "$type": "DeploymentContent",
          "host": "client-test.example.com",
          "imagePullSecret": "fleet-registry-pull",
          "bundles": {
            "registry": "cr.example.test",
            "sources": [ "plugins", "reinsurance" ],
            "identity": "fw-3.1.0-abc"
          }
        }
        """;

    [Fact]
    public void TheRecordsJson_CarriesTheBundlesBlock_InTheChartsShape_AndWritesItBack()
    {
        var record = DeploymentRecordJson.Read(BundlesJson)!;

        Assert.NotNull(record.Bundles);
        Assert.Equal("cr.example.test", record.Bundles!.Registry);
        Assert.Equal(["plugins", "reinsurance"], record.Bundles.Sources);
        Assert.Equal("fw-3.1.0-abc", record.Bundles.Identity);

        var written = DeploymentRecordJson.Write(record);
        Assert.Contains("\"bundles\":{", written);
        Assert.Contains("\"registry\":\"cr.example.test\"", written);
        Assert.Contains("\"sources\":[\"plugins\",\"reinsurance\"]", written);
        var reread = DeploymentRecordJson.Read(written)!.Bundles!;
        Assert.Equal(("cr.example.test", "fw-3.1.0-abc"), (reread.Registry, reread.Identity));
        Assert.Equal(["plugins", "reinsurance"], reread.Sources);
    }

    [Fact]
    public void ARecordThatFetchesBundles_RendersThePreWarmRoot_OnBothRoutes()
    {
        var record = DeploymentRecordJson.Read(BundlesJson)!;

        foreach (var options in new[] { PortalConfigOptions.Helm, PortalConfigOptions.Aspire("http://localhost:8080") })
            Assert.Equal(BundlesSpec.DefaultRoot, DeploymentPortalConfig.PortalConfig(record, options)["PreWarm__PrebuiltBundleRoot"]);
    }

    [Fact]
    public void AStatedRoot_IsTheOneRendered_Trimmed()
    {
        var record = new DeploymentContent().WithBundles("cr.example.test", ["plugins"], root: " /data/shelf ");

        Assert.Equal("/data/shelf", DeploymentPortalConfig.BundleRoot(record));
        Assert.Equal("/data/shelf", DeploymentPortalConfig.PortalConfig(record)["PreWarm__PrebuiltBundleRoot"]);
    }

    [Fact]
    public void TheSources_AreTrimmed_AndBlanksDropped_TheRegistryTrimmed()
    {
        var record = new DeploymentContent().WithBundles(" cr.example.test ", [" plugins ", "", "  ", "education"]);

        Assert.Equal("cr.example.test", DeploymentPortalConfig.BundleRegistry(record));
        Assert.Equal(["plugins", "education"], DeploymentPortalConfig.BundleSources(record));
    }

    [Fact]
    public void ARecordThatFetchesNoBundles_RendersNoPreWarmRoot()
    {
        // Negative controls: no block, and a block whose registry is blank — both mean "no fetch",
        // and the pre-warm root must not appear, so every existing record renders as before.
        var none = DeploymentRecordJson.Read(BundlesJson.Replace("\"registry\": \"cr.example.test\"", "\"registry\": \"  \""))!;
        var absent = new DeploymentContent { Host = "client-test.example.com" };

        foreach (var record in new[] { none, absent })
        {
            Assert.Null(DeploymentPortalConfig.BundleRegistry(record));
            Assert.Null(DeploymentPortalConfig.BundleRoot(record));
            Assert.False(DeploymentPortalConfig.PortalConfig(record).ContainsKey("PreWarm__PrebuiltBundleRoot"));
        }
    }

    [Fact]
    public void TheTypedRoot_WinsOverAnExtraNamingTheSameKey()
    {
        // The pre-warm root has ONE home once bundles are declared: an extra stating another
        // directory would split the shelf the init container fills from the one the portal reads.
        var record = new DeploymentContent()
            .WithBundles("cr.example.test", ["plugins"])
            .WithPortalConfig("PreWarm__PrebuiltBundleRoot", "/somewhere/else");

        Assert.Equal(BundlesSpec.DefaultRoot, DeploymentPortalConfig.PortalConfig(record)["PreWarm__PrebuiltBundleRoot"]);
    }
}
