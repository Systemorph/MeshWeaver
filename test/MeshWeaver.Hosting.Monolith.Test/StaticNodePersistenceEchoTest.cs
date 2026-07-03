using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// 🚨 Regression repro for the boot-time "SaveMeshNode failed … relation
/// "code.mesh_nodes" does not exist" noise on every portal start (memex-cloud
/// 2026-07-02). Built-in type-definition nodes (Code / Markdown / User, registered via
/// <c>AddMeshNodes</c>) are STATIC-served: <c>MeshDataSource.WithMeshNodes</c> serves them
/// through <c>WithInitialData</c>, bypassing persistence entirely, and the same static
/// source wins again on every activation. The per-node-hub persistence sampler must
/// therefore never post a <c>SaveMeshNodeRequest</c> for them:
/// <list type="bullet">
///   <item>On Postgres the type-def path routes to a schema named after the lowercased
///     type (<c>code</c>/<c>markdown</c>/<c>user</c>) that is BY DESIGN never provisioned
///     (schema creation is gated to partition-owning creates — the ghost-schema
///     invariant), so the echo failed with 42P01 on every boot.</item>
///   <item>On InMemory / FileSystem the echo "succeeds" — writing a degraded duplicate
///     (delegate-typed <c>HubConfiguration</c> and default-suppressed fields are lost)
///     that shadows the static definition in persistence-first readers. That silent
///     success is what this test pins red: activating the hub must leave persistence
///     untouched.</item>
/// </list>
/// See Doc/Architecture/NodeTypeCatalogs.md ("never auto-persisted to a phantom schema").
/// </summary>
public class StaticNodePersistenceEchoTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    [Fact(Timeout = 60000)]
    public async Task ActivatingStaticTypeDefHubs_DoesNotEchoThemIntoPersistence()
    {
        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        var paths = new[] { "Markdown", "Code", "User" };

        // Capture any write echo for the static type-def paths BEFORE activating the hubs.
        var echoes = new ConcurrentQueue<DataChangeNotification>();
        using var echoSub = storage.Changes
            .Where(c => paths.Contains(c.Path, StringComparer.OrdinalIgnoreCase))
            .Subscribe(echoes.Enqueue);

        // Activate each built-in type-def per-node hub — the same thing boot-time NodeType
        // enrichment / static-repo import does in prod — and confirm the static node serves.
        foreach (var path in paths)
        {
            var served = await ReadNode(path)
                .Should().Within(30.Seconds()).Match(n => n is not null);
            served!.Path.Should().Be(path);
        }

        // Negative assertion (sanctioned fixed wait — "confirm nothing happened"): the
        // persistence sampler fires 200 ms after the own-stream emission; give it ample
        // headroom, then confirm no echo write ever reached storage.
        await Task.Delay(1500, TestContext.Current.CancellationToken);

        echoes.Should().BeEmpty(
            "static-served type definitions have no persistence backing — the per-node-hub " +
            "sampler must not auto-persist them (on Postgres this echo is the boot-time 42P01 " +
            "'relation \"code.mesh_nodes\" does not exist' noise)");

        foreach (var path in paths)
        {
            var persisted = await storage.Read(path, Mesh.JsonSerializerOptions)
                .Should().Within(10.Seconds()).Emit();
            persisted.Should().BeNull(
                $"the static type definition '{path}' must never be shadowed by a persisted echo");
        }
    }
}
