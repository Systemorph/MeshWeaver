using MeshWeaver.Mesh.Security;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// Pins the registration-bootstrap-key contract (<c>mwr_…</c>) and the credential-path slugging of
/// first-startup auto-registration. The prefix discipline is the security property: an instance
/// validator must never accept a bootstrap key or vice versa, and both must be identifiable when
/// one turns up in a log.
/// </summary>
public class RegistrationKeysTest
{
    [Fact]
    public void GeneratedKey_CarriesTheBootstrapPrefix_AndIsUnique()
    {
        var a = RegistrationKeys.Generate();
        var b = RegistrationKeys.Generate();
        Assert.StartsWith("mwr_", a);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void BootstrapAndInstancePrefixes_AreDisjoint()
    {
        // Neither validator can accept the other's key: the shape check alone already separates them.
        Assert.True(RegistrationKeys.HasKeyShape(RegistrationKeys.Generate()));
        Assert.False(RegistrationKeys.HasKeyShape(InstanceKeys.Generate()));
        Assert.Null(InstanceKeys.ExtractKey($"Bearer {RegistrationKeys.Generate()}"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("mw_personal")]
    [InlineData("mwi_instance")]
    [InlineData("bogus")]
    public void NonBootstrapShapes_AreRejected(string? raw)
        => Assert.False(RegistrationKeys.HasKeyShape(raw));

    [Fact]
    public void Usability_EnforcesRevocationAndExpiry()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(new RegistrationKey().IsUsable(now));
        Assert.False(new RegistrationKey { IsRevoked = true }.IsUsable(now));
        Assert.False(new RegistrationKey { ExpiresAt = now.AddSeconds(-1) }.IsUsable(now));
        Assert.True(new RegistrationKey { ExpiresAt = now.AddDays(1) }.IsUsable(now));
    }

    [Theory]
    [InlineData("https://memex.meshweaver.cloud", "Admin/PluginRegistryCredential/memex-meshweaver-cloud")]
    [InlineData("https://memex.meshweaver.cloud/", "Admin/PluginRegistryCredential/memex-meshweaver-cloud")]
    [InlineData("http://memex.meshweaver.cloud", "Admin/PluginRegistryCredential/memex-meshweaver-cloud")]
    [InlineData("http://localhost:5022", "Admin/PluginRegistryCredential/localhost")]
    [InlineData("", "Admin/PluginRegistryCredential/registry")]
    public void CredentialPath_KeysByHost_SoUrlVariantsShareOneCredential(string url, string expected)
        => Assert.Equal(expected, PluginRegistryCredentials.Path(url));
}
