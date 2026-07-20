using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// Pins <see cref="PluginCatalogOptions.EffectiveRegistries"/> — the resolution of the consumer's
/// registry list: the multi-registry <c>PluginCatalog:Registries</c> list wins when configured,
/// the legacy single <c>RegistryUrl</c>/<c>RegistryRef</c> pair keeps working as a one-entry
/// fallback, and nothing configured yields an empty list (the admin tab shows its
/// "not configured" note).
/// </summary>
public class PluginCatalogOptionsTest
{
    [Fact]
    public void NothingConfigured_YieldsEmpty()
    {
        Assert.Empty(new PluginCatalogOptions().EffectiveRegistries);
    }

    [Fact]
    public void LegacySingleRegistry_FoldsIntoOneEntry()
    {
        var options = new PluginCatalogOptions
        {
            RegistryUrl = "https://memex.meshweaver.cloud",
            RegistryRef = "main",
            RegistryToken = "tok-instance-1",
        };

        var registry = Assert.Single(options.EffectiveRegistries);
        Assert.Equal("https://memex.meshweaver.cloud", registry.Url);
        Assert.Equal("main", registry.Ref);
        Assert.Equal("tok-instance-1", registry.Token);
    }

    [Fact]
    public void RegistriesList_WinsOverLegacyPair()
    {
        var options = new PluginCatalogOptions
        {
            RegistryUrl = "https://legacy.example",
            Registries =
            [
                new PluginRegistryReference { Name = "Plugins", Url = "https://memex.meshweaver.cloud" },
                new PluginRegistryReference { Name = "Education", Url = "https://edu.example", Ref = "main" },
            ],
        };

        Assert.Collection(options.EffectiveRegistries,
            r => Assert.Equal("https://memex.meshweaver.cloud", r.Url),
            r =>
            {
                Assert.Equal("https://edu.example", r.Url);
                Assert.Equal("Education", r.Name);
                Assert.Equal("main", r.Ref);
            });
    }

    [Fact]
    public void RegistriesList_BlankUrlEntriesAreDropped_FallingBackToLegacyWhenNoneRemain()
    {
        var options = new PluginCatalogOptions
        {
            RegistryUrl = "https://legacy.example",
            Registries = [new PluginRegistryReference { Name = "misconfigured", Url = " " }],
        };

        var registry = Assert.Single(options.EffectiveRegistries);
        Assert.Equal("https://legacy.example", registry.Url);
    }
}
