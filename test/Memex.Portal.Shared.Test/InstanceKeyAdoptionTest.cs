using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Memex.Portal.Shared.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🅿️ Registry-side half of a key ROTATION driven by the Hosting operator (MeshWeaver#2802).
///
/// <para>The operator's <c>hosting-kv-rotate</c> mints the raw <c>mwi_</c> key, puts it in Key
/// Vault, and reports ONLY its SHA-256 through a <c>::hosting:: key_hash=</c> line. The registry
/// then adopts that hash: the instance node takes it, a fresh index entry points at it, and the
/// PREVIOUS index entry is deleted so the old key stops authenticating at once. The raw key never
/// reaches the registry process — this test never holds one either, only hashes.</para>
/// </summary>
public class InstanceKeyAdoptionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddPluginCatalog();

    private MeshWeaverInstanceService Service() => new(
        Mesh.ServiceProvider.GetRequiredService<IMeshService>(),
        Mesh,
        Mesh.ServiceProvider.GetRequiredService<ILogger<MeshWeaverInstanceService>>(),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build());

    private static string IndexPath(string hash)
        => $"{MeshWeaverInstanceNodeType.IndexNamespace}/{InstanceKeys.HashPrefix(hash)}";

    // 🚨 Index nodes live in the system-owned `MeshWeaverInstance/` namespace and are read here the
    // way the registry's own tests read fresh nodes: as System (RLS), through GetMeshNode, which
    // re-probes a NotFound once against a fresh activation instead of terminating the stream —
    // the point-read-of-a-node-that-may-not-exist rule from AGENTS.md.
    private Task<MeshNode?> Node(string path)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        return access.RunAsSystem(() => Mesh.GetMeshNode(path, TimeSpan.FromSeconds(10)).Take(1))
            .Timeout(TimeSpan.FromSeconds(30))
            .Await();
    }

    [Fact(Timeout = 120_000)]
    public async Task AdoptKeyHash_MovesTheInstanceOntoTheNewHash_AndRetiresTheOldIndexEntry()
    {
        var service = Service();
        var registered = await service
            .Register("owner", "Owner", "owner@test.com", "rotate-me", "Rotate Me")
            .Timeout(TimeSpan.FromSeconds(60)).Await();
        var instancePath = registered.Node.Path!;
        var oldHash = registered.Instance.KeyHash;
        oldHash.Should().HaveLength(64, "registration persists the SHA-256 hex of the key");
        (await Node(IndexPath(oldHash))).Should().NotBeNull("registration wrote the index entry for the original key");

        // What the operator reports: a hash of a key this process has never seen.
        var newHash = InstanceKeys.Hash("mwi_" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"));

        // Through the CONTRACT the Hosting plugin resolves — by instance id, as the operator knows it.
        await ((IInstanceKeyRegistry)service).AdoptKeyHash("rotate-me", newHash).Timeout(TimeSpan.FromSeconds(60)).Await();

        var node = await Node(instancePath);
        var instance = node!.ContentAs<MeshWeaverInstance>(Mesh.JsonSerializerOptions)!;
        instance.KeyHash.Should().Be(newHash, "the instance now authenticates the rotated key");
        instance.KeyIssuedAt.Should().NotBeNull();
        instance.KeyIssuedAt!.Value.Should().BeAfter(registered.Instance.KeyIssuedAt!.Value,
            "re-issue moves the issued-at stamp");

        var newIndex = await Node(IndexPath(newHash));
        newIndex.Should().NotBeNull("a fresh index entry must point at the instance");
        newIndex!.ContentAs<MeshWeaverInstanceIndex>(Mesh.JsonSerializerOptions)!.InstancePath
            .Should().Be(instancePath);

        // 🚨 The old key must stop authenticating: its index entry is GONE, not merely stale.
        (await Node(IndexPath(oldHash))).Should().BeNull("the previous index entry is deleted in the same adoption");
    }

    [Fact(Timeout = 60_000)]
    public async Task AdoptKeyHash_RefusesAnythingThatIsNotAHash()
    {
        var service = Service();
        // A raw key is exactly what must never be sent here — shape-refused before any read.
        var act = () => service.AdoptKeyHash("anything", "mwi_notahash").Timeout(TimeSpan.FromSeconds(10)).Await();
        await act.Should().ThrowAsync<ArgumentException>("the registry stores hashes only");
    }
}
