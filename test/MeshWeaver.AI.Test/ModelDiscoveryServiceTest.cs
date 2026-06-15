#pragma warning disable CS1591

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Tests for <see cref="ModelDiscoveryService"/> â€” the hierarchical
/// walk over namespace ancestors AND NodeType ancestors. Asserts that:
/// <list type="bullet">
///   <item>(a) <c>GetModelsAtNode</c> returns the satellite at one path.</item>
///   <item>(b) <c>GetModelsForNodeHierarchy</c> walks UP and unions every
///         level (closer paths shadow further ones in the projection).</item>
///   <item>(c) <c>GetEffectiveModels</c> combines (b) for the node-path
///         with (b) for the NodeType-path.</item>
///   <item>The service is anchored to the mesh hub, so subscriptions
///         survive even when a per-thread hub is busy.</item>
/// </list>
/// </summary>
public class ModelDiscoveryServiceTest : AITestBase
{
    public ModelDiscoveryServiceTest(ITestOutputHelper output) : base(output) { }

    protected override bool ShareMeshAcrossTests => false;

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();
    private ModelDiscoveryService Discovery => Mesh.ServiceProvider.GetRequiredService<ModelDiscoveryService>();

    private async Task CreateUserProvider(string ownerPath, string providerName, string modelId)
    {
        var providerPath = $"{ownerPath}/{ModelProviderNodeType.RootNamespace}/{providerName}";
        await MeshService.CreateNode(new MeshNode(providerName, $"{ownerPath}/{ModelProviderNodeType.RootNamespace}")
        {
            NodeType = ModelProviderNodeType.NodeType,
            Name = providerName,
            State = MeshNodeState.Active,
            MainNode = ownerPath,
            Content = new ModelProviderConfiguration
            {
                Provider = providerName,
                ApiKey = $"sk-{providerName}-{ownerPath}",
                CreatedAt = DateTimeOffset.UtcNow,
                Models = ImmutableArray.Create(modelId),
            }
        }).Should().Within(20.Seconds()).Emit();
        await MeshService.CreateNode(new MeshNode(modelId, providerPath)
        {
            NodeType = LanguageModelNodeType.NodeType,
            Name = modelId,
            State = MeshNodeState.Active,
            MainNode = ownerPath,
            Content = new ModelDefinition
            {
                Id = modelId,
                Provider = providerName,
                ProviderRef = providerPath,
            }
        }).Should().Within(20.Seconds()).Emit();
    }

    [Fact]
    public async Task GetModelsAtNode_ReturnsSatelliteSubtree()
    {
        var owner = $"owner-{Guid.NewGuid():N}";
        await CreateUserProvider(owner, "Anthropic", "claude-opus-4-7");

        var snapshot = await Discovery.GetModelsAtNode(owner)
            .Should().Within(15.Seconds()).Match(s => s.Count >= 2);

        snapshot.Should().Contain(n => n.NodeType == ModelProviderNodeType.NodeType
            && n.Path == $"{owner}/_Provider/Anthropic");
        snapshot.Should().Contain(n => n.NodeType == LanguageModelNodeType.NodeType
            && n.Path == $"{owner}/_Provider/Anthropic/claude-opus-4-7");
    }

    [Fact]
    public async Task GetModelsForNodeHierarchy_UnionsAncestorLevels()
    {
        var owner = $"org-{Guid.NewGuid():N}";
        var child = $"{owner}/Project1";

        // Two providers â€” one at the org level, one at the child level.
        await CreateUserProvider(owner, "Anthropic", "claude-sonnet-4-6");
        await CreateUserProvider(child, "OpenAI", "gpt-4o-mini");

        // Asking for the child's hierarchy should see BOTH (its own + org's).
        var snapshot = await Discovery.GetModelsForNodeHierarchy(child)
            .Should().Within(20.Seconds()).Match(s => s.Any(n => n.Provider() == "Anthropic")
                     && s.Any(n => n.Provider() == "OpenAI"));

        snapshot.Should().Contain(n => n.Path == $"{child}/_Provider/OpenAI",
            "child node's own ModelProvider surfaces");
        snapshot.Should().Contain(n => n.Path == $"{owner}/_Provider/Anthropic",
            "ancestor org's ModelProvider is also unioned in");
    }

    [Fact]
    public async Task GetEffectiveModels_CombinesNodePathAndNodeTypePath()
    {
        var nodePath = $"nodeOwner-{Guid.NewGuid():N}";
        var nodeTypePath = $"ntOwner-{Guid.NewGuid():N}";

        // One provider on the node-path side, one on the NodeType side.
        await CreateUserProvider(nodePath, "Anthropic", "claude-opus-4-7");
        await CreateUserProvider(nodeTypePath, "OpenAI", "gpt-4o");

        var snapshot = await Discovery.GetEffectiveModels(nodePath, nodeTypePath)
            .Should().Within(20.Seconds()).Match(s => s.Any(n => n.Provider() == "Anthropic")
                     && s.Any(n => n.Provider() == "OpenAI"));

        snapshot.Should().Contain(n => n.Path == $"{nodePath}/_Provider/Anthropic");
        snapshot.Should().Contain(n => n.Path == $"{nodeTypePath}/_Provider/OpenAI");
    }

    [Fact]
    public void Service_RegisteredOnTopLevelMeshHub_NotPerThreadHub()
    {
        // Resolving the service from the mesh hub's ServiceProvider must
        // succeed. The contract is that callers reach into the top-level
        // mesh hub for this â€” never their own per-thread/per-exec hub
        // (which may be blocked by an in-flight handler).
        var fromMesh = Mesh.ServiceProvider.GetService<ModelDiscoveryService>();
        fromMesh.Should().NotBeNull("ModelDiscoveryService is registered as a top-level singleton");

        // The service is a hub-scoped singleton; resolving it twice
        // returns the same instance.
        var again = Mesh.ServiceProvider.GetRequiredService<ModelDiscoveryService>();
        ReferenceEquals(fromMesh, again).Should().BeTrue();
    }
}

internal static class MeshNodeProviderProjection
{
    public static string? Provider(this MeshNode n) => n.Content switch
    {
        ModelProviderConfiguration cfg => cfg.Provider,
        ModelDefinition def => def.Provider,
        _ => null
    };
}
