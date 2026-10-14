using MeshWeaver.Deployment;
using Xunit;

namespace MeshWeaver.Deployment.Contract.Test;

/// <summary>
/// The single-registry catalog URL (<c>PluginCatalog:RegistryUrl</c>) is a typed field on the record
/// (Memex#116). Before it, the URL lived only in a hand-edited Helm overlay: no renderer read it from
/// the record, so a re-render dropped it and the deploy-drift sentinel reported it as never delivered.
/// A registry naming ITSELF is the case that motivated it — that makes the registry a consumer of its
/// own catalog, which is live behaviour, not dead configuration. Pinned here: the field reads from the
/// record's JSON, every route that carries the catalog wiring renders it, it is normalised once, and a
/// record that does not state it renders nothing.
/// </summary>
public class RegistryUrlRecordTest
{
    private const string RegistryJson = """
        {
          "$type": "DeploymentContent",
          "host": "registry.example.com",
          "isPluginRegistry": true,
          "pluginRepos": [ { "name": "Plugins", "url": "https://github.com/Systemorph/MeshWeaver.Plugins", "isRegistrySource": true } ],
          "registryUrl": "https://registry.example.com"
        }
        """;

    [Fact]
    public void TheRecordsJson_CarriesRegistryUrl_AndWritesItBack()
    {
        var record = DeploymentRecordJson.Read(RegistryJson)!;

        Assert.Equal("https://registry.example.com", record.RegistryUrl);
        Assert.Contains("\"registryUrl\"", DeploymentRecordJson.Write(record));
    }

    [Fact]
    public void ARecordWithRegistryUrl_RendersItIntoTheBootCatalogConfig()
    {
        var record = DeploymentRecordJson.Read(RegistryJson)!;

        Assert.Contains("PluginCatalog:RegistryUrl=https://registry.example.com",
            DeploymentPortalConfig.BootConfigurationEntries(record));
    }

    [Fact]
    public void TheAspireRoute_RendersRegistryUrlAsAPortalKey_TheHelmPortalConfigDoesNot()
    {
        var record = new DeploymentContent { Host = "registry.example.com", IsPluginRegistry = true }
            .WithRegistryUrl(" https://registry.example.com/ ");

        var aspire = DeploymentPortalConfig.PortalConfig(record, PortalConfigOptions.Aspire("http://localhost:8080"));
        var helm = DeploymentPortalConfig.PortalConfig(record, PortalConfigOptions.Helm);

        Assert.Equal("https://registry.example.com", aspire["PluginCatalog__RegistryUrl"]);
        // Helm delivers the catalog wiring through the chart's pluginCatalog values (HelmValues),
        // never the portal ConfigMap derivation — the same split as every other PluginCatalog key.
        Assert.False(helm.ContainsKey("PluginCatalog__RegistryUrl"));
    }

    [Fact]
    public void ARecordWithoutRegistryUrl_RendersNone_OnAnyRoute()
    {
        // Negative control: the same registry, no URL stated.
        var record = DeploymentRecordJson.Read(RegistryJson.Replace("\"registryUrl\": \"https://registry.example.com\"", "\"owner\": \"x\""))!;
        Assert.Null(record.RegistryUrl);

        Assert.DoesNotContain(DeploymentPortalConfig.BootConfigurationEntries(record),
            e => e.StartsWith("PluginCatalog:RegistryUrl", StringComparison.Ordinal));
        Assert.False(DeploymentPortalConfig.PortalConfig(record, PortalConfigOptions.Aspire("http://localhost:8080"))
            .ContainsKey("PluginCatalog__RegistryUrl"));
    }

    [Fact]
    public void TheUrl_IsTrimmed_WithoutATrailingSlash_AndBlankMeansNone()
    {
        Assert.Equal("https://r.example", DeploymentPortalConfig.NormalizedRegistryUrl("  https://r.example/  "));
        Assert.Null(DeploymentPortalConfig.NormalizedRegistryUrl(null));
        Assert.Null(DeploymentPortalConfig.NormalizedRegistryUrl("   "));
        Assert.Null(DeploymentPortalConfig.NormalizedRegistryUrl("/"));
        Assert.Equal(["PluginCatalog:RegistryUrl=https://r.example"], DeploymentPortalConfig.RegistryUrlEntries("https://r.example/"));
        Assert.Empty(DeploymentPortalConfig.RegistryUrlEntries(" "));
    }
}
