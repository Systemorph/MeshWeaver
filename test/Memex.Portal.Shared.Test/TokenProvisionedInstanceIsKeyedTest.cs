using MeshWeaver.PluginCatalog;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// #5245: an installation provisioned with a registry TOKEN (no registration key) was classified
/// as the OPEN lane, so Settings ▸ Instance registration offered it a consent form and read
/// "Awaiting consent — not registered" forever — auto-registration skips a token-configured
/// installation ("the explicit token wins"), so no credential is ever stored for the page to find.
/// The consent step belongs to the open lane only; an operator-configured token is operator
/// provisioning just as a registration key is. Every case goes through the production
/// <c>InstanceConsentService.Target(options)</c> — registry selection, legacy-token attribution and
/// the keyed rule together — never a copy of its wiring.
/// </summary>
public class TokenProvisionedInstanceIsKeyedTest
{
    private const string RegistryUrl = "https://memex.meshweaver.cloud";

    private static bool Keyed(PluginCatalogOptions options)
    {
        var target = InstanceConsentService.Target(options);
        Assert.NotNull(target);
        return target.Value.Keyed;
    }

    [Fact]
    public void ALegacyRegistryToken_IsOperatorProvisioning()
    {
        // The partnerre-memex shape: PluginCatalog__RegistryUrl + PluginCatalog__RegistryToken from
        // the client's Key Vault, an instance id, and no BootstrapKey.
        var options = new PluginCatalogOptions
        {
            RegistryUrl = RegistryUrl,
            RegistryToken = "mwi_operator-issued",
            InstanceId = "partnerre-memex",
        };
        Assert.True(Keyed(options), "a configured registry token is operator provisioning — no consent step applies");
    }

    [Fact]
    public void ANamedRegistrysOwnToken_IsOperatorProvisioning()
    {
        var options = new PluginCatalogOptions { InstanceId = "partnerre-memex" };
        options.Registries.Add(new PluginRegistryReference { Url = RegistryUrl, Token = "mwi_operator-issued" });
        Assert.True(Keyed(options));
    }

    [Fact]
    public void ARegistrationKey_IsOperatorProvisioning()
    {
        var options = new PluginCatalogOptions
        {
            RegistryUrl = RegistryUrl, InstanceId = "acme", BootstrapKey = "mwb_bootstrap",
        };
        Assert.True(Keyed(options));
    }

    [Fact]
    public void NeitherKeyNorToken_IsTheOpenLane()
    {
        // The control that keeps the consent form where it belongs: an instance id alone.
        var options = new PluginCatalogOptions { RegistryUrl = RegistryUrl, InstanceId = "acme" };
        Assert.False(Keyed(options),
            "an installation with neither a key nor a token registers through the consent-gated open lane");
    }
}
