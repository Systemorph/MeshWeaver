using MeshWeaver.Deployment;
using Xunit;

namespace MeshWeaver.Deployment.Contract.Test;

/// <summary>
/// A plugin registry's registration seed (<c>PluginCatalog:DefaultGrants</c>) is a typed field on the
/// record (Memex#116). Before it, the seed lived only in a hand-edited Helm overlay: no renderer read it
/// from the record, so a re-render dropped it and the deploy-drift sentinel reported it as never
/// delivered. Pinned here: the field reads from the record's JSON, every route that carries the catalog
/// wiring renders it, it is normalised once, and a record that does not state it renders nothing.
/// </summary>
public class DefaultGrantsRecordTest
{
    private const string RegistryJson = """
        {
          "$type": "DeploymentContent",
          "host": "registry.example.com",
          "isPluginRegistry": true,
          "pluginRepos": [ { "name": "Plugins", "url": "https://github.com/Systemorph/MeshWeaver.Plugins", "isRegistrySource": true } ],
          "defaultGrants": [ "Plugins/*" ]
        }
        """;

    [Fact]
    public void TheRecordsJson_CarriesDefaultGrants_AndWritesThemBack()
    {
        var record = DeploymentRecordJson.Read(RegistryJson)!;

        Assert.Equal(["Plugins/*"], record.DefaultGrants);
        Assert.Contains("\"defaultGrants\"", DeploymentRecordJson.Write(record));
    }

    [Fact]
    public void ARegistryRecordWithDefaultGrants_RendersThemIntoTheBootCatalogConfig()
    {
        var record = DeploymentRecordJson.Read(RegistryJson)!;

        var entries = DeploymentPortalConfig.BootConfigurationEntries(record);

        Assert.Contains("PluginCatalog:DefaultGrants:0=Plugins/*", entries);
    }

    [Fact]
    public void TheAspireRoute_RendersDefaultGrantsAsPortalKeys_TheHelmPortalConfigDoesNot()
    {
        var record = new DeploymentContent { Host = "registry.example.com", IsPluginRegistry = true }
            .DefaultGrants("Plugins/*", "Education/Edu");

        var aspire = DeploymentPortalConfig.PortalConfig(record, PortalConfigOptions.Aspire("http://localhost:8080"));
        var helm = DeploymentPortalConfig.PortalConfig(record, PortalConfigOptions.Helm);

        Assert.Equal("Plugins/*", aspire["PluginCatalog__DefaultGrants__0"]);
        Assert.Equal("Education/Edu", aspire["PluginCatalog__DefaultGrants__1"]);
        // Helm delivers the catalog wiring through the chart's pluginCatalog values (HelmValues),
        // never the portal ConfigMap derivation — the same split as every other PluginCatalog key.
        Assert.DoesNotContain(helm.Keys, k => k.StartsWith("PluginCatalog__DefaultGrants", StringComparison.Ordinal));
    }

    [Fact]
    public void ARecordWithoutDefaultGrants_RendersNoSeed_OnAnyRoute()
    {
        // Negative control: the same registry, no seed stated.
        var record = DeploymentRecordJson.Read(RegistryJson.Replace("\"defaultGrants\": [ \"Plugins/*\" ]", "\"owner\": \"x\""))!;
        Assert.Empty(record.DefaultGrants);

        Assert.DoesNotContain(DeploymentPortalConfig.BootConfigurationEntries(record),
            e => e.StartsWith("PluginCatalog:DefaultGrants", StringComparison.Ordinal));
        Assert.DoesNotContain(DeploymentPortalConfig.PortalConfig(record, PortalConfigOptions.Aspire("http://localhost:8080")).Keys,
            k => k.StartsWith("PluginCatalog__DefaultGrants", StringComparison.Ordinal));
    }

    [Fact]
    public void Patterns_AreTrimmed_BlanksDropped_DuplicatesKeptOnce_InDeclaredOrder()
    {
        Assert.Equal(
            ["Plugins/*", "Education/*"],
            DeploymentPortalConfig.DefaultGrantPatterns([" Plugins/* ", "", "  ", "Education/*", "plugins/*"]));
        Assert.Empty(DeploymentPortalConfig.DefaultGrantPatterns(null));
        Assert.Equal(
            ["PluginCatalog:DefaultGrants:0=Plugins/*"],
            DeploymentPortalConfig.DefaultGrantEntries([" Plugins/* ", "plugins/*"]));
    }
}
