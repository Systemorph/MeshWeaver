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
    /// <summary>
    /// A new instance ASKS FOR ITS CERTIFICATE without anyone saying so. The issuer is a record
    /// default, not something an overlay writes as an annotation: fabrikam.example.com had the
    /// annotation in its checked-in overlay and not on its record, a Provision renders from the
    /// record, and the host was served another instance's certificate for nine hours
    /// (2026-09-15). The default is what makes that shape unreachable for the next instance.
    /// </summary>
    [Fact]
    public void AnInstanceWithATlsSecretAsksForItsCertificateByDefault()
    {
        // Declared with nothing but a host and a TLS secret.
        var declared = new DeploymentContent()
            .WithHost("portal.example.com")
            .WithIngress(className: "nginx", tlsSecret: "portal-tls");
        Assert.Equal(IngressSpec.DefaultClusterIssuer, declared.Ingress!.ClusterIssuer);

        // The bare shape carries it too — a record that never calls WithIngress, and a record
        // written before the field existed, both deserialize onto this initializer.
        Assert.Equal(IngressSpec.DefaultClusterIssuer, new IngressSpec().ClusterIssuer);
        var old = DeploymentRecordJson.Read("""
            {"host":"portal.example.com","ingress":{"className":"nginx","tlsSecret":"portal-tls",
             "annotations":{"nginx.ingress.kubernetes.io/proxy-buffer-size":"16k"}}}
            """);
        Assert.Equal(IngressSpec.DefaultClusterIssuer, old!.Ingress!.ClusterIssuer);
    }

    /// <summary>
    /// The default never overrides a decision. An explicit issuer stands, and <c>none</c> — the
    /// opt-out for a Secret created by other means — survives the round trip rather than being
    /// helpfully replaced by the fleet's issuer.
    /// </summary>
    [Fact]
    public void AnExplicitIssuerStandsAndNoneIsKept()
    {
        var staging = new DeploymentContent().WithIngress(tlsSecret: "portal-tls", clusterIssuer: "letsencrypt-staging");
        Assert.Equal("letsencrypt-staging", staging.Ingress!.ClusterIssuer);

        var optedOut = new DeploymentContent().WithIngress(tlsSecret: "portal-tls", clusterIssuer: IngressSpec.NoClusterIssuer);
        Assert.Equal(IngressSpec.NoClusterIssuer, optedOut.Ingress!.ClusterIssuer);
        var reread = DeploymentRecordJson.Read(DeploymentRecordJson.Write(optedOut));
        Assert.Equal(IngressSpec.NoClusterIssuer, reread!.Ingress!.ClusterIssuer);

        // And a later call that says nothing about the issuer leaves the decision alone.
        Assert.Equal("letsencrypt-staging", staging.WithIngress(className: "nginx").Ingress!.ClusterIssuer);
    }

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

    /// <summary>
    /// The EU-only AI keys a record carries reach the portal config under exactly the names the
    /// portal reads and the chart renders: the EU route's section (<c>OpenRouterEU__*</c>), the
    /// instance requirement (<c>AI__RequiredDataResidency</c>) and a section's own processing marks
    /// (<c>{Section}__DataResidency</c> / <c>__DataRetention</c>). A record that states none of
    /// them renders none — every existing record renders byte-identically.
    /// </summary>
    [Fact]
    public void TheEuAiKeysRenderUnderThePortalsNames()
    {
        var record = new DeploymentContent().WithAi(a => a
            .OpenRouter(["z-ai/glm-5.3"])
            .OpenRouterEU(["z-ai/glm-5.3", "mistralai/mistral-medium-3"])
            .RequiredDataResidency(" Eu ")
            .Tiers(heavy: "Provider/OpenRouterEU/z-ai/glm-5.3"));
        record = record with
        {
            Ai = record.Ai! with
            {
                Anthropic = new ModelProvider { Models = ["claude-sonnet-5"], DataResidency = "Eu", DataRetention = "ZDR" },
                // An Order / Enabled stated on the EU route anyway renders NOTHING — the chart has no
                // OpenRouterEU__Order and no feature flag reader, and Memex's key coverage would red.
                OpenRouterEU = record.Ai.OpenRouterEU! with { Order = 3, Enabled = true, DataResidency = "Eu" },
            },
        };

        var c = DeploymentPortalConfig.PortalConfig(record, PortalConfigOptions.Helm);

        Assert.Equal("z-ai/glm-5.3", c["OpenRouterEU__Models__0"]);
        Assert.Equal("mistralai/mistral-medium-3", c["OpenRouterEU__Models__1"]);
        Assert.False(c.ContainsKey("OpenRouterEU__Order"), "no Order key exists for the EU route");
        Assert.False(c.ContainsKey("Features__Ai__Providers__OpenRouterEU"), "no feature flag is rendered for the EU route");
        Assert.Equal("Eu", c["OpenRouterEU__DataResidency"]);
        Assert.False(c.ContainsKey("OpenRouterEU__Endpoint"), "no override stated — the portal's EU default stands");
        Assert.Equal("Eu", c["AI__RequiredDataResidency"]);
        Assert.Equal("Eu", c["Anthropic__DataResidency"]);
        Assert.Equal("ZDR", c["Anthropic__DataRetention"]);
        Assert.Equal("Provider/OpenRouterEU/z-ai/glm-5.3", c["ModelTier__Heavy"]);
        Assert.False(c.ContainsKey("OpenRouter__DataResidency"), "an unstated mark renders nothing");

        var endpoint = new DeploymentContent().WithAi(a => a.OpenRouterEU([], endpoint: " https://eu.openrouter.example/api/v1 "));
        Assert.Equal("https://eu.openrouter.example/api/v1",
            DeploymentPortalConfig.PortalConfig(endpoint, PortalConfigOptions.Helm)["OpenRouterEU__Endpoint"]);

        // Blank renders nothing — the chart's contract for these keys: a blank endpoint is the EU
        // default, a blank model slot names nothing, and neither becomes an empty key.
        var blanks = new DeploymentContent().WithAi(a => a.OpenRouterEU(["", " z-ai/glm-5.3 ", "  "], endpoint: "  "));
        var bc = DeploymentPortalConfig.PortalConfig(blanks, PortalConfigOptions.Helm);
        Assert.False(bc.ContainsKey("OpenRouterEU__Endpoint"), "a blank endpoint renders no key");
        Assert.Equal("z-ai/glm-5.3", bc["OpenRouterEU__Models__0"]);
        Assert.False(bc.ContainsKey("OpenRouterEU__Models__1"), "blank slots render no key");

        var plain = DeploymentPortalConfig.PortalConfig(new DeploymentContent().WithAi(a => a.OpenRouter(["z-ai/glm-5.3"])), PortalConfigOptions.Helm);
        Assert.DoesNotContain(plain.Keys, k => k.StartsWith("OpenRouterEU__", StringComparison.Ordinal)
                                               || k.EndsWith("__DataResidency", StringComparison.Ordinal)
                                               || k.EndsWith("__DataRetention", StringComparison.Ordinal)
                                               || k == "AI__RequiredDataResidency");

        // The record round-trips through its JSON form with the new fields intact.
        var back = DeploymentRecordJson.Read(DeploymentRecordJson.Write(record))!;
        Assert.Equal("Eu", back.Ai!.RequiredDataResidency);
        Assert.Equal(2, back.Ai.OpenRouterEU!.Models.Count);
        Assert.Equal("ZDR", back.Ai.Anthropic!.DataRetention);
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

        // The key binds to an enum on the pod: the renderer emits the canonical casing and refuses
        // a misspelling by name, instead of letting it abort the new replica's host.
        var lower = DeploymentPortalConfig.PortalConfig(new DeploymentContent().WithUpdatePolicy(" stable "), PortalConfigOptions.Helm);
        Assert.Equal("Stable", lower["SelfUpdate__DefaultPolicy"]);
        var misspelled = new DeploymentContent().WithUpdatePolicy("Continuos");
        var refusal = Assert.Throws<InvalidOperationException>(() => DeploymentPortalConfig.PortalConfig(misspelled, PortalConfigOptions.Helm));
        Assert.Contains("Continuos", refusal.Message);
        Assert.Contains("SelfUpdate__DefaultPolicy", refusal.Message);
    }
}
