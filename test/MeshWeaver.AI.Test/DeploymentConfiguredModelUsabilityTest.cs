#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// A model the deployment can ACTUALLY serve must read as usable.
///
/// <para>A deployment that keeps a provider's key in configuration (<c>Anthropic__ApiKey</c>) serves
/// that provider's models perfectly well — every factory resolves
/// <c>resolution.ApiKey ?? configuration.ApiKey</c>. <see cref="ChatClientCredentialResolver"/> saw
/// only the <c>ModelProvider</c> node, so it answered
/// <see cref="ChatClientCredentialResolver.HasUsableCredential"/> = <c>false</c> for a model that
/// runs — and <c>AgentChatClient.ApplyStaleModelFallback</c> then SILENTLY swapped the user's
/// explicit pick for the deployment default (measured on <c>memex.systemorph.com</c>, 2026-08-21,
/// MeshWeaver#1965: every <c>claude-*</c> selection was served by another provider's factory).</para>
///
/// <para>🚨 <b>The mechanism changed under this test, deliberately, and the test did not.</b>
/// MeshWeaver#1983 made the resolver read the deployment's configuration as a fourth RUNG. That was
/// the right compensator for a seeder that could only ever run at node CREATION, and the wrong
/// long-term shape: it left a model's credential living in two places that cannot see each other.
/// MeshWeaver#1982 removes the rung and makes the SEED real instead —
/// <see cref="ProviderCredentialSeed"/> carries <c>{Section}:ApiKey</c> onto the node at boot, so
/// the node can answer. The user-visible claim below is unchanged and must stay true: a key that
/// lives only in the deployment's configuration makes its models usable.</para>
///
/// <para>The fixture is therefore the DB-synced <c>Provider</c> partition — the shape every portal
/// deployment runs, and the only one where a provider node exists to go stale. (On the in-memory
/// path <see cref="BuiltInLanguageModelProvider"/> re-projects configuration into the served node on
/// every read, so the case cannot arise.)</para>
/// </summary>
public class DeploymentConfiguredModelUsabilityTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Config section + provider name of a provider whose key the DEPLOYMENT holds.</summary>
    private const string KeyedSection = "AnthropicProbe";

    /// <summary>Config section + provider name of a provider nobody holds a key for.</summary>
    private const string KeylessSection = "GatewayProbe";

    /// <summary>🚨 Never logged, never in an assertion message — an echoed key is a rotated key.</summary>
    private const string DeploymentKey = "sk-ant-deployment-config-key";

    private const string KeyedEndpoint = "https://probe.example/anthropic/v1/messages";
    private const string KeylessEndpoint = "https://probe.example/gateway/v1/messages";

    private const string KeyedModelId = "claude-probe";
    private const string KeylessModelId = "gateway-probe";

    private IConfigurationRoot DeploymentConfiguration { get; } = new ConfigurationBuilder()
        .Add(new MemoryConfigurationSource())
        .Build();

    protected override bool ShareMeshAcrossTests => false;

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private ChatClientCredentialResolver Resolver =>
        Mesh.ServiceProvider.GetRequiredService<ChatClientCredentialResolver>();

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        // The deployment's own configuration, exactly as a Helm/env deployment supplies it:
        // {Section}__ApiKey / __Endpoint / __Models for ONE of the two providers below.
        DeploymentConfiguration[$"{KeyedSection}:ApiKey"] = DeploymentKey;
        DeploymentConfiguration[$"{KeyedSection}:Endpoint"] = KeyedEndpoint;
        DeploymentConfiguration[$"{KeyedSection}:Models:0"] = KeyedModelId;
        // KeylessSection deliberately carries NO key — an endpoint and a model list only.
        DeploymentConfiguration[$"{KeylessSection}:Endpoint"] = KeylessEndpoint;
        DeploymentConfiguration[$"{KeylessSection}:Models:0"] = KeylessModelId;
        // Required for the seed to write anything at all: without a master key it refuses rather
        // than persisting a credential in the clear (ProviderCredentialSeedWithoutMasterKeyTest).
        DeploymentConfiguration["Ai:KeyProtection:MasterKey"] = "test-master-key-usability-do-not-use";

        return base.ConfigureMesh(builder)
            .AddAI(new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { ModelProviderNodeType.RootNamespace })
            .AddLanguageModelCatalogSource(new LanguageModelCatalogSource(
                SectionName: KeyedSection, ProviderName: KeyedSection, Order: 1,
                DisplayLabel: "Anthropic (probe)", DefaultEndpoint: KeyedEndpoint,
                DefaultModelIds: ImmutableArray<string>.Empty, RequiresApiKey: true))
            .AddLanguageModelCatalogSource(new LanguageModelCatalogSource(
                SectionName: KeylessSection, ProviderName: KeylessSection, Order: 2,
                DisplayLabel: "Gateway (probe)", DefaultEndpoint: KeylessEndpoint,
                DefaultModelIds: ImmutableArray<string>.Empty, RequiresApiKey: true))
            .ConfigureServices(services =>
            {
                services.AddSingleton<IConfiguration>(DeploymentConfiguration);
                services.AddSingleton<IStaticRepoSource>(sp =>
                    new ModelStaticRepoSource(sp.GetRequiredService<BuiltInLanguageModelProvider>()));
                return services;
            });
    }

    /// <summary>Boots the deployment: the catalog import, then the credential seed — in that order.</summary>
    private async Task BootAsync()
    {
        var imported = await StaticRepoImporter.ImportAll(Mesh).ToList().FirstAsync().ToTask(Ct);
        foreach (var r in imported)
            Output.WriteLine($"import: partition={r.Partition} outcome={r.Outcome} count={r.Count}");

        var seeded = await ProviderCredentialSeed.Run(Mesh).ToList().FirstAsync().ToTask(Ct);
        foreach (var r in seeded)
            Output.WriteLine($"seed: path={r.ProviderPath} section={r.Section} outcome={r.Outcome}");

        Resolver.EnsureSubscription();
    }

    [Fact(Timeout = 180000)]
    public async Task ModelWhoseKeyLivesOnlyInDeploymentConfig_IsUsable()
    {
        await BootAsync();

        var resolution = await Observable.Interval(TimeSpan.FromMilliseconds(50))
            .Select(_ => Resolver.Resolve(KeyedModelId))
            .Should().Within(30.Seconds())
            .Match(r => !string.IsNullOrEmpty(r.ApiKey),
                "the deployment's configured key serves this provider's models");

        resolution.ApiKey.Should().Be(DeploymentKey);
        resolution.Endpoint.Should().Be(KeyedEndpoint);
        resolution.Source.Should().StartWith("providerRef:",
            "the NODE answered: configuration reached it through the seed, not through a resolution "
            + "rung — one administered home, one place to rotate (#1982)");

        Resolver.HasUsableCredential(KeyedModelId).Should().BeTrue(
            "a model the factories can serve must never be reported unusable — that is what "
            + "silently swaps an explicit user selection (MeshWeaver#1965)");
    }

    [Fact(Timeout = 180000)]
    public async Task ModelWithNoKeyAnywhere_StaysUnusable()
    {
        await BootAsync();

        // Give the snapshot the same chance to warm as the positive case, then assert the negative.
        await Observable.Interval(TimeSpan.FromMilliseconds(50))
            .Select(_ => Resolver.Resolve(KeylessModelId))
            .Should().Within(30.Seconds())
            .Match(r => r.Endpoint == KeylessEndpoint, "the model's provider node is in the snapshot");

        Resolver.Resolve(KeylessModelId).ApiKey.Should().BeNull(
            "no key exists on the node and none is configured for this section");
        Resolver.HasUsableCredential(KeylessModelId).Should().BeFalse(
            "an endpoint-only provider still throws 'ApiKey is missing' in every factory — neither "
            + "the seed nor the resolver may turn every model green");
    }
}
