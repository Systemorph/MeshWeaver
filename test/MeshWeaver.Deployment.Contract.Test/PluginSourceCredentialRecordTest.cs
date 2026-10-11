using System.Collections.Immutable;
using MeshWeaver.Deployment;
using Xunit;

namespace MeshWeaver.Deployment.Contract.Test;

/// <summary>
/// A git source's OWN credential, on the record: <c>secretName</c> (a vault object's NAME) or
/// <c>installationOwner</c> (an account's GitHub App installation). Before this,
/// <see cref="PluginRepoMount.SecretName"/> was on the record and rendered by nothing, so a source
/// in a second organisation could be recorded and still not be read.
///
/// <para>Pinned here: the mount renders the configuration key the token is READ from (never the
/// vault name, never a value), the vault mapping is derived from the same mount so the two cannot
/// disagree, the key follows the source's NAME (not its index), and a mount that states no
/// credential renders exactly what it rendered before.</para>
/// </summary>
public class PluginSourceCredentialRecordTest
{
    private static readonly PluginRepoMount Plugins = new()
    {
        Name = "Plugins", Url = "https://github.com/Systemorph/MeshWeaver.Plugins", IsRegistrySource = true,
    };

    private static readonly PluginRepoMount Extensions = new()
    {
        Name = "Extensions", Url = "https://github.com/Northwind/extensions", IsRegistrySource = true,
        SecretName = "memex-PluginSource-Extensions-Token",
    };

    private static readonly PluginRepoMount Fabrikam = new()
    {
        Name = "Fabrikam", Url = "https://github.com/Fabrikam/modules", IsRegistrySource = true,
        InstallationOwner = " Fabrikam ",
    };

    [Fact]
    public void ASourceNamingASecret_RendersTheKeyItsTokenIsReadFrom_NotTheVaultName()
    {
        var entries = DeploymentPortalConfig.ConfigurationEntries([Plugins, Extensions, Fabrikam], null);

        Assert.Contains("PluginCatalog:Sources:1:TokenKey=PluginCatalog:SourceTokens:Extensions", entries);
        Assert.Contains("PluginCatalog:Sources:2:InstallationOwner=Fabrikam", entries);
        Assert.DoesNotContain(entries, e => e.Contains("memex-PluginSource-Extensions-Token", StringComparison.Ordinal));
        // The undeclared source renders no credential key at all — an empty TokenKey would be a
        // DECLARED key that is empty, which the portal refuses.
        Assert.DoesNotContain(entries, e => e.StartsWith("PluginCatalog:Sources:0:TokenKey", StringComparison.Ordinal));
        Assert.DoesNotContain(entries, e => e.StartsWith("PluginCatalog:Sources:0:InstallationOwner", StringComparison.Ordinal));
    }

    [Fact]
    public void AMountWithoutACredential_RendersExactlyWhatItRenderedBefore()
    {
        Assert.Equal(
            [
                "PluginCatalog:Sources:0:Name=Plugins",
                "PluginCatalog:Sources:0:RepoPath=https://github.com/Systemorph/MeshWeaver.Plugins",
                "PluginCatalog:Sources:0:Ref=main",
            ],
            DeploymentPortalConfig.ConfigurationEntries([Plugins], null));
        Assert.Empty(DeploymentPortalConfig.SourceCredentialSecrets([Plugins]));
    }

    [Fact]
    public void TheVaultMapping_IsDerivedFromTheMount_AndLandsOnTheKeyTheSourceReads()
    {
        var secret = Assert.Single(DeploymentPortalConfig.SourceCredentialSecrets([Plugins, Extensions, Fabrikam]));

        Assert.Equal("memex-PluginSource-Extensions-Token", secret.VaultSecret);
        Assert.Equal("PluginCatalog__SourceTokens__Extensions", secret.Key);
        // The environment key and the configuration key the source is told to read are ONE key.
        Assert.Equal(Extensions.SourceTokenConfigKey, secret.Key.Replace("__", ":"));
    }

    [Fact]
    public void TheTokenKeyFollowsTheSourcesName_NotItsPosition()
    {
        var first = DeploymentPortalConfig.ConfigurationEntries([Extensions, Plugins], null);
        var second = DeploymentPortalConfig.ConfigurationEntries([Plugins, Extensions], null);

        Assert.Contains("PluginCatalog:Sources:0:TokenKey=PluginCatalog:SourceTokens:Extensions", first);
        Assert.Contains("PluginCatalog:Sources:1:TokenKey=PluginCatalog:SourceTokens:Extensions", second);
        Assert.Equal(
            DeploymentPortalConfig.SourceCredentialSecrets([Extensions, Plugins]),
            DeploymentPortalConfig.SourceCredentialSecrets([Plugins, Extensions]));
    }

    [Theory]
    [InlineData("MeshWeaver.Plugins", "MeshWeaver_Plugins")]
    [InlineData("a--b::c", "a_b_c")]
    [InlineData(" Extensions ", "Extensions")]
    public void TheNameIsOneKeySegment(string name, string segment)
    {
        var mount = Extensions with { Name = name };

        Assert.Equal(segment, mount.KeySegment);
        Assert.Equal($"PluginCatalog__SourceTokens__{segment}", mount.SourceTokenEnvKey);
    }

    [Fact]
    public void AConsumerMountsSecretName_IsNotAGitToken()
    {
        var consumer = new PluginRepoMount
        {
            Name = "Plugins", Url = "https://memex.meshweaver.cloud", SecretName = "memex-PluginCatalog-RegistryToken",
        };

        Assert.Null(consumer.SourceTokenConfigKey);
        Assert.Empty(DeploymentPortalConfig.SourceCredentialSecrets([consumer]));
        Assert.DoesNotContain(
            DeploymentPortalConfig.ConfigurationEntries([consumer], null),
            e => e.Contains("TokenKey", StringComparison.Ordinal));
    }

    [Fact]
    public void TwoCredentialsOnOneSource_AndAnInstallationOnAConsumer_AreProblems_NotGuesses()
    {
        var both = Extensions with { InstallationOwner = "Northwind" };
        var consumer = new PluginRepoMount
        {
            Name = "Registry", Url = "https://memex.meshweaver.cloud", InstallationOwner = "Systemorph",
        };

        var (usable, problems) = DeploymentPortalConfig.Validate([both, consumer, Plugins]);

        Assert.Equal([Plugins], usable);
        Assert.Contains(problems, p => p.Contains("'Extensions'", StringComparison.Ordinal)
                                       && p.Contains("both secretName and installationOwner", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("'Registry'", StringComparison.Ordinal)
                                       && p.Contains("not a registry source", StringComparison.Ordinal));
    }

    /// <summary>
    /// Two different names can derive the SAME key segment. Rendering both would hand one
    /// repository the other's vault token, so the second is a problem and renders nothing.
    /// </summary>
    [Fact]
    public void TwoSourcesWhoseNamesKeyTheSameToken_AreNotBothRendered()
    {
        var dashed = Extensions with { Name = "a-b", SecretName = "vault-one" };
        var underscored = Extensions with { Name = "a_b", Url = "https://github.com/Northwind/other", SecretName = "vault-two" };

        var (usable, problems) = DeploymentPortalConfig.Validate([dashed, underscored]);

        Assert.Equal([dashed], usable);
        Assert.Contains(problems, p => p.Contains("'a_b'", StringComparison.Ordinal)
                                       && p.Contains("'a-b'", StringComparison.Ordinal)
                                       && p.Contains("PluginCatalog:SourceTokens:a_b", StringComparison.Ordinal));
        var secret = Assert.Single(DeploymentPortalConfig.SourceCredentialSecrets([dashed, underscored]));
        Assert.Equal("vault-one", secret.VaultSecret);
    }

    [Fact]
    public void TheRecordCarriesBothFieldsThroughJson()
    {
        var record = new DeploymentContent { PluginRepos = ImmutableList.Create(Extensions, Fabrikam with { InstallationOwner = "Fabrikam" }) };

        var json = System.Text.Json.JsonSerializer.Serialize(record, DeploymentRecordJson.Options);
        var back = System.Text.Json.JsonSerializer.Deserialize<DeploymentContent>(json, DeploymentRecordJson.Options)!;

        Assert.Contains("\"secretName\":\"memex-PluginSource-Extensions-Token\"", json.Replace(" ", ""), StringComparison.Ordinal);
        Assert.Contains("\"installationOwner\":\"Fabrikam\"", json.Replace(" ", ""), StringComparison.Ordinal);
        Assert.Equal("memex-PluginSource-Extensions-Token", back.PluginRepos[0].SecretName);
        Assert.Equal("Fabrikam", back.PluginRepos[1].InstallationOwner);
    }
}
