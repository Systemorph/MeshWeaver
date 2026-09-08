using MeshWeaver.Deployment;
using Xunit;

namespace MeshWeaver.Deployment.Contract.Test;

/// <summary>
/// The fluent surface is a set of PURE transforms of the record: each call returns a new record,
/// leaves its input untouched, composes, and needs no Aspire — the same calls serve an AppHost,
/// the setup wizard, the template generator and a test. Defaults come from the record, never
/// from the builder.
/// </summary>
public class FluentBuilderTest
{
    [Fact]
    public void EveryTransformLeavesItsInputUntouched()
    {
        var bare = new DeploymentContent();
        var built = bare
            .WithHost("portal.example.com", "example.com")
            .WithNamespace("portal")
            .WithDatabase("portal", server: "pg-portal")
            .WithImage("ghcr.io/systemorph/memex-portal-ai", tag: "3.1.0")
            .WithPluginRepo("plugins", "https://github.com/Systemorph/MeshWeaver.Plugins", gitRef: "main")
            .PreInstall("MeshWeaver.Plugins/Hosting")
            .WithRequiredModule("MeshWeaver.Hosting.Postgres")
            .WithVolume("data", "/data", size: "128Gi")
            .WithReplicas(2)
            .WithKeyVault("kv-portal", "portal-")
            .WithKeyVaultSecrets(s => s.Map("Email__ClientSecret", "email-clientsecret"))
            .WithSignIn(microsoftClientId: "client", microsoftTenantId: "tenant")
            .WithEmail(mailboxAddress: "portal@example.com")
            .WithAi(a => a.OpenRouter(["anthropic/claude-sonnet-4"]).Tiers(heavy: "anthropic/claude-opus-4"))
            .WithOperator(ns: "hosting")
            .WithStartupProbe(periodSeconds: 10, failureThreshold: 60)
            .WithPortalConfig("Features__Onboarding__InvitationOnly", "true");

        // The input is what it was.
        Assert.Equal(DeploymentRecordJson.Write(new DeploymentContent()), DeploymentRecordJson.Write(bare));

        // And the output carries every call.
        Assert.Equal("portal.example.com", built.Host);
        Assert.Equal("portal", built.Namespace);
        Assert.Equal("portal", built.Database);
        Assert.Equal("3.1.0", built.PinnedImageTag);
        Assert.Single(built.PluginRepos);
        Assert.Equal(["MeshWeaver.Plugins/Hosting"], built.PreInstall);
        Assert.Contains("MeshWeaver.Hosting.Postgres", built.RequiredModules);
        Assert.Single(built.Volumes);
        Assert.Equal(2, built.Replicas);
        Assert.Equal("kv-portal", built.KeyVault);
        Assert.Contains(built.KeyVaultSecrets!.Secrets, s => s.Key == "Email__ClientSecret" && s.VaultSecret == "email-clientsecret");
        Assert.Equal("client", built.SignIn!.MicrosoftClientId);
        Assert.Equal("portal@example.com", built.Email!.MailboxAddress);
        Assert.Equal("anthropic/claude-opus-4", built.Ai!.Tiers!.Heavy);
        Assert.Equal("hosting", built.Operator!.Namespace);
        Assert.Equal(60, built.StartupProbe!.FailureThreshold);
        Assert.Equal("true", built.ExtraPortalConfig["Features__Onboarding__InvitationOnly"]);
    }

    [Fact]
    public void TheSameRecordRendersTheSameKeysForHelmAndForAspire()
    {
        // The two renderers differ in exactly the ways PortalConfigOptions names: Helm emits the
        // MEMEX_* database keys from the record (Aspire's Postgres resource injects its own) and
        // the in-cluster Mcp__BaseUrl (the adapter sets that key to the endpoint Aspire allocates,
        // so the derivation emits nothing for it — never a blank); Aspire emits the plugin-catalog
        // boot wiring as env (Helm hands the same entries to the operator's catalog config file).
        // Everything else is one derivation, key for key.
        var record = DeploymentRecordJson.ReadFile(Path.Combine(AppContext.BaseDirectory, "Fixtures", "memex.json"));
        var helm = DeploymentPortalConfig.PortalConfig(record, PortalConfigOptions.Helm);
        var aspire = DeploymentPortalConfig.PortalConfig(record, PortalConfigOptions.Aspire(mcpBaseUrl: null));

        var helmOnly = helm.Keys.Except(aspire.Keys).ToList();
        var aspireOnly = aspire.Keys.Except(helm.Keys).ToList();

        Assert.False(aspire.ContainsKey("Mcp__BaseUrl"), "the Aspire view must not emit a blank Mcp__BaseUrl — the adapter owns that key");
        Assert.True(helmOnly.All(k => k.StartsWith("MEMEX_", StringComparison.Ordinal) || k == "Mcp__BaseUrl"),
            "keys Helm renders and Aspire does not, beyond the database keys and the in-cluster MCP URL: " + string.Join(", ", helmOnly));
        Assert.True(aspireOnly.All(k => k.StartsWith("PluginCatalog__", StringComparison.Ordinal)),
            "keys Aspire renders and Helm does not, beyond the catalog boot wiring: " + string.Join(", ", aspireOnly));
        Assert.True(helmOnly.Count > 0 && aspireOnly.Count > 0, "the memex record should exercise both documented differences");

        var shared = helm.Keys.Intersect(aspire.Keys).ToList();
        var differing = shared.Where(k => helm[k] != aspire[k]).ToList();
        Assert.True(shared.Count > 40, $"only {shared.Count} shared keys — the memex record renders far more");
        Assert.True(differing.Count == 0,
            "shared keys whose value differs between the renderers: " + string.Join(", ", differing));
    }

    [Fact]
    public void ABareRecordStatesItsStorageAndPort()
    {
        // Rule (c): defaults live on the record. A bare record already renders a bootable portal.
        var config = DeploymentPortalConfig.PortalConfig(new DeploymentContent(), PortalConfigOptions.Aspire(mcpBaseUrl: null));
        Assert.Equal("PostgreSql", config["Graph__Storage__Type"]);
        Assert.Equal("Filesystem", config["Deployment__Backend"]);
        Assert.Equal("8080", config["ASPNETCORE_HTTP_PORTS"]);
    }
}
