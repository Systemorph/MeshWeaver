using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Orleans port of <c>ThreeNodePropagationTest</c>: minimal repro of the
/// cross-grain MeshNode synchronization protocol.
///
/// <para>
/// Topology:
/// </para>
/// <code>
///        a (owning per-grain hub at User/TestUser/a — full mesh-node setup
///           via the standard Markdown NodeType: AddMeshDataSource +
///           AddDefaultLayoutAreas + persistence)
///       / \
///      ↕   ↕   (two streams: b↔a, c↔a — both subscribe to a's MeshNode
///     b     c    via workspace.GetRemoteStream&lt;MeshNode, MeshNodeReference&gt;)
/// </code>
///
/// <para>
/// Flow under test:
/// </para>
/// <list type="number">
///   <item>Create node <c>a</c> via <c>CreateNodeRequest</c> — the routing layer
///   activates the per-grain hub with the Markdown NodeType's full configuration.</item>
///   <item>Two test clients <c>b</c> and <c>c</c> each open a remote
///   <see cref="MeshNodeReference"/> stream to <c>a</c>'s address.</item>
///   <item>Both receive the initial snapshot — proves cross-grain subscription works.</item>
///   <item><c>c</c> calls <c>.Update(...)</c> on its stream — the synchronization
///   protocol carries the patch through Orleans routing to <c>a</c>'s grain hub.</item>
///   <item><c>a</c>'s MeshDataSource validates and applies the patch, then
///   broadcasts the change.</item>
///   <item><c>b</c>'s subscription must observe the new state — propagation
///   through the owning grain.</item>
/// </list>
///
/// <para>
/// Counterpart: <c>ThreeNodePropagationTest</c> in the monolith suite passes —
/// proves the bug is Orleans-specific. This test is the focused canary for
/// the cross-grain stream propagation cluster.
/// </para>
/// </summary>
[Collection(nameof(OrleansClusterCollection))]
public class OrleansThreeNodePropagationTest(SharedOrleansFixture fixture, ITestOutputHelper output)
    : OrleansSharedTestBase(fixture, output)
{
    [Fact(Timeout = 60_000)]
    public async Task ChangeInC_PropagatesViaA_ToB_AcrossGrains()
    {
        var ct = new CancellationTokenSource(50.Seconds()).Token;
        Output.WriteLine("[test] start");

        // 1. Pick a unique owning path so test reruns don't collide on the
        //    shared TestCluster state.
        var aId = $"three-node-a-{Guid.NewGuid():N}";
        var pathA = $"User/TestUser/{aId}";

        // 2. Create node `a` via the canonical CreateNodeRequest path. The mesh
        //    hub processes the create; the per-grain hub activates lazily on
        //    first inbound message (the GetRemoteStream subscriptions below).
        //    Using Markdown NodeType pulls in the full mesh-node setup
        //    (AddMeshDataSource + AddDefaultLayoutAreas via ConfigureDefaultNodeHub)
        //    so the per-grain hub has the complete data layer wired up — same
        //    as production.
        var creator = await GetClientAsync($"creator-{Guid.NewGuid():N}", "TestUser");
        var createResp = await creator.Observe(
                new CreateNodeRequest(new MeshNode(aId, "User/TestUser")
                {
                    Name = "A0",
                    NodeType = "Markdown",
                }),
                o => o.WithTarget(new Address("User/TestUser")))
            .FirstAsync().ToTask(ct);
        createResp.Message.Success.Should().BeTrue(createResp.Message.Error);
        Output.WriteLine($"[test] CreateNode succeeded: {pathA}");

        // 3. Two distinct test clients (b, c). Each gets its own routing-
        //    registered address; the silo-side IRoutingService is wired to
        //    stream messages back.
        var hubB = await GetClientAsync($"three-b-{Guid.NewGuid():N}", "TestUser");
        var hubC = await GetClientAsync($"three-c-{Guid.NewGuid():N}", "TestUser");

        // 4. Both b and c open a remote MeshNodeReference stream to a's grain.
        var streamFromB = hubB.GetWorkspace().GetRemoteStream<MeshNode, MeshNodeReference>(
            new Address(pathA), new MeshNodeReference());
        var streamFromC = hubC.GetWorkspace().GetRemoteStream<MeshNode, MeshNodeReference>(
            new Address(pathA), new MeshNodeReference());

        // 5. Capture every emission b sees.
        var bSnapshots = new List<string?>();
        using var subB = streamFromB
            .Select(ci => ci.Value?.Name)
            .Subscribe(name =>
            {
                lock (bSnapshots) bSnapshots.Add(name);
                Output.WriteLine($"[b] emission #{bSnapshots.Count}: {name ?? "(null)"}");
            });

        // 6. Wait for b's initial snapshot.
        await WaitFor(() => { lock (bSnapshots) return bSnapshots.Contains("A0"); }, 20.Seconds(), ct);
        Output.WriteLine($"[b] received initial 'A0'");

        // 7. Wait for c's initial snapshot too — needed before .Update so the
        //    transform sees the latest current.
        var cSnapshots = new List<string?>();
        using var subC = streamFromC
            .Select(ci => ci.Value?.Name)
            .Subscribe(name =>
            {
                lock (cSnapshots) cSnapshots.Add(name);
                Output.WriteLine($"[c] emission #{cSnapshots.Count}: {name ?? "(null)"}");
            });
        await WaitFor(() => { lock (cSnapshots) return cSnapshots.Contains("A0"); }, 20.Seconds(), ct);

        // 8. C writes via its remote stream Update. The synchronization protocol
        //    carries the patch through Orleans routing to a's grain.
        Output.WriteLine("[c] issuing .Update to set Name='A1'");
        streamFromC.Update(current =>
        {
            if (current is null) return null;
            var updated = current with { Name = "A1" };
            return new ChangeItem<MeshNode>(
                updated, streamFromC.StreamId, streamFromC.StreamId,
                ChangeType.Patch, streamFromC.Hub.Version,
                [new EntityUpdate(nameof(MeshNode), updated.Id, updated) { OldValue = current }]);
        });

        // 9. B's subscription must observe the new state — propagation via a's grain.
        await WaitFor(() => { lock (bSnapshots) return bSnapshots.Contains("A1"); }, 30.Seconds(), ct);
        lock (bSnapshots) bSnapshots.Should().Contain("A1",
            "b's subscription must observe c's update via a's grain broadcast");
        Output.WriteLine($"[b] saw 'A1' propagation");
    }

    private static async Task WaitFor(Func<bool> predicate, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!predicate())
        {
            ct.ThrowIfCancellationRequested();
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Predicate did not become true within {timeout}.");
            await Task.Delay(100, ct);
        }
    }
}
