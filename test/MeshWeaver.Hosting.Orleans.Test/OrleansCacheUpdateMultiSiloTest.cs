#pragma warning disable CS1591

using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Two-silo cluster, no client. The test driver issues
/// <c>cache.Update</c> against a silo's <see cref="IMeshNodeStreamCache"/> â€”
/// the cache hub's <c>UpdateRemote</c> posts <c>PatchDataRequest</c> to the
/// owning per-node hub, which Orleans hashes onto one of the two silos.
///
/// <para>This pins the cross-silo path of <c>cache.Update</c> â€” the same flow
/// a portal silo exercises when it rotates a credential on a node activated
/// on a different silo. The previous client-based variant of this test
/// conflated cross-process notifier scoping with the cache.Update flow.</para>
/// </summary>
public class OrleansCacheUpdateMultiSiloTest : IClassFixture<TwoSiloCacheUpdateFixture>
{
    private readonly TwoSiloCacheUpdateFixture _fixture;

    public OrleansCacheUpdateMultiSiloTest(TwoSiloCacheUpdateFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Create a ModelProvider node, rotate its ApiKey through
    /// <see cref="IMeshNodeStreamCache.Update(string, Func{MeshNode, MeshNode}, System.Text.Json.JsonSerializerOptions)"/>,
    /// then read it back through <c>workspace.GetMeshNodeStream(path)</c>
    /// and assert the post-rotate value persisted. Uses the single-node
    /// authoritative read primitive rather than the synced-query feed.
    /// </summary>
    [Fact]
    public async Task RotateApiKey_ThroughCacheUpdate_PersistsAndIsReadable()
    {
        var ct = new CancellationTokenSource(45.Seconds()).Token;
        var siloHub = _fixture.PrimarySiloMeshHub;
        var ns = $"acme-{Guid.NewGuid():N}/_Provider";
        var providerPath = $"{ns}/Anthropic";

        // 1. Create the provider node through the silo's mesh hub.
        var providerNode = new MeshNode("Anthropic", ns)
        {
            NodeType = ModelProviderNodeType.NodeType,
            Name = "Anthropic",
            State = MeshNodeState.Active,
            Content = new ModelProviderConfiguration
            {
                Provider = "Anthropic",
                ApiKey = "sk-original",
                CreatedAt = DateTimeOffset.UtcNow,
            }
        };
        var createResp = await siloHub
            .Observe(new CreateNodeRequest(providerNode), o => o.WithTarget(siloHub.Address))
            .FirstAsync().ToTask(ct);
        createResp.Message.Success.Should().BeTrue(createResp.Message.Error);
        createResp.Message.Node!.Path.Should().Be(providerPath);

        // 2. Rotate the ApiKey via cache.Update on the silo's
        //    IMeshNodeStreamCache. The cache hub posts PatchDataRequest to
        //    the per-node hub at providerPath â€” Orleans routes to whichever
        //    silo owns that grain key. With AddAI on both silos the
        //    caller's JsonSerializerOptions know ModelProviderConfiguration,
        //    so the lambda receives typed Content.
        var cache = siloHub.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
        await cache.Update(providerPath, node => node with
        {
            Content = (node.Content as ModelProviderConfiguration
                       ?? new ModelProviderConfiguration { Provider = "Anthropic" })
                with { ApiKey = "sk-rotated" }
        }, siloHub.JsonSerializerOptions)
        .Take(1).Timeout(30.Seconds()).ToTask(ct);

        // 3. Read the node back through GetMeshNodeStream â€” the authoritative
        //    single-node primitive. The persistence sampler debounces saves
        //    by ~200 ms, so poll until the sk-rotated value surfaces.
        var workspace = siloHub.GetWorkspace();
        var rotated = await Observable
            .Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => workspace.GetMeshNodeStream(providerPath).Take(1))
            .Where(n => n is not null
                        && (n.Content as ModelProviderConfiguration)?.ApiKey == "sk-rotated")
            .FirstAsync().Timeout(20.Seconds()).ToTask(ct);

        var cfg = rotated!.Content.Should().BeOfType<ModelProviderConfiguration>().Subject;
        cfg.ApiKey.Should().Be("sk-rotated", "cache.Update rotate must persist");
        cfg.Provider.Should().Be("Anthropic", "other fields must be preserved through the merge patch");
    }
}
