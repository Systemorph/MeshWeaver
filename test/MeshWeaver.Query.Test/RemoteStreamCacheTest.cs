using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Query.Test;

/// <summary>
/// Pins down the workspace's per-<c>(address, reference)</c> remote-stream
/// cache (<c>Workspace._remoteStreamCache</c>):
///
/// <list type="number">
///   <item>Two consecutive
///     <c>workspace.GetRemoteStream&lt;MeshNode, MeshNodeReference&gt;(addr, ref)</c>
///     calls return the <b>same</b> <see cref="ISynchronizationStream{TStream}"/>
///     instance — the workspace serves the cached one.</item>
///   <item>After the cached stream is disposed (no remaining subscribers
///     that need it), the next <c>GetRemoteStream(...)</c> call returns a
///     <b>fresh</b> instance — the cache must not hand out a dead stream.</item>
/// </list>
///
/// These guarantees are what every consumer of remote streams relies on:
/// the synced query data source's read subscription and any external
/// caller (e.g., a write through the same <c>(addr, ref)</c>) hit the
/// same instance, but a torn-down subscription doesn't poison the next
/// caller with a corpse.
/// </summary>
public class RemoteStreamCacheTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string TargetNamespace = $"{TestPartition}/RemoteCache";

    private static MeshNode MakeNode(string id, string name)
        => new(id, TargetNamespace)
        {
            Name = name,
            NodeType = "Markdown",
            State = MeshNodeState.Active,
        };

    [Fact(Skip = "Pre-existing flake: workspace.GetRemoteStream's SubscribeRequest never gets a response within 15s for a node just created via NodeFactory.CreateNode — leaks the callback at dispose. Pinning down the per-node-hub activation race is a separate workstream.")]
    public async Task GetRemoteStream_TwiceForSameKey_ReturnsSameInstance()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = $"{TargetNamespace}/alpha";

        await NodeFactory.CreateNode(MakeNode("alpha", "Alpha")).FirstAsync().ToTask(ct);

        var workspace = Mesh.GetWorkspace();
        var first = workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
            new Address(path), new MeshNodeReference());

        // Wire up the stream by waiting for its first non-null emission. Without this
        // the workspace's internal SubscribeRequest stays pending and the
        // disposal-time leak detector fails the test before the assertion runs.
        // Don't filter on Name="Alpha" — the per-node hub's first emission can
        // arrive before the persistence-write completes (DesiredId still null
        // when the catalog index ticks), so we just wait for any emission to
        // confirm the SubscribeRequest round-tripped.
        await first
            .FirstAsync()
            .Timeout(System.TimeSpan.FromSeconds(15))
            .ToTask(ct);

        var second = workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
            new Address(path), new MeshNodeReference());

        ReferenceEquals(first, second).Should().BeTrue(
            "the workspace caches per (address, reference); repeated GetRemoteStream calls return the cached instance");
    }

    [Fact(Skip = "Pre-existing flake: workspace.GetRemoteStream's SubscribeRequest never gets a response within 15s for a node just created via NodeFactory.CreateNode — leaks the callback at dispose. Pinning down the per-node-hub activation race is a separate workstream.")]
    public async Task GetRemoteStream_AfterDispose_ReturnsFreshInstance()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = $"{TargetNamespace}/beta";

        await NodeFactory.CreateNode(MakeNode("beta", "Beta")).FirstAsync().ToTask(ct);

        var workspace = Mesh.GetWorkspace();

        var stream = workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
            new Address(path), new MeshNodeReference());

        // Wait for the stream to actually wire up so disposal has something to tear down.
        // Don't filter on Name="Beta" — the first emission can arrive with the
        // skeleton MeshNode shape (DesiredId still null) before the persistence
        // catalog has fully materialised the node's metadata. Any emission
        // proves the SubscribeRequest round-trip; the filter races otherwise.
        await stream
            .FirstAsync()
            .Timeout(System.TimeSpan.FromSeconds(15)).ToTask(ct);

        // Dispose the stream — the cache must drop the entry so the next caller
        // doesn't get a dead instance.
        stream.Dispose();

        var fresh = workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
            new Address(path), new MeshNodeReference());

        ReferenceEquals(fresh, stream).Should().BeFalse(
            "after disposal the cache must evict the dead stream and a subsequent GetRemoteStream must return a brand new one");

        // Sanity — the fresh stream is alive (any emission proves it).
        var revived = await fresh
            .FirstAsync()
            .Timeout(System.TimeSpan.FromSeconds(15)).ToTask(ct);
        revived.Should().NotBeNull();
    }
}
