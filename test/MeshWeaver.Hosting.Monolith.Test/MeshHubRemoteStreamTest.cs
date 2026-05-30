using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Regression tests for subscriber-address routing when the MESH HUB
/// itself (not a child hub) opens a remote stream to a node hub.
///
/// <para>Root cause of the original bug:</para>
/// <para>
/// <c>CreateExternalClient</c> posted <c>SubscribeRequest</c> from
/// <c>reduced.Hub</c> (the inner sync hub whose parent IS the mesh hub).
/// <c>HierarchicalRouting</c> skips sender-wrapping when
/// <c>parentHub.Address.Type == MeshType</c> (line 175-176), so
/// <c>request.Sender = sync/{id}</c> (bare, no host qualifier).
/// <c>HandleSubscribeRequest</c> recorded that as the Subscriber.
/// When the node hub later sent <c>DataChangedEvent</c> to <c>sync/{id}</c>,
/// it found its OWN inner hub at that address (same <c>ClientId</c>) and
/// delivered locally — the mesh hub's stream never received it.
/// </para>
///
/// <para>Fix: post from <c>hub</c> (outer mesh hub), so
/// <c>request.Sender = hub.Address</c>. <c>DataChangedEvent</c> sent to
/// <c>hub.Address</c> reaches the mesh hub where <c>RouteStreamMessage</c>
/// forwards it to the correct inner <c>sync/{id}</c> hub.</para>
///
/// <para>This exact pattern is used by <c>NodeTypeService.GetOrCreateWritableStream</c>
/// to receive the NodeType MeshNode after flipping
/// <c>CompilationStatus = Pending</c> — the stream never receiving the
/// response was what blocked the transparent-recompile flow
/// (<see cref="CodeEditRecompileTest"/>).</para>
/// </summary>
public class MeshHubRemoteStreamTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Namespace = $"{TestPartition}/MeshHubStream";

    /// <summary>
    /// The initial DataChangedEvent from the node hub must reach the mesh hub's
    /// inner sync stream — not be delivered locally on the node hub.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task MeshHub_RemoteStream_ReceivesInitialSnapshot()
    {
        var ct = new CancellationTokenSource(30.Seconds()).Token;
        var path = $"{Namespace}/snap1";

        await NodeFactory.CreateNode(new MeshNode("snap1", Namespace)
        {
            Name = "Snapshot",
            NodeType = "Markdown",
        }).ToTask(ct);

        // Open a remote MeshNodeReference stream directly from the MESH HUB's workspace.
        // This is the scenario broken before the fix: mesh hub type = "mesh", so
        // HierarchicalRouting skips sender-wrapping and the DataChangedEvent goes
        // to the node hub's own inner sync hub instead of the mesh hub's.
        var stream = Mesh.GetWorkspace().GetRemoteStream<MeshNode, MeshNodeReference>(
            new Address(path), new MeshNodeReference());

        var received = await stream
            .Where(ci => ci.Value?.Name == "Snapshot")
            .Timeout(10.Seconds())
            .FirstAsync()
            .ToTask(ct);

        received.Value.Should().NotBeNull();
        received.Value!.Name.Should().Be("Snapshot");
    }

    /// <summary>
    /// After the initial snapshot, subsequent node updates must also flow
    /// through the mesh hub's stream (verifies the ongoing DataChangedEvent path).
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task MeshHub_RemoteStream_ReceivesNodeUpdate()
    {
        var ct = new CancellationTokenSource(30.Seconds()).Token;
        var path = $"{Namespace}/upd1";

        await NodeFactory.CreateNode(new MeshNode("upd1", Namespace)
        {
            Name = "V1",
            NodeType = "Markdown",
        }).ToTask(ct);

        // No `using` — the stream is owned by `Workspace._remoteStreamCache` and
        // disposing it here races with the cache + the test base's Mesh.Dispose()
        // cascade. The cache + framework dispose the stream cleanly.
        var stream = Mesh.GetWorkspace().GetRemoteStream<MeshNode, MeshNodeReference>(
            new Address(path), new MeshNodeReference());

        // Capture names for assertion using the IObservable<ChangeItem<MeshNode>> interface.
        // Concurrent-safe accumulator + lock — Subscribe handler and the
        // assertion lambda below both read/write under the same lock so the
        // List<T> snapshot is stable when FluentAssertions inspects it.
        // Since 486e8d22b made every change emit individually (no Buffer),
        // the `await stream.Where(V2).FirstAsync()` synchronisation point can
        // fire BEFORE this independent Subscribe handler has executed for V2.
        var names = new List<string?>();
        using var sub = ((IObservable<ChangeItem<MeshNode>>)stream)
            .Subscribe(ci => { if (ci.Value?.Name is { } n) lock (names) names.Add(n); });

        // Wait for the initial snapshot — proves subscription routing works.
        var initial = await stream
            .Where(ci => ci.Value?.Name == "V1")
            .Timeout(10.Seconds())
            .FirstAsync()
            .ToTask(ct);

        // Now update the node and expect the change to arrive on the same stream.
        await NodeFactory.UpdateNode(initial.Value! with { Name = "V2" }).ToTask(ct);

        await stream
            .Where(ci => ci.Value?.Name == "V2")
            .Timeout(10.Seconds())
            .FirstAsync()
            .ToTask(ct);

        // Poll until BOTH names appear — the separate `sub` Subscribe handler
        // runs independently of the `await … FirstAsync()` synchronisation
        // above, and per-change emissions can interleave such that the await
        // resolves before the sub handler has appended V2.
        await Observable.Interval(50.Milliseconds()).StartWith(0L)
            .Where(_ => { lock (names) return names.Contains("V1") && names.Contains("V2"); })
            .FirstAsync()
            .Timeout(5.Seconds())
            .ToTask(ct);

        string?[] snapshot;
        lock (names) snapshot = names.ToArray();
        snapshot.Should().Contain("V1").And.Contain("V2");
    }

    /// <summary>
    /// Multiple concurrent mesh-hub-level streams to different nodes must each
    /// receive their own DataChangedEvents independently (no cross-stream pollution).
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task MeshHub_MultipleStreams_ReceiveIndependentUpdates()
    {
        var ct = new CancellationTokenSource(30.Seconds()).Token;
        var pathA = $"{Namespace}/multi-a";
        var pathB = $"{Namespace}/multi-b";

        await NodeFactory.CreateNode(new MeshNode("multi-a", Namespace)
        {
            Name = "NodeA",
            NodeType = "Markdown",
        }).ToTask(ct);
        await NodeFactory.CreateNode(new MeshNode("multi-b", Namespace)
        {
            Name = "NodeB",
            NodeType = "Markdown",
        }).ToTask(ct);

        var streamA = Mesh.GetWorkspace().GetRemoteStream<MeshNode, MeshNodeReference>(
            new Address(pathA), new MeshNodeReference());
        var streamB = Mesh.GetWorkspace().GetRemoteStream<MeshNode, MeshNodeReference>(
            new Address(pathB), new MeshNodeReference());

        var receivedA = await streamA.Where(ci => ci.Value?.Name == "NodeA")
            .Timeout(10.Seconds()).FirstAsync().ToTask(ct);
        var receivedB = await streamB.Where(ci => ci.Value?.Name == "NodeB")
            .Timeout(10.Seconds()).FirstAsync().ToTask(ct);

        receivedA.Value!.Name.Should().Be("NodeA");
        receivedB.Value!.Name.Should().Be("NodeB");
    }
}
