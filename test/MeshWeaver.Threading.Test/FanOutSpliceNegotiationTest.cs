using System.Collections.Concurrent;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Threading.Test;

/// <summary>
/// The wiring half of #1284's fan-out change, asserted against the running mesh rather than inferred.
///
/// <para><see cref="MeshWeaver.Data.Test"/>'s <c>FanOutStringSpliceTest</c> pins what a splice DOES —
/// how it is produced, folded, and refused. What it cannot say is whether the framework's own
/// subscribers actually ask for one, and that is the single point on which the whole negotiation
/// turns: a capability nobody declares is a feature that never runs, and the failure is silent in
/// exactly the direction that looks fine (everyone keeps getting whole values, the measurement never
/// moves, no test goes red).</para>
///
/// <para>So this asserts the declaration itself, on real <c>SubscribeRequest</c>s as the owning node
/// hubs receive them. The recorder is the same passive shape
/// <c>StreamingCellWriteByteCountTest</c> uses for <c>PatchDataRequest</c>: it reads the request and
/// hands the delivery back UNPROCESSED, so the owner's real subscribe handler still runs.</para>
/// </summary>
public class FanOutSpliceNegotiationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>
    /// Recorded with the TARGET they were addressed to, so the assertion can name the one node
    /// under test. Asserting over every subscribe the mesh happens to post during startup would
    /// make this test fail for reasons that have nothing to do with the capability — and any
    /// subscribe posted by something other than <c>JsonSynchronizationStream</c> is a different
    /// question from the one asked here.
    /// </summary>
    private readonly ConcurrentBag<(string? Target, SubscribeRequest Request)> subscribes = [];

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureDefaultNodeHub(config => config.WithHandler<SubscribeRequest>((_, delivery) =>
            {
                subscribes.Add((delivery.Target?.ToString(), delivery.Message));
                return delivery;
            }));

    [Fact]
    public async Task TheFrameworksOwnSubscribers_AskForSplices()
    {
        const string id = "splice-negotiation";
        var path = $"{TestPartition}/{id}";
        await NodeFactory.CreateNode(new MeshNode(id, TestPartition)
        {
            Name = "Negotiation",
            NodeType = "Markdown",
            Content = "# Negotiation",
        }).Should().Emit();

        // A read through the ordinary surface — the same one the GUI, the routing layer and every
        // cross-hub write resolve through — is what opens a subscription to the owning node hub.
        await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null)
            .FirstAsync().Timeout(30.Seconds()).ToTask();

        var recorded = subscribes
            .Where(s => s.Target is not null
                        && s.Target.EndsWith(path, StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Request)
            .ToList();
        recorded.Should().NotBeEmpty(
            "reading a node opens a SubscribeRequest against its owning hub — with none recorded this "
            + "test would pass on no evidence");
        Output.WriteLine(
            $"SubscribeRequests for {path}: {recorded.Count}, "
            + $"declaring AcceptsStringSplice: {recorded.Count(r => r.AcceptsStringSplice)}");

        recorded.Should().AllSatisfy(r => r.AcceptsStringSplice.Should().BeTrue(
            "every subscribe this framework posts is posted by the same assembly that folds the "
            + "frames (JsonSynchronizationStream), so the claim is always safe to make — and if it "
            + "were ever NOT made, the owner would keep shipping whole values and the fan-out fix "
            + "would silently do nothing"));
    }
}
