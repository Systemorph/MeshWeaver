#pragma warning disable CS1591

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Tests <see cref="ChatClientCredentialResolver"/>'s precedence chain
/// end-to-end. The resolver follows
/// <see cref="ModelDefinition.ProviderRef"/> to the ModelProvider node
/// holding the credential. We exercise each rung with real mesh writes.
/// </summary>
public class ChatClientCredentialResolverTest : AITestBase
{
    public ChatClientCredentialResolverTest(ITestOutputHelper output) : base(output) { }

    protected override bool ShareMeshAcrossTests => false;

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    [Fact]
    public void Resolve_UnknownModelId_ReturnsMissing()
    {
        var resolver = Mesh.ServiceProvider.GetRequiredService<ChatClientCredentialResolver>();
        var resolution = resolver.Resolve("definitely-not-real-" + Guid.NewGuid());
        resolution.Should().Be(CredentialResolution.Missing);
    }

    [Fact]
    public void Resolve_NullOrEmpty_ReturnsMissing()
    {
        var resolver = Mesh.ServiceProvider.GetRequiredService<ChatClientCredentialResolver>();
        resolver.Resolve("").Should().Be(CredentialResolution.Missing);
        resolver.Resolve(null!).Should().Be(CredentialResolution.Missing);
    }

    [Fact]
    public async Task Resolve_ModelWithLegacyApiKeySecretRef_FallsThroughToModelNode()
    {
        var modelId = $"legacy-{Guid.NewGuid():N}";
        // Place the LanguageModel under the canonical _Provider subtree â€”
        // the resolver's subscription only watches _Provider/* (per
        // AgentPickerProjection.BuildModelQueries' documented pattern).
        var modelNs = $"{ModelProviderNodeType.RootNamespace}/Anthropic";
        var modelPath = $"{modelNs}/{modelId}";

        // Legacy shape: a LanguageModel node carrying the key directly on
        // its content (no ProviderRef). The resolver's last rung â€”
        // "model-node" â€” picks this up.
        await MeshService.CreateNode(new MeshNode(modelId, modelNs)
        {
            NodeType = LanguageModelNodeType.NodeType,
            Name = modelId,
            State = MeshNodeState.Active,
            Content = new ModelDefinition
            {
                Id = modelId,
                Provider = "Anthropic",
                ApiKeySecretRef = "sk-legacy-on-model-node",
                Endpoint = "https://legacy.example/v1/messages",
                // No ProviderRef â€” resolver should fall through to model fields.
                ProviderRef = null,
            }
        }).Should().Within(15.Seconds()).Emit();

        // Pre-warm the resolver's snapshot so it sees the model.
        var workspace = Mesh.GetWorkspace();
        await workspace.GetMeshNodeStream(modelPath)
            .Should().Within(10.Seconds()).Match(n => n?.Content is ModelDefinition);

        var resolver = Mesh.ServiceProvider.GetRequiredService<ChatClientCredentialResolver>();
        resolver.EnsureSubscription();
        // Poll the resolver's public surface â€” robust against OnNext
        // ordering vs the warmup observable above.
        var resolution = await Observable.Interval(TimeSpan.FromMilliseconds(50))
            .Select(_ => resolver.Resolve(modelId))
            .Should().Within(10.Seconds()).Match(r => r.ApiKey != null);
        resolution.ApiKey.Should().Be("sk-legacy-on-model-node",
            "fallback to ModelDefinition.ApiKeySecretRef when no ProviderRef is set");
        resolution.Endpoint.Should().Be("https://legacy.example/v1/messages");
        resolution.Source.Should().Be("model-node");
    }

    [Fact]
    public async Task GetProviderForModel_LooksUpProviderStamp()
    {
        var modelId = $"pmodel-{Guid.NewGuid():N}";
        var modelNs = $"{ModelProviderNodeType.RootNamespace}/Anthropic";
        var modelPath = $"{modelNs}/{modelId}";

        await MeshService.CreateNode(new MeshNode(modelId, modelNs)
        {
            NodeType = LanguageModelNodeType.NodeType,
            Name = modelId,
            State = MeshNodeState.Active,
            Content = new ModelDefinition
            {
                Id = modelId,
                Provider = "Anthropic",
            }
        }).Should().Within(15.Seconds()).Emit();

        var workspace = Mesh.GetWorkspace();
        await workspace.GetMeshNodeStream(modelPath)
            .Should().Within(10.Seconds()).Match(n => n?.Content is ModelDefinition);

        var resolver = Mesh.ServiceProvider.GetRequiredService<ChatClientCredentialResolver>();
        resolver.EnsureSubscription();
        var provider = await Observable.Interval(TimeSpan.FromMilliseconds(50))
            .Select(_ => resolver.GetProviderForModel(modelId))
            .Should().Within(10.Seconds()).Match(p => p != null);
        provider.Should().Be("Anthropic");
        resolver.GetProviderForModel("does-not-exist").Should().BeNull();
    }
}
