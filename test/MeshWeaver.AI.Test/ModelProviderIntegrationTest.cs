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
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Integration tests covering the user-owned ModelProvider + LanguageModel
/// flow against a real Monolith mesh. Exercises the same path the chat
/// client takes: ModelDefinition.ProviderRef â†’ ModelProvider node â†’
/// (Endpoint, ApiKey) via <see cref="ChatClientCredentialResolver"/>.
/// </summary>
public class ModelProviderIntegrationTest : AITestBase
{
    public ModelProviderIntegrationTest(ITestOutputHelper output) : base(output) { }

    protected override bool ShareMeshAcrossTests => false;

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();
    private IWorkspace Workspace => Mesh.GetWorkspace();

    [Fact]
    public async Task UserOwnedModelProvider_ResolverFindsKeyViaProviderRef()
    {
        var ct = new CancellationTokenSource(20.Seconds()).Token;
        var userId = $"user-{Guid.NewGuid():N}";
        var providerPath = $"{userId}/_Provider/Anthropic";
        var modelId = "claude-opus-4-7";
        var modelPath = $"{providerPath}/{modelId}";
        var rawKey = "sk-ant-USERKEY-1234567890";

        // 1. Create the ModelProvider node in the user's namespace.
        var providerNode = new MeshNode("Anthropic", $"{userId}/_Provider")
        {
            NodeType = ModelProviderNodeType.NodeType,
            Name = "Anthropic",
            State = MeshNodeState.Active,
            MainNode = userId,
            Content = new ModelProviderConfiguration
            {
                Provider = "Anthropic",
                ApiKey = rawKey,
                Endpoint = "https://api.anthropic.com/v1/messages",
                Label = "User's key",
                CreatedAt = DateTimeOffset.UtcNow,
                Models = ImmutableArray.Create(modelId)
            }
        };
        var providerCreated = await MeshService.CreateNode(providerNode).FirstAsync().ToTask(ct);
        providerCreated.Path.Should().Be(providerPath);

        // 2. Create the LanguageModel child that references the provider.
        var modelNode = new MeshNode(modelId, providerPath)
        {
            NodeType = LanguageModelNodeType.NodeType,
            Name = modelId,
            State = MeshNodeState.Active,
            MainNode = userId,
            Content = new ModelDefinition
            {
                Id = modelId,
                Provider = "Anthropic",
                ProviderRef = providerPath,
                Order = 1
            }
        };
        await MeshService.CreateNode(modelNode).FirstAsync().ToTask(ct);

        // 3. Pre-warm the resolver's snapshot via the same workspace.GetQuery
        //    the resolver uses internally â€” see SyncedMeshNodeQueries.md.
        await Workspace.GetQuery(
                "warmup",
                AgentPickerProjection.BuildModelQueries(currentPath: userId))
            .Where(s => s.Any(n => n.Path == modelPath))
            .Take(1)
            .Timeout(15.Seconds())
            .ToTask(ct);

        // 4. Resolve and verify. Poll via Observable.Interval â€” the
        //    resolver's internal IngestSnapshot is driven by the same
        //    cached observable, but the OnNext order between our
        //    Take(1) above and the resolver's subscription is not
        //    guaranteed. Polling the public surface is the robust wait.
        var resolver = Mesh.ServiceProvider.GetRequiredService<ChatClientCredentialResolver>();
        resolver.WatchPartition(userId);

        var resolution = await Observable.Interval(TimeSpan.FromMilliseconds(50))
            .Select(_ => resolver.Resolve(modelId))
            .Where(r => r.ApiKey != null)
            .Take(1).Timeout(10.Seconds()).ToTask(ct);
        resolution.ApiKey.Should().Be(rawKey, "resolver follows ProviderRef â†’ ModelProvider.ApiKey");
        resolution.Endpoint.Should().Be("https://api.anthropic.com/v1/messages");
        resolution.Source.Should().StartWith("providerRef:");
    }

    [Fact]
    public async Task Resolver_MissingProvider_ReturnsMissing()
    {
        var ct = new CancellationTokenSource(15.Seconds()).Token;
        // Model node without a ProviderRef and no parent provider â€”
        // resolver should report Missing so the factory falls back to IOptions.
        var orphanId = $"orphan-model-{Guid.NewGuid():N}";
        var modelNs = $"{ModelProviderNodeType.RootNamespace}/NoSuchProvider";
        var modelPath = $"{modelNs}/{orphanId}";
        var modelNode = new MeshNode(orphanId, modelNs)
        {
            NodeType = LanguageModelNodeType.NodeType,
            Name = orphanId,
            Content = new ModelDefinition
            {
                Id = orphanId,
                Provider = "NoSuchProvider",
            }
        };
        await MeshService.CreateNode(modelNode).FirstAsync().ToTask(ct);

        await Workspace.GetQuery("warm-orphan", AgentPickerProjection.BuildModelQueries())
            .Where(s => s.Any(n => n.Path == modelPath))
            .Take(1)
            .Timeout(10.Seconds())
            .ToTask(ct);

        var resolver = Mesh.ServiceProvider.GetRequiredService<ChatClientCredentialResolver>();
        var resolution = resolver.Resolve(orphanId);

        resolution.ApiKey.Should().BeNull();
        resolution.Endpoint.Should().BeNull();
        resolution.Source.Should().Be("missing");
    }

    [Fact]
    public async Task Resolver_UnknownModelId_ReturnsMissing()
    {
        var ct = new CancellationTokenSource(10.Seconds()).Token;
        var resolver = Mesh.ServiceProvider.GetRequiredService<ChatClientCredentialResolver>();
        resolver.EnsureSubscription();
        // Wait via the same synced observable the resolver subscribes to â€”
        // once it emits any Initial snapshot, the resolver has been
        // notified too (same cache id, same upstream).
        await Workspace.GetQuery(AgentPickerProjection.ModelsQueryId, AgentPickerProjection.BuildModelQueries())
            .Take(1).Timeout(8.Seconds()).ToTask(ct);
        resolver.Resolve("definitely-not-a-real-model-id").Should().Be(CredentialResolution.Missing);
    }

    [Fact]
    public async Task ResolverGetProviderForModel_ReturnsCachedProvider()
    {
        var ct = new CancellationTokenSource(15.Seconds()).Token;
        var modelId = $"prov-{Guid.NewGuid():N}";
        var modelNs = $"{ModelProviderNodeType.RootNamespace}/Anthropic";
        var modelPath = $"{modelNs}/{modelId}";
        var modelNode = new MeshNode(modelId, modelNs)
        {
            NodeType = LanguageModelNodeType.NodeType,
            Name = modelId,
            Content = new ModelDefinition
            {
                Id = modelId,
                Provider = "Anthropic",
            }
        };
        await MeshService.CreateNode(modelNode).FirstAsync().ToTask(ct);

        // Drive the resolver's own subscription so its internal
        // modelsById dict ingests the new node, then poll-via-interval
        // until the public surface sees it. Polling is robust against
        // OnNext ordering between our Take(1) and the resolver's
        // own OnNext on the same cached observable.
        var resolver = Mesh.ServiceProvider.GetRequiredService<ChatClientCredentialResolver>();
        resolver.EnsureSubscription();
        var provider = await Observable.Interval(TimeSpan.FromMilliseconds(50))
            .Select(_ => resolver.GetProviderForModel(modelId))
            .Where(p => p != null)
            .Take(1).Timeout(10.Seconds()).ToTask(ct);
        provider.Should().Be("Anthropic");
    }
}
