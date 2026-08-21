#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// A model the deployment can ACTUALLY serve must read as usable.
///
/// <para>Every provider factory resolves its driver config in two steps —
/// <c>resolution.ApiKey ?? configuration.ApiKey</c> — so a deployment that keeps its key in
/// configuration (<c>Anthropic__ApiKey</c>) serves that provider's models perfectly well even when
/// the <c>ModelProvider</c> NODE carries no key. <see cref="ChatClientCredentialResolver"/> used to
/// see only the node, so it answered <see cref="ChatClientCredentialResolver.HasUsableCredential"/>
/// = <c>false</c> for a model that runs — and <c>AgentChatClient.ApplyStaleModelFallback</c> then
/// SILENTLY swapped the user's explicit pick for the deployment default (measured on
/// <c>memex.systemorph.com</c>, 2026-08-21, MeshWeaver#1965: every <c>claude-*</c> selection was
/// served by another provider's factory).</para>
///
/// <para>The node is keyless in the first place because it is <b>create-if-absent</b>: the catalog
/// seeder stamps <c>{Section}:ApiKey</c> onto the <c>ModelProvider</c> node the FIRST time it
/// creates it and never revisits it, so a key added to the deployment's configuration afterwards
/// reaches the factories and never reaches the node. That is why the fixtures below point their
/// model at a provider node distinct from the one this test's own catalog source seeds: a
/// freshly-seeded node would carry the config key and hide the case entirely.</para>
/// </summary>
public class DeploymentConfiguredModelUsabilityTest : AITestBase
{
    /// <summary>Config section + provider name of a provider whose key the DEPLOYMENT holds.</summary>
    private const string KeyedSection = "AnthropicProbe";

    /// <summary>Config section + provider name of a provider nobody holds a key for.</summary>
    private const string KeylessSection = "GatewayProbe";

    private const string DeploymentKey = "sk-ant-deployment-config-key";
    private const string DeploymentEndpoint = "https://probe.example/anthropic/v1/messages";

    /// <summary>The provider node the models point at — endpoint only, deliberately NO key.</summary>
    private const string KeyedProviderNodeId = "AnthropicProbeExisting";
    private const string KeylessProviderNodeId = "GatewayProbeExisting";
    private const string NodeEndpoint = "https://node.example/anthropic/v1/messages";

    public DeploymentConfiguredModelUsabilityTest(ITestOutputHelper output) : base(output) { }

    protected override bool ShareMeshAcrossTests => false;

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private ChatClientCredentialResolver Resolver =>
        Mesh.ServiceProvider.GetRequiredService<ChatClientCredentialResolver>();

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            // The deployment's own configuration, exactly as a Helm/env deployment supplies it:
            // Anthropic__ApiKey / Anthropic__Endpoint for ONE of the two providers below.
            .ConfigureServices(services =>
            {
                services.RemoveAll<IConfiguration>();
                services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
                    .SetBasePath(Directory.GetCurrentDirectory())
                    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        [$"{KeyedSection}:ApiKey"] = DeploymentKey,
                        [$"{KeyedSection}:Endpoint"] = DeploymentEndpoint,
                        // KeylessSection deliberately carries NOTHING.
                    })
                    .Build());
                return services;
            })
            .AddLanguageModelCatalogSource(new LanguageModelCatalogSource(
                SectionName: KeyedSection, ProviderName: KeyedSection, Order: 1,
                DisplayLabel: "Anthropic (probe)", DefaultEndpoint: null,
                DefaultModelIds: ImmutableArray<string>.Empty, RequiresApiKey: true))
            .AddLanguageModelCatalogSource(new LanguageModelCatalogSource(
                SectionName: KeylessSection, ProviderName: KeylessSection, Order: 2,
                DisplayLabel: "Gateway (probe)", DefaultEndpoint: null,
                DefaultModelIds: ImmutableArray<string>.Empty, RequiresApiKey: true));

    [Fact]
    public async Task ModelWhoseKeyLivesOnlyInDeploymentConfig_IsUsable()
    {
        var modelId = await SeedModel(KeyedSection, KeyedProviderNodeId, "claude-probe");

        var resolution = await Observable.Interval(TimeSpan.FromMilliseconds(50))
            .Select(_ => Resolver.Resolve(modelId))
            .Should().Within(15.Seconds())
            .Match(r => !string.IsNullOrEmpty(r.ApiKey),
                "the deployment's configured key serves this provider's models");

        resolution.ApiKey.Should().Be(DeploymentKey);
        // The NODE's endpoint still wins over the configured one — same per-field precedence the
        // factories apply (resolution.Endpoint ?? configuration.Endpoint).
        resolution.Endpoint.Should().Be(NodeEndpoint);
        resolution.Source.Should().Contain($"config:{KeyedSection}",
            "the operator must be able to see WHICH source answered");

        Resolver.HasUsableCredential(modelId).Should().BeTrue(
            "a model the factories can serve must never be reported unusable — that is what "
            + "silently swaps an explicit user selection (MeshWeaver#1965)");
    }

    [Fact]
    public async Task ModelWithNoKeyAnywhere_StaysUnusable()
    {
        var modelId = await SeedModel(KeylessSection, KeylessProviderNodeId, "gateway-probe");

        // Give the snapshot the same chance to warm as the positive case, then assert the negative.
        await Observable.Interval(TimeSpan.FromMilliseconds(50))
            .Select(_ => Resolver.Resolve(modelId))
            .Should().Within(15.Seconds())
            .Match(r => r.Endpoint == NodeEndpoint, "the model's provider node is in the snapshot");

        Resolver.Resolve(modelId).ApiKey.Should().BeNull(
            "no key exists on the node and none is configured for this section");
        Resolver.HasUsableCredential(modelId).Should().BeFalse(
            "an endpoint-only provider still throws 'ApiKey is missing' in every factory — the "
            + "config rung must not turn every model green");
    }

    /// <summary>
    /// Creates the memex shape: a <c>ModelProvider</c> node carrying an endpoint but NO key, plus a
    /// <c>LanguageModel</c> under the provider's catalog namespace that references it.
    /// </summary>
    /// <returns>The seeded model's wire id.</returns>
    private async Task<string> SeedModel(string providerName, string providerNodeId, string modelPrefix)
    {
        var providerPath = $"{ModelProviderNodeType.RootNamespace}/{providerNodeId}";
        await MeshService.CreateNode(new MeshNode(providerNodeId, ModelProviderNodeType.RootNamespace)
        {
            NodeType = ModelProviderNodeType.NodeType,
            Name = providerNodeId,
            State = MeshNodeState.Active,
            Content = new ModelProviderConfiguration
            {
                Provider = providerName,
                Endpoint = NodeEndpoint,
                ApiKey = null,
            }
        }).Should().Within(15.Seconds()).Emit();

        var modelId = $"{modelPrefix}-{Guid.NewGuid():N}"[..24];
        var modelNamespace = $"{ModelProviderNodeType.RootNamespace}/{providerName}";
        await MeshService.CreateNode(new MeshNode(modelId, modelNamespace)
        {
            NodeType = LanguageModelNodeType.NodeType,
            Name = modelId,
            State = MeshNodeState.Active,
            Content = new ModelDefinition
            {
                Id = modelId,
                Provider = providerName,
                ProviderRef = providerPath,
            }
        }).Should().Within(15.Seconds()).Emit();

        Resolver.EnsureSubscription();
        return modelId;
    }
}
