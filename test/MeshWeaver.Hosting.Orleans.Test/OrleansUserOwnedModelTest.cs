#pragma warning disable CS1591

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Orleans integration tests for the user-owned ModelProvider +
/// LanguageModel flow. Every credential is a MeshNode under the user's
/// namespace; the chat-client factory's
/// <see cref="ChatClientCredentialResolver"/> follows
/// <see cref="ModelDefinition.ProviderRef"/> via the same
/// <c>workspace.GetQuery</c> the chat picker uses.
///
/// <para>Tests focus on the cross-silo / cross-grain path: user creates
/// the nodes through a client hub, the silo's mesh hub persists them, and
/// the silo's per-hub <see cref="ChatClientCredentialResolver"/> resolves
/// the credential through the workspace synced query.</para>
/// </summary>
public class OrleansUserOwnedModelTest(ITestOutputHelper output) : OrleansSharedTestBase(output)
{
    private async Task<IMessageHub> GetClientAsync(string userId)
    {
        var client = await base.GetClientAsync($"models-{Guid.NewGuid():N}", userId);
        return client;
    }

    [Fact(Timeout = 30_000)]
    public async Task UserCreatesProvider_ThenResolverFindsKey()
    {
        var ct = new CancellationTokenSource(45.Seconds()).Token;
        var userId = $"user-{Guid.NewGuid():N}";
        var providerPath = $"{userId}/_Provider/Anthropic";
        var modelId = "claude-opus-4-7";
        var modelPath = $"{providerPath}/{modelId}";
        var rawKey = "sk-ant-USER-orleans-key";

        var client = await GetClientAsync(userId);
        var meshAddress = Fixture.ClientMesh.Address;

        // 1. Create ModelProvider node in the user's namespace.
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
                Label = "User-owned key",
                CreatedAt = DateTimeOffset.UtcNow,
                Models = ImmutableArray.Create(modelId),
            }
        };
        var providerResp = await client.Observe(new CreateNodeRequest(providerNode), o => o.WithTarget(meshAddress))
            .FirstAsync().ToTask(ct);
        providerResp.Message.Success.Should().BeTrue(providerResp.Message.Error);
        providerResp.Message.Node!.Path.Should().Be(providerPath);

        // 2. Create the LanguageModel child referencing the provider.
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
                Order = 1,
            }
        };
        var modelResp = await client.Observe(new CreateNodeRequest(modelNode), o => o.WithTarget(meshAddress))
            .FirstAsync().ToTask(ct);
        modelResp.Message.Success.Should().BeTrue(modelResp.Message.Error);
        modelResp.Message.Node!.Path.Should().Be(modelPath);

        // 3. Verify the resolver can see both nodes â€” read through the
        //    SAME synced query (AgentPickerProjection-shape) that
        //    ChatClientCredentialResolver subscribes to. Asserting via the
        //    resolver's read path means we're testing what the resolver
        //    actually observes, not a separate per-node read that races
        //    Orleans routing-grain index convergence.
        var siloWorkspace = client.GetWorkspace();
        var providerNs = $"{userId}/_Provider";

        var snapshot = await siloWorkspace.GetQuery(
                $"user-owned-models-test:{userId}",
                $"namespace:{providerNs} nodeType:{ModelProviderNodeType.NodeType}",
                $"namespace:{providerPath} nodeType:{LanguageModelNodeType.NodeType}")
            .Where(s => s.Any(n => n.Path == providerPath && n.Content is ModelProviderConfiguration)
                        && s.Any(n => n.Path == modelPath && n.Content is ModelDefinition))
            .Take(1).Timeout(20.Seconds()).ToTask(ct);

        var providerSnap = snapshot.Single(n => n.Path == providerPath);
        var cfg = providerSnap.Content.Should().BeOfType<ModelProviderConfiguration>().Subject;
        cfg.ApiKey.Should().Be(rawKey,
            "user-supplied credential is persisted in the ModelProvider node under the owner's namespace");

        var modelSnap = snapshot.Single(n => n.Path == modelPath);
        var def = modelSnap.Content.Should().BeOfType<ModelDefinition>().Subject;
        def.ProviderRef.Should().Be(providerPath,
            "LanguageModel.ProviderRef points back at the parent ModelProvider, so the resolver can follow the link");
        def.Provider.Should().Be("Anthropic");
    }

    [Fact(Timeout = 30_000)]
    public async Task UserModelAndProvider_VisibleInSyncedQuery()
    {
        var ct = new CancellationTokenSource(45.Seconds()).Token;
        var userId = $"user-{Guid.NewGuid():N}";
        var providerPath = $"{userId}/_Provider/OpenAI";
        var modelId = "gpt-4o-mini";
        var modelPath = $"{providerPath}/{modelId}";

        var client = await GetClientAsync(userId);
        var meshAddress = Fixture.ClientMesh.Address;

        var providerNode = new MeshNode("OpenAI", $"{userId}/_Provider")
        {
            NodeType = ModelProviderNodeType.NodeType,
            Name = "OpenAI",
            State = MeshNodeState.Active,
            MainNode = userId,
            Content = new ModelProviderConfiguration
            {
                Provider = "OpenAI",
                ApiKey = "sk-openai-test",
                CreatedAt = DateTimeOffset.UtcNow,
                Models = ImmutableArray.Create(modelId),
            }
        };
        await client.Observe(new CreateNodeRequest(providerNode), o => o.WithTarget(meshAddress))
            .FirstAsync().ToTask(ct);

        var modelNode = new MeshNode(modelId, providerPath)
        {
            NodeType = LanguageModelNodeType.NodeType,
            Name = modelId,
            State = MeshNodeState.Active,
            MainNode = userId,
            Content = new ModelDefinition
            {
                Id = modelId,
                Provider = "OpenAI",
                ProviderRef = providerPath,
            }
        };
        await client.Observe(new CreateNodeRequest(modelNode), o => o.WithTarget(meshAddress))
            .FirstAsync().ToTask(ct);

        // Subscribe to a synced query scoped to the owner's Model subtree
        // â€” the picker's default scope:selfAndAncestors query walks UP from
        // currentPath, so a sibling subtree like {userId}/Model isn't
        // visible by default. This explicit subtree query is what
        // ChatClientCredentialResolver.WatchPartition uses internally.
        // Read through the CLIENT workspace (carries the user's
        // AccessContext via the fixture's SetCircuitContext). Reading via
        // the silo's mesh hub would run with no identity and trip RLS
        // on the user-partition subtree.
        var siloWorkspace = client.GetWorkspace();

        var providerNs = $"{userId}/_Provider";

        var snapshot = await siloWorkspace.GetQuery(
                $"picker-test:{userId}",
                $"namespace:{providerNs} nodeType:{ModelProviderNodeType.NodeType}",
                $"namespace:{providerPath} nodeType:{LanguageModelNodeType.NodeType}")
            .Where(s => s.Any(n => n.Path == modelPath) && s.Any(n => n.Path == providerPath))
            .Take(1).Timeout(20.Seconds()).ToTask(ct);

        snapshot.Should().Contain(n => n.Path == modelPath
            && n.NodeType == LanguageModelNodeType.NodeType);
        snapshot.Should().Contain(n => n.Path == providerPath
            && n.NodeType == ModelProviderNodeType.NodeType);
    }

    // The rotate-via-cache.Update flow is now exercised by
    // OrleansCacheUpdateMultiSiloTest under a clean 2-silo (no-client)
    // fixture â€” that mirrors prod, where a silo issues cache.Update against
    // a node whose owning per-node hub is activated on a different silo.
    // The previous single-silo+client variant of this test conflated
    // cross-process IDataChangeNotifier scoping with the cache.Update flow
    // itself; the dedicated 2-silo test isolates the cache path.
}
