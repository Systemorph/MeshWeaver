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
    public void MeshHub_RemoteStream_ReceivesInitialSnapshot()
    {
        var path = $"{Namespace}/snap1";

        NodeFactory.CreateNode(new MeshNode("snap1", Namespace)
        {
            Name = "Snapshot",
            NodeType = "Markdown",
        }).Should().Emit();

        // Open a remote MeshNodeReference stream directly from the MESH HUB's workspace.
        // This is the scenario broken before the fix: mesh hub type = "mesh", so
        // HierarchicalRouting skips sender-wrapping and the DataChangedEvent goes
        // to the node hub's own inner sync hub instead of the mesh hub's.
        var stream = Mesh.GetWorkspace().GetMeshNodeStream(path);

        var received = stream
            .Should().Match(ci => ci?.Name == "Snapshot");

        received.Should().NotBeNull();
        received!.Name.Should().Be("Snapshot");
    }

    /// <summary>
    /// After the initial snapshot, subsequent node updates must also flow
    /// through the mesh hub's stream (verifies the ongoing DataChangedEvent path).
    /// </summary>
    [Fact(Timeout = 60_000)]
    public void MeshHub_RemoteStream_ReceivesNodeUpdate()
    {
        var path = $"{Namespace}/upd1";

        NodeFactory.CreateNode(new MeshNode("upd1", Namespace)
        {
            Name = "V1",
            NodeType = "Markdown",
        }).Should().Emit();

        // No `using` — the stream is owned by `Workspace._remoteStreamCache` and
        // disposing it here races with the cache + the test base's Mesh.Dispose()
        // cascade. The cache + framework dispose the stream cleanly.
        var stream = Mesh.GetWorkspace().GetMeshNodeStream(path);

        // Capture names for assertion using the IObservable<ChangeItem<MeshNode>> interface.
        // Concurrent-safe accumulator + lock — Subscribe handler and the
        // assertion lambda below both read/write under the same lock so the
        // List<T> snapshot is stable when inspected.
        // Since 486e8d22b made every change emit individually (no Buffer),
        // the `stream.Where(V2)` synchronisation point can fire BEFORE this
        // independent Subscribe handler has executed for V2.
        var names = new List<string?>();
        using var sub = stream
            .Subscribe(ci => { if (ci?.Name is { } n) lock (names) names.Add(n); });

        // Wait for the initial snapshot — proves subscription routing works.
        var initial = stream
            .Should().Match(ci => ci?.Name == "V1");

        // Now update the node and expect the change to arrive on the same stream.
        NodeFactory.UpdateNode(initial! with { Name = "V2" }).Should().Emit();

        stream.Should().Match(ci => ci?.Name == "V2");

        // Poll until BOTH names appear — the separate `sub` Subscribe handler
        // runs independently of the `stream.Where(...)` synchronisation above,
        // and per-change emissions can interleave such that the match resolves
        // before the sub handler has appended V2.
        Observable.Interval(50.Milliseconds()).StartWith(0L)
            .Should().Within(5.Seconds())
            .Match(_ => { lock (names) return names.Contains("V1") && names.Contains("V2"); });

        string?[] snapshot;
        lock (names) snapshot = names.ToArray();
        snapshot.Should().Contain("V1").And.Contain("V2");
    }

    /// <summary>
    /// Multiple concurrent mesh-hub-level streams to different nodes must each
    /// receive their own DataChangedEvents independently (no cross-stream pollution).
    /// </summary>
    [Fact(Timeout = 60_000)]
    public void MeshHub_MultipleStreams_ReceiveIndependentUpdates()
    {
        var pathA = $"{Namespace}/multi-a";
        var pathB = $"{Namespace}/multi-b";

        NodeFactory.CreateNode(new MeshNode("multi-a", Namespace)
        {
            Name = "NodeA",
            NodeType = "Markdown",
        }).Should().Emit();
        NodeFactory.CreateNode(new MeshNode("multi-b", Namespace)
        {
            Name = "NodeB",
            NodeType = "Markdown",
        }).Should().Emit();

        var streamA = Mesh.GetWorkspace().GetMeshNodeStream(pathA);
        var streamB = Mesh.GetWorkspace().GetMeshNodeStream(pathB);

        var receivedA = streamA.Should().Match(ci => ci?.Name == "NodeA");
        var receivedB = streamB.Should().Match(ci => ci?.Name == "NodeB");

        receivedA!.Name.Should().Be("NodeA");
        receivedB!.Name.Should().Be("NodeB");
    }
}
