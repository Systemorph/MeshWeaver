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
    public void TheActionsExecutorReachesTheConfigWithTheOperatorJobOff()
    {
        // Plugins#1738: the control instance runs its actions through aks-ops.yml and the GitHub App,
        // with the in-cluster Job disabled. The switch must therefore render on its own, and the
        // Job flag must NOT come along with it.
        var record = new DeploymentContent().WithOperatorExecutor(" actions ", "rbuergi");
        Assert.False(record.Operator!.Enabled);
        Assert.Equal("Actions", record.Operator.Executor);
        Assert.Equal("rbuergi", record.Operator.Maintainer);

        foreach (var options in new[] { PortalConfigOptions.Helm, PortalConfigOptions.Aspire("http://localhost:8080") })
        {
            var config = DeploymentPortalConfig.PortalConfig(record, options);
            Assert.Equal("Actions", config["Hosting__Operator__Executor"]);
            Assert.Equal("rbuergi", config["Hosting__Operator__Maintainer"]);
            Assert.False(config.ContainsKey("Hosting__Operator__Enabled"), "the executor switch must not arm the operator Job");
        }

        // Switching back keeps the maintainer, and an operator without an executor emits no key.
        Assert.Equal("rbuergi", record.WithOperatorExecutor("Job").Operator!.Maintainer);
        var jobOnly = DeploymentPortalConfig.PortalConfig(new DeploymentContent().WithOperator(), PortalConfigOptions.Helm);
        Assert.False(jobOnly.ContainsKey("Hosting__Operator__Executor"), "blank means Job, and the chart's default says so");
        Assert.False(jobOnly.ContainsKey("Hosting__Operator__Maintainer"));

        // The portal reads anything but "Actions" as Job, so a misspelling must not reach it.
        Assert.Throws<ArgumentException>(() => new DeploymentContent().WithOperatorExecutor("Action"));

        // The maintainer is trimmed on the way in; null keeps it, blank clears it.
        Assert.Equal("rbuergi", new DeploymentContent().WithOperatorExecutor("Actions", "  rbuergi ").Operator!.Maintainer);
        Assert.Null(record.WithOperatorExecutor("Actions", "  ").Operator!.Maintainer);

        // A record that never passed the transform (JSON, an initializer) renders the same way on
        // both renderers, and a misspelling fails closed on both rather than reaching the portal.
        var raw = new DeploymentContent { Operator = new HostingOperatorSpec { Executor = " actions ", Maintainer = " rbuergi " } };
        var misspelled = new DeploymentContent { Operator = new HostingOperatorSpec { Executor = "Action" } };
        foreach (var options in new[] { PortalConfigOptions.Helm, PortalConfigOptions.Aspire("http://localhost:8080") })
        {
            var config = DeploymentPortalConfig.PortalConfig(raw, options);
            Assert.Equal("Actions", config["Hosting__Operator__Executor"]);
            Assert.Equal("rbuergi", config["Hosting__Operator__Maintainer"]);
            Assert.Throws<InvalidOperationException>(() => DeploymentPortalConfig.PortalConfig(misspelled, options));
        }
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

    /// <summary>
    /// What a NEW instance starts with: the record's platform policy and pattern render as the
    /// self-updater's seed keys, and a record that says nothing renders neither — the chart's own
    /// default (Stable, no pattern) must not be narrowed or widened by silence.
    /// </summary>
    [Fact]
    public void UpdatePolicyAndPattern_SeedANewInstance_ThroughTheConfig()
    {
        var record = new DeploymentContent().WithUpdatePolicy("Continuous").WithUpdatePattern(" 3.0.0-ci* ");
        Assert.Equal("3.0.0-ci*", record.UpdatePattern);
        foreach (var options in new[] { PortalConfigOptions.Helm, PortalConfigOptions.Aspire("http://localhost:8080") })
        {
            var config = DeploymentPortalConfig.PortalConfig(record, options);
            Assert.Equal("Continuous", config["SelfUpdate__DefaultPolicy"]);
            Assert.Equal("3.0.0-ci*", config["SelfUpdate__DefaultPattern"]);

            var silent = DeploymentPortalConfig.PortalConfig(new DeploymentContent(), options);
            Assert.False(silent.ContainsKey("SelfUpdate__DefaultPolicy"), "an absent policy renders nothing — the image's own default stands");
            Assert.False(silent.ContainsKey("SelfUpdate__DefaultPattern"), "an absent pattern renders nothing");
        }
        Assert.Null(new DeploymentContent().WithUpdatePattern("  ").UpdatePattern);
    }
}
