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

    [Fact]
    public async Task GetRemoteStream_TwiceForSameKey_ReturnsSameInstance()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = $"{TargetNamespace}/alpha";

        await NodeFactory.CreateNode(MakeNode("alpha", "Alpha")).FirstAsync().ToTask(ct);

        var workspace = Mesh.GetWorkspace();
        var first = workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
            new Address(path), new MeshNodeReference());
        var second = workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
            new Address(path), new MeshNodeReference());

        ReferenceEquals(first, second).Should().BeTrue(
            "the workspace caches per (address, reference); repeated GetRemoteStream calls return the cached instance");
    }

    [Fact]
    public async Task GetRemoteStream_AfterDispose_ReturnsFreshInstance()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = $"{TargetNamespace}/beta";

        await NodeFactory.CreateNode(MakeNode("beta", "Beta")).FirstAsync().ToTask(ct);

        var workspace = Mesh.GetWorkspace();

        var stream = workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
            new Address(path), new MeshNodeReference());

        // Wait for the stream to actually wire up so disposal has something to tear down.
        await stream
            .Where(c => c.Value != null && c.Value.Name == "Beta")
            .FirstAsync().Timeout(System.TimeSpan.FromSeconds(15)).ToTask(ct);

        // Dispose the stream — the cache must drop the entry so the next caller
        // doesn't get a dead instance.
        stream.Dispose();

        var fresh = workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
            new Address(path), new MeshNodeReference());

        ReferenceEquals(fresh, stream).Should().BeFalse(
            "after disposal the cache must evict the dead stream and a subsequent GetRemoteStream must return a brand new one");

        // Sanity — the fresh stream is alive and serves the current value.
        var revived = await fresh
            .Where(c => c.Value != null && c.Value.Name == "Beta")
            .FirstAsync().Timeout(System.TimeSpan.FromSeconds(15)).ToTask(ct);
        revived.Value!.Name.Should().Be("Beta");
    }
}
