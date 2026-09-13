// <meshweaver>
// Id: Testing/HostingOrleans/OrleansChangeFeedCompositionTest
// DisplayName: Testing/HostingOrleans/OrleansChangeFeedCompositionTest — migrated from xunit (convert-xunit-to-inmesh.py)
// </meshweaver>
#nullable enable
using MeshWeaver.Reactive.Assertions;
using MeshWeaver.Testing.InMesh;
using System;
#pragma warning disable CS1591

using System.Text.Json;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Pins the default Orleans registration order at the production seam. Storage notifications and
/// direct logical publishes must reach the same process-local invalidation feed even when the
/// Orleans wrapper registration order changes.
/// </summary>
public class OrleansChangeFeedCompositionTest
{
    [MeshFact]
    public async Task DefaultComposition_UsesOneStorageRelayedLocalInvalidationFeed()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOrleansMeshServices();
        await using var provider = services.BuildServiceProvider();

        var local = provider.GetRequiredService<InProcessMeshChangeFeed>();
        var invalidations = provider.GetRequiredService<IMeshInvalidationFeed>();
        var logical = provider.GetRequiredService<IMeshChangeFeed>();
        invalidations.Should().BeSameAs(local,
            "Orleans and AddMeshCatalog must expose the storage-relayed process singleton");

        MeshChangeEvent? last = null;
        var count = 0;
        using var subscription = invalidations.Subscribe(change =>
        {
            last = change;
            count++;
        });
        var stored = new MeshNode("plugins", "Hosting/PlatformBuilds")
        {
            NodeType = "Hosting/Publication",
            Version = 81,
            State = MeshNodeState.Active,
        };

        await provider.GetRequiredService<IStorageAdapter>()
            .Write(stored, new JsonSerializerOptions())
            .Should().Emit();
        count.Should().Be(1);
        last!.Version.Should().Be(81);

        logical.Publish(MeshChangeEvent.Updated(stored with { Version = 82 }));
        count.Should().Be(2,
            "the logical feed selected by Orleans must fan into this same invalidation singleton");
        last!.Version.Should().Be(82);
    }
}
