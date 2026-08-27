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
///
/// <para>The tests do NOT wait for stream emission — the cache behavior is
/// purely about object identity (same/different instance) and is independent
/// of whether the owning hub emits within a given window. Emission correctness
/// is covered by the per-node-hub activation and SyncedQuery tests.</para>
/// </summary>
public class RemoteStreamCacheTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly string TargetNamespace = $"{TestPartition}/RemoteCache";

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
        var path = $"{TargetNamespace}/alpha";

        await NodeFactory.CreateNode(MakeNode("alpha", "Alpha")).Should().Emit();

        var workspace = Mesh.GetWorkspace();
        var first = ((MeshWeaver.Data.Workspace)workspace).GetRemoteStreamUnchecked<MeshNode, MeshNodeReference>(
            new Address(path), new MeshNodeReference());

        var second = ((MeshWeaver.Data.Workspace)workspace).GetRemoteStreamUnchecked<MeshNode, MeshNodeReference>(
            new Address(path), new MeshNodeReference());

        ReferenceEquals(first, second).Should().BeTrue(
            "the workspace caches per (address, reference); repeated GetRemoteStream calls return the cached instance");

        // Clean up: dispose the stream so the hub's Observe subscription is
        // released before quiescing checks run.
        first.Dispose();
    }

    [Fact]
    public async Task GetRemoteStream_AfterDispose_ReturnsFreshInstance()
    {
        var path = $"{TargetNamespace}/beta";

        await NodeFactory.CreateNode(MakeNode("beta", "Beta")).Should().Emit();

        var workspace = Mesh.GetWorkspace();

        var stream = ((MeshWeaver.Data.Workspace)workspace).GetRemoteStreamUnchecked<MeshNode, MeshNodeReference>(
            new Address(path), new MeshNodeReference());

        // Dispose the stream — the cache must drop the entry so the next caller
        // doesn't get a dead instance.
        stream.Dispose();

        var fresh = ((MeshWeaver.Data.Workspace)workspace).GetRemoteStreamUnchecked<MeshNode, MeshNodeReference>(
            new Address(path), new MeshNodeReference());

        ReferenceEquals(fresh, stream).Should().BeFalse(
            "after disposal the cache must evict the dead stream and a subsequent GetRemoteStream must return a brand new one");

        // Clean up.
        fresh.Dispose();
    }

    /// <summary>
    /// 🚨 A TERMINALLY FAULTED mirror is as dead as a disposed one, and must never be served again
    /// — Systemorph/MeshWeaver#2387.
    ///
    /// <para><b>The defect.</b> A remote mirror's store is a <c>ReplaySubject</c>, so once it takes
    /// an <c>OnError</c> the Rx grammar makes that permanent: it can never emit again, and every
    /// LATER subscriber replays the same error the instant it subscribes. Nothing disposes it —
    /// <c>CreateExternalClient</c>'s terminal arm faults the stream and tears down only its
    /// keep-alive, leaving the object "errored but undisposed" — and the cache judged liveness on
    /// disposal alone, so it kept handing the corpse back for the whole process lifetime.</para>
    ///
    /// <para><b>What that cost in production.</b> Three replicas booted together; a per-node hub
    /// for one package was busy past the mirror's request budget, so that ONE
    /// <c>SubscribeRequest</c> timed out and poisoned the path. Every later write to it failed in
    /// <b>0.07 ms</b> while reporting <c>"no initial state arrived … within 30s"</c> — including
    /// the default installer's own <c>falling back to full install</c> repair, which re-entered
    /// the same dead mirror and could not possibly succeed. Each pod came up missing exactly one
    /// baseline package, and only a restart cleared it.</para>
    ///
    /// <para>The assertion is two-sided: the identity check names the defect, and the replay
    /// checks name the HARM — the corpse really is poisonous, and the replacement really does
    /// start clean.</para>
    /// </summary>
    [Fact]
    public async Task GetRemoteStream_AfterTerminalFault_ReturnsFreshInstance()
    {
        var path = $"{TargetNamespace}/gamma";

        await NodeFactory.CreateNode(MakeNode("gamma", "Gamma")).Should().Emit();

        var workspace = (MeshWeaver.Data.Workspace)Mesh.GetWorkspace();

        var faulted = workspace.GetRemoteStreamUnchecked<MeshNode, MeshNodeReference>(
            new Address(path), new MeshNodeReference());

        // Exactly the terminal a real boot produces: the owning per-node hub did not answer this
        // mirror's SubscribeRequest inside the request budget, so CreateExternalClient's error arm
        // calls reduced.OnError(TimeoutException) and disposes the keep-alive.
        faulted.OnError(new TimeoutException(
            $"No response received for request SubscribeRequest → target {path}."));

        Exception? replayed = null;
        faulted.Subscribe(_ => { }, ex => replayed = ex).Dispose();
        replayed.Should().BeOfType<TimeoutException>(
            "the store is a ReplaySubject: a terminal error is replayed to every later subscriber "
            + "INSTANTLY — which is why serving this instance from the cache converts one slow "
            + "owner round-trip into a permanent failure of the path");

        var fresh = workspace.GetRemoteStreamUnchecked<MeshNode, MeshNodeReference>(
            new Address(path), new MeshNodeReference());

        ReferenceEquals(fresh, faulted).Should().BeFalse(
            "a faulted stream can never emit again, so the cache must evict it and build a new "
            + "mirror — otherwise one transient owner timeout is terminal for the process");

        Exception? onFresh = null;
        fresh.Subscribe(_ => { }, ex => onFresh = ex).Dispose();
        onFresh.Should().BeNull(
            "the replacement must start clean; a fresh mirror that already carries the old "
            + "terminal is the same defect one instance later");

        // Clean up.
        fresh.Dispose();
    }
}
