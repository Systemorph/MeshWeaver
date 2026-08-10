#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Pins where node CRUD is EXECUTED — the half <see cref="NodeOperationTargetTest"/> cannot see.
/// That test asserts which address the operation is aimed at; this one asserts which hub actually
/// ran it, by reading the <c>Sender</c> off the real response delivery. The handler answers with
/// <c>hub.Post(response, o =&gt; o.ResponseFor(request))</c>, so the response's sender IS the hub whose
/// action block did the work.
///
/// <para>🚨 Why the sender is the interesting half. The mesh hub is the mesh's ROUTER. The
/// <c>NodeOperationTarget</c> fallback used to be <c>hub.GetMeshHub().Address</c>, so every create /
/// delete / move from a hub that declared no execution target of its own ran on the router's single
/// -threaded action block — starving real <c>SubscribeRequest</c> traffic (prod 2026-06-11) — and
/// every message that work then sent went out stamped <c>Sender = mesh/{id}</c>. The second half is
/// the volume driver: <c>ROUTER_TRAFFIC</c> reports the sender role at the RECEIVING hub, so the
/// line count scales with the number of per-node hubs rather than with the number of message types.
/// Production 2026-08 logged <c>"RawJson has the mesh hub as sender (sender: mesh/…, target:
/// …/Source/FNodeTypeAtomicSolution)"</c> once per node hub.</para>
///
/// <para>The assertion runs <see cref="RouterTrafficRule"/> — the detector's own predicate — over
/// the real delivery, so a regression here is exactly one production ERROR line.</para>
/// </summary>
public class NodeOperationOriginTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    [Fact(Timeout = 60_000)]
    public async Task NodeCreate_IsExecutedByTheDedicatedHub_NeverByTheRouter()
    {
        // A plain client hub: the common shape that declares no execution target of its own and
        // therefore takes the fallback — the path that used to land on the router.
        var client = GetClient();
        var target = client.NodeOperationTarget();

        var path = $"{TestPartition}/Origin-{Guid.NewGuid():N}";
        var node = MeshNode.FromPath(path) with
        {
            Name = "Origin Probe",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
        };

        var response = await AwaitResponseAsync(new CreateNodeRequest(node), o => o.WithTarget(target), client);

        response.Message.Error.Should().BeNull(
            "the create must genuinely succeed — a rejected op would prove nothing about where work runs");

        // The hub that answered is the hub that ran the create.
        response.Sender.Should().NotBeNull();
        response.Sender!.Type.Should().NotBe(AddressExtensions.MeshType,
            "node CRUD must never execute on the ROUTER's action block");
        response.Sender.Should().Be(Mesh.NodeOperationExecutionHub()!.Address,
            "it belongs on the mesh's dedicated off-router node-operation hub");

        // The detector's own predicate, applied to the real delivery: no role at all means the
        // router is neither end of it — no ROUTER_TRAFFIC line, in production or here.
        RouterTrafficRule.RoleOf(client.Address.Type, response.Sender.Type, response.Message)
            .Should().BeNull("the router must be neither the sender nor the target of node CRUD");
    }
}
