using System;
using System.Reactive.Linq;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Pins the persist-before-emit contract of <c>meshService.CreateNode(node)</c>:
/// the <see cref="CreateNodeResponse"/> (and therefore the
/// <see cref="IMeshService.CreateNode"/> observable's emission) must land only
/// AFTER the node is durably persisted, so a caller that reads the node from
/// INSIDE <c>CreateNode(...).Subscribe(...)</c> provably finds it — no
/// eventual-consistency window, no <c>[ROUTE] NotFound</c> race.
///
/// <para>The read is deliberately a single, immediate owner-hub round-trip
/// (<c>Mesh.GetMeshNode</c> → <c>GetDataRequest@{path}</c>) chained off the
/// create's emission via <c>SelectMany</c>. NO polling, NO retry loop, NO
/// <c>Task.Delay</c>: if the create emitted before the persist landed, this
/// first read races the write and returns <c>null</c> → the test fails. The
/// only way it passes is if persist genuinely happens-before emit.</para>
/// </summary>
public class CreateEmitsAfterPersistTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    [Fact(Timeout = 60000)]
    public async Task CreateEmitsAfterPersist_ImmediateReadFindsNode()
    {
        var factory = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var mesh = Mesh;
        var path = $"{TestPartition}/CreatePersist-{Guid.NewGuid():N}";

        // FromPath splits namespace ("TestData") from id — see CrossHubWritePersistenceTest.
        var node = MeshNode.FromPath(path) with
        {
            Name = "Persisted",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
        };

        // The contract probe: subscribe inside CreateNode, then issue ONE
        // authoritative owner-hub read immediately on the create's emission.
        // Because GetMeshNode posts GetDataRequest to a FRESH per-node hub at
        // {path} (which loads its state from durable storage), this read can
        // only succeed if the node was persisted before CreateNode emitted.
        var readBack = await factory.CreateNode(node)
            .SelectMany(_ => mesh.GetMeshNode(path, ReadNodeTimeout).Take(1))
            .Should().Within(60.Seconds())
            .Match(n => n is not null,
                "the node must be durably persisted before CreateNode emits — " +
                "an immediate read from inside Create().Subscribe() must find it, " +
                "never race the persist into a [ROUTE] NotFound");

        readBack!.Path.Should().Be(path);
        readBack.Name.Should().Be("Persisted");
    }
}
