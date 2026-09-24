using System;
using System.Reactive.Linq;
using MeshWeaver.AI;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚪 <b>A pod-hub ADDRESS is not a mesh node path</b> —
/// <see href="https://github.com/Systemorph/MeshWeaver/issues/5120">#5120</see>.
///
/// <para><b>The defect.</b> <c>portal/nodeops-{meshId}</c>, <c>cache/{meshId}</c>,
/// <c>mesh/{id}</c> and the other stream-routed address types route to a hub hosted in a pod's
/// process, never to a node — and none of those hubs owns a node: they carry a workspace
/// (<c>AddData()</c>) but no <c>MeshNodeReference</c> reducer. A node read handed such an address
/// went through the mesh-node cache, which opened an upstream <c>SubscribeRequest</c> to it; the hub
/// refused it with a <c>DataSourceConfigurationException</c> logged at Error, and the reader got a
/// fault it reported as "no verdict". Measured on memex: pod <c>…-4dpcn</c> logged
/// <c>FetchNode UNAVAILABLE for cache/Hj7OStRhsEG9wL7lLfx7Cg</c> in the same millisecond as the
/// refusal on <c>…-xs87x</c> — an MCP / API <c>get</c> of another pod's cache address, copied out of
/// a log line.</para>
///
/// <para><b>The measurement.</b> The same <c>get</c>, on this process's own pod-hub addresses. On the
/// unfixed build it answers with the UNAVAILABLE sentinel ("it is UNKNOWN whether this node exists")
/// about an address whose answer was never in doubt; fixed, it answers <c>Not found</c>. The stream
/// seam (<c>GetMeshNodeStream</c> through the cache) is measured on its own, because it is the route
/// the refusal travelled.</para>
///
/// <para><b>Negative control, measured on the unfixed build</b> (the cache guard and the
/// <c>DefaultIfEmpty</c> in <c>GetWithBrokenNodeTypeFallback</c> reverted): the two <c>get</c> facts
/// and the stream fact FAIL — <c>get</c> answers <c>Unavailable: … faulted: DeliveryFailureException</c>
/// and the stream errors with the reducer refusal, the production shape. The one-shot
/// <c>GetMeshNodeOutcome</c> fact PASSES on both builds: that route already answered <c>Absent</c>, so
/// it is a regression pin, not the repro.</para>
/// </summary>
public class APodHubAddressIsNotANodePathTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private string NodeOpsAddress => Mesh.NodeOperationIssuingHub().Address.ToString();
    private string CacheAddress => new Address("cache", Mesh.Address.Id).ToString();

    /// <summary>
    /// <c>get</c> of this process's node-CRUD hub address answers <c>Not found</c>, never the
    /// UNAVAILABLE sentinel — the path cannot hold a node, so there is a verdict.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Get_OfTheNodeOperationHubAddress_AnswersNotFound()
    {
        // Control: the address really is a live, hosted hub — otherwise "Not found" would be true
        // for the boring reason that nothing is there, and the assertion would measure nothing.
        Mesh.NodeOperationIssuingHub().Should().NotBeSameAs(Mesh);

        var answer = await new MeshOperations(Mesh).Get("@" + NodeOpsAddress)
            .Should().Within(TimeSpan.FromSeconds(20))
            .Emit("get must ANSWER — a pod-hub address is a question with a definite answer",
                TestContext.Current.CancellationToken);

        Output.WriteLine($"DIAG get({NodeOpsAddress}) = {answer}");
        answer.Should().StartWith($"Not found: {NodeOpsAddress}",
            "no mesh node can exist at a pod-hub address — the router sends it to a hub that owns "
            + "no node — so the truthful answer is 'not found', not 'unknown, retry'");
    }

    /// <summary>
    /// The same for another pod-hub type — the mesh-node cache's own address, the exact shape of the
    /// memex sample (a <c>cache/{meshId}</c> handed to <c>get</c>).
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Get_OfTheCacheHubAddress_AnswersNotFound()
    {
        var answer = await new MeshOperations(Mesh).Get("@" + CacheAddress)
            .Should().Within(TimeSpan.FromSeconds(20))
            .Emit("get must ANSWER — a pod-hub address is a question with a definite answer",
                TestContext.Current.CancellationToken);

        Output.WriteLine($"DIAG get({CacheAddress}) = {answer}");
        answer.Should().StartWith($"Not found: {CacheAddress}");
    }

    /// <summary>
    /// The one-shot seam: <c>GetMeshNodeOutcome</c> answers <see cref="NodeReadStatus.Absent"/> for a
    /// pod-hub address. It already did before #5120's fix (the hub answers the <c>GetDataRequest</c>
    /// with no data) — a regression pin, so the two read routes keep agreeing.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task GetMeshNodeOutcome_OfAPodHubAddress_IsAbsent()
    {
        var outcome = await Mesh.GetMeshNodeOutcome(NodeOpsAddress, TimeSpan.FromSeconds(10))
            .Should().Within(TimeSpan.FromSeconds(20))
            .Emit("the read must settle", TestContext.Current.CancellationToken);

        Output.WriteLine($"DIAG outcome({NodeOpsAddress}) = {outcome.Status}");
        outcome.Status.Should().Be(NodeReadStatus.Absent,
            "a pod-hub address can never hold a node; anything but Absent reports a question with a "
            + "definite answer as undecided");
    }

    /// <summary>
    /// The stream seam: a live read through the mesh-node cache completes EMPTY — it neither
    /// faults nor emits, because there is no node and there never will be one.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task GetMeshNodeStream_OfAPodHubAddress_CompletesEmpty()
    {
        var reads = Mesh.GetMeshNodeStream(NodeOpsAddress)
            .Select(n => n?.Path ?? "(null)")
            .ToList();

        var emitted = await reads
            .Should().Within(TimeSpan.FromSeconds(20))
            .Emit("the read must COMPLETE — a fault here is the #5120 refusal reaching the reader",
                TestContext.Current.CancellationToken);

        emitted.Should().BeEmpty("no node exists at a pod-hub address, so nothing may be emitted");
    }

    /// <summary>
    /// 🚨 Negative control on the predicate itself: a real node path whose first segment merely
    /// LOOKS like an address type in another case, or contains one further in, is untouched.
    /// </summary>
    [Fact]
    public void ThePredicate_MatchesOnlyAPodHubAddressTypeAsTheFirstSegment()
    {
        var types = MeshConfiguration.DefaultStreamRoutedAddressTypes;
        MeshConfiguration.IsPodHubAddress("portal/nodeops-abc", types).Should().BeTrue();
        MeshConfiguration.IsPodHubAddress("cache/Hj7OStRhsEG9wL7lLfx7Cg", types).Should().BeTrue();
        MeshConfiguration.IsPodHubAddress("mesh/abc", types).Should().BeTrue();

        MeshConfiguration.IsPodHubAddress($"{TestPartition}/portal", types).Should().BeFalse();
        MeshConfiguration.IsPodHubAddress("Portal/Home", types).Should().BeFalse(
            "address types are ordinal — a partition named 'Portal' is a node path");
        MeshConfiguration.IsPodHubAddress("portal", types).Should().BeFalse(
            "a bare segment is not an address");
        MeshConfiguration.IsPodHubAddress(null, types).Should().BeFalse();
        MeshConfiguration.IsPodHubAddress("", types).Should().BeFalse();
    }
}
