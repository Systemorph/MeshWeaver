#pragma warning disable CS1591

using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// The legacy-token fallback decision (<see cref="RegistryTokenResolver.LegacyTokenFallback"/>):
/// a named registry with no token of its own inherits the legacy
/// <c>PluginCatalog:RegistryToken</c> ONLY when attribution is unambiguous — it is the legacy URL
/// or the sole configured registry. Without the fallback, upgrading a consumer to the named
/// Registries shape silently drops auth (every catalog read 401s — systemorph, 2026-08-20);
/// without the guard, a token could be sent to a host it was not issued for.
/// </summary>
public class RegistryTokenFallbackTest
{
    private static PluginCatalogOptions Options(
        string legacyUrl = "", string legacyToken = "mwi_legacy",
        params PluginRegistryReference[] registries) => new()
    {
        RegistryUrl = legacyUrl,
        RegistryToken = legacyToken,
        Registries = [.. registries],
    };

    [Fact]
    public void SoleNamedRegistry_WithoutOwnToken_InheritsTheLegacyToken()
    {
        var reg = new PluginRegistryReference { Name = "Plugins", Url = "https://reg.example" };
        Assert.Equal("mwi_legacy",
            RegistryTokenResolver.LegacyTokenFallback(Options(registries: reg), reg));
    }

    [Fact]
    public void MatchingLegacyUrl_InheritsTheLegacyToken_EvenAmongSeveral()
    {
        var plugins = new PluginRegistryReference { Name = "Plugins", Url = "https://reg.example" };
        var edu = new PluginRegistryReference { Name = "Edu", Url = "https://other.example" };
        var options = Options(legacyUrl: "https://reg.example/", registries: [plugins, edu]);
        Assert.Equal("mwi_legacy", RegistryTokenResolver.LegacyTokenFallback(options, plugins));
    }

    [Fact]
    public void AmbiguousRegistry_DoesNotInheritTheLegacyToken()
    {
        var plugins = new PluginRegistryReference { Name = "Plugins", Url = "https://reg.example" };
        var edu = new PluginRegistryReference { Name = "Edu", Url = "https://other.example" };
        var options = Options(registries: [plugins, edu]);
        Assert.Null(RegistryTokenResolver.LegacyTokenFallback(options, edu));
        Assert.Null(RegistryTokenResolver.LegacyTokenFallback(options, plugins));
    }

    [Fact]
    public void OwnToken_SuppressesTheFallback()
    {
        var reg = new PluginRegistryReference
        {
            Name = "Plugins", Url = "https://reg.example", Token = "mwi_own",
        };
        Assert.Null(RegistryTokenResolver.LegacyTokenFallback(Options(registries: reg), reg));
    }

    [Fact]
    public void WithLegacyTokens_StampsOnlyTheQualifyingReferences()
    {
        var plugins = new PluginRegistryReference { Name = "Plugins", Url = "https://reg.example" };
        var options = Options(registries: plugins);
        var stamped = RegistryTokenResolver.WithLegacyTokens(options, options.Registries);
        Assert.Equal("mwi_legacy", stamped[0].Token);
        Assert.Equal("Plugins", stamped[0].Name);
        // The original reference is never mutated — the stamp is a copy.
        Assert.Equal("", plugins.Token);
    }

    [Fact]
    public void NoLegacyToken_NoFallback()
    {
        var reg = new PluginRegistryReference { Name = "Plugins", Url = "https://reg.example" };
        Assert.Null(RegistryTokenResolver.LegacyTokenFallback(
            Options(legacyToken: "", registries: reg), reg));
    }
}
