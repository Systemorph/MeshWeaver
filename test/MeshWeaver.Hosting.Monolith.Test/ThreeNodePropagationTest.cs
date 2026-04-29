using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Three-node propagation test: minimal repro of the cross-hub MeshNode
/// synchronization protocol.
///
/// <para>
/// Topology:
/// </para>
/// <code>
///        a (owning hub, holds the MeshNode)
///       / \
///      ↕   ↕    (two streams: b↔a, c↔a — both subscribe to a's MeshNode)
///     b     c
/// </code>
///
/// <para>
/// Flow under test:
/// </para>
/// <list type="number">
///   <item>Create node <c>a</c> (own hub at <c>TestPartition/a</c>) with initial state.</item>
///   <item>Two client hubs <c>b</c> and <c>c</c> each open a remote
///   <see cref="MeshNodeReference"/> stream to <c>a</c>.</item>
///   <item>Both receive the initial snapshot — proves the streams are wired.</item>
///   <item><c>c</c> calls <c>.Update(...)</c> on its stream — the synchronization
///   protocol carries the patch to <c>a</c>'s owning hub.</item>
///   <item><c>a</c>'s MeshDataSource validates and applies the patch, then
///   broadcasts the change.</item>
///   <item><c>b</c>'s subscription must observe the new state.</item>
/// </list>
///
/// <para>
/// This is the same bug class as <c>OrleansHostedHubRoutingTest.ThreadHub_LocalWorkspaceWrite_VisibleViaGetDataRequest</c>
/// but without Orleans grain machinery — fast iteration, easy to step through
/// in a debugger.
/// </para>
/// </summary>
public class ThreeNodePropagationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    [Fact(Timeout = 30_000)]
    public async Task ChangeInC_PropagatesViaA_ToB()
    {
        Output.WriteLine("[test] start");
        var ct = new CancellationTokenSource(20.Seconds()).Token;

        // 1. Create the owning node `a` with an initial Name.
        var pathA = $"{TestPartition}/a";
        Output.WriteLine($"[test] before CreateNode pathA={pathA}");
        await NodeFactory.CreateNode(
            new MeshNode("a", TestPartition) { Name = "A0", NodeType = "Markdown" });
        Output.WriteLine("[test] CreateNode succeeded");

        // 2. Create two distinct client hubs (b, c). Each gets its own address +
        //    AddData() so its workspace can host a remote-MeshNodeReference stream;
        //    the routing service registers them so responses from `a` route back.
        var hubB = Mesh.ServiceProvider.CreateMessageHub(
            new Address("client", "b"),
            c => ConfigureClient(c).AddData())!;
        var hubC = Mesh.ServiceProvider.CreateMessageHub(
            new Address("client", "c"),
            c => ConfigureClient(c).AddData())!;

        // 3. Both b and c open a remote stream to a's MeshNode.
        var streamFromB = hubB.GetWorkspace().GetRemoteStream<MeshNode, MeshNodeReference>(
            new Address(pathA), new MeshNodeReference());
        var streamFromC = hubC.GetWorkspace().GetRemoteStream<MeshNode, MeshNodeReference>(
            new Address(pathA), new MeshNodeReference());

        // 4. Capture every emission b sees so we can assert the propagation.
        var bSnapshots = new List<string?>();
        using var subB = streamFromB
            .Select(ci => ci.Value?.Name)
            .Subscribe(name =>
            {
                bSnapshots.Add(name);
                Output.WriteLine($"[b] emission #{bSnapshots.Count}: {name ?? "(null)"}");
            });

        // 5. Wait for b's initial snapshot — proves the stream is wired up.
        await WaitFor(() => bSnapshots.Contains("A0"), 10.Seconds(), ct);
        Output.WriteLine($"[b] received initial snapshot 'A0' after {bSnapshots.Count} emission(s)");

        // 6. Likewise wait for c — its Update closure receives the latest node so
        //    we want it to have seen at least one emission first (otherwise the
        //    update transform runs against null/stale state).
        var cSnapshots = new List<string?>();
        using var subC = streamFromC
            .Select(ci => ci.Value?.Name)
            .Subscribe(name =>
            {
                cSnapshots.Add(name);
                Output.WriteLine($"[c] emission #{cSnapshots.Count}: {name ?? "(null)"}");
            });
        await WaitFor(() => cSnapshots.Contains("A0"), 10.Seconds(), ct);

        // 7. C writes via its stream. The synchronization protocol carries the
        //    patch to a's owning hub, which validates + applies + broadcasts.
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

        // 8. B's subscription must observe the new state — propagation through a.
        await WaitFor(() => bSnapshots.Contains("A1"), 10.Seconds(), ct);
        bSnapshots.Should().Contain("A1",
            "b's subscription must observe c's update via a's broadcast");
        Output.WriteLine($"[b] saw 'A1' propagation after {bSnapshots.Count} emission(s)");
    }

    private static async Task WaitFor(Func<bool> predicate, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!predicate())
        {
            ct.ThrowIfCancellationRequested();
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Predicate did not become true within {timeout}.");
            await Task.Delay(50, ct);
        }
    }
}
