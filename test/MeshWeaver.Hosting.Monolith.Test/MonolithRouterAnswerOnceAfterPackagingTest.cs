using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// The MONOLITH half of the answer-once contract across the packaging boundary (issue #1485) —
/// the Orleans half is
/// <c>MeshWeaver.Hosting.Orleans.Test.OrleansRouterAnswerOnceAfterPackagingTest</c>.
///
/// <para><b>Why this test exists as its own file.</b> The routers' comments assert that they
/// "both agree" on when a delivery may be answered, and #1485 was filed on the reading that the
/// monolith guards sit BEFORE packaging and therefore still work. They do not: there is exactly
/// ONE call site of <c>IRoutingService.DeliverMessage</c> (<c>MeshBuilder</c>), it packages, and
/// BOTH implementations are behind it. So the two routers did agree — in being uniformly dead.
/// A claim of agreement that nothing executes is how they drifted; this pins it on both sides.</para>
///
/// <para>Drives the REAL <c>IRoutingService</c> of a real monolith mesh (no mocks) with a delivery
/// packaged exactly as <c>MeshBuilder</c> packages it, at an address that resolves to nothing —
/// the <c>RoutingServiceBase.PostNotFound</c> path.</para>
/// </summary>
public class MonolithRouterAnswerOnceAfterPackagingTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private static readonly Address MissingTarget = new("doesnotexist", "missing-node");

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        builder
            .UseMonolithMesh()
            .AddPartitionedInMemoryPersistence()
            .AddGraph()
            .AddMeshNodes(TestUsers.PublicAdminAccess());

    /// <summary>
    /// A packaged <see cref="HeartBeatEvent"/> and a packaged <see cref="DeliveryFailure"/> routed
    /// to a NotFound address must NOT be answered; ordinary traffic must still be. The ordinary
    /// message is dispatched LAST and to the SAME address, so it drains behind the other two
    /// through the per-address activation FIFO — its NACK arriving is the deterministic proof that
    /// the two before it were fully routed.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task PackagedFireAndForgetAndNacks_AreNotAnswered_WhileOrdinaryTrafficIs()
    {
        var nackedDeliveryIds = new ConcurrentQueue<string>();
        var controlAnswered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controlId = string.Empty;

        var client = GetClient(c => c.WithHandler<DeliveryFailure>((_, d) =>
        {
            nackedDeliveryIds.Enqueue(d.Message.Delivery.Id);
            if (d.Message.Delivery.Id == controlId)
                controlAnswered.TrySetResult();
            return d.Processed();
        }));

        IMessageDelivery Packaged<TMessage>(TMessage message)
        {
            var packaged = new MessageDelivery<TMessage>(client.Address, MissingTarget, message,
                    Mesh.JsonSerializerOptions)
                .Package(Mesh.JsonSerializerOptions);
            packaged.Message.Should().BeOfType<RawJson>(
                "MeshBuilder packages every delivery before the router sees it — that erasure IS the defect");
            return packaged;
        }

        var heartBeat = Packaged(new HeartBeatEvent());
        var failure = Packaged(new DeliveryFailure(
            new MessageDelivery<string>(client.Address, MissingTarget, "inner", Mesh.JsonSerializerOptions),
            "inner failure"));
        var control = Packaged("ordinary-payload");
        controlId = control.Id;

        await RoutingService.DeliverMessage(heartBeat).FirstAsync().ToTask();
        await RoutingService.DeliverMessage(failure).FirstAsync().ToTask();
        await RoutingService.DeliverMessage(control).FirstAsync().ToTask();

        await controlAnswered.Task.WaitAsync(TimeSpan.FromSeconds(20));

        var answered = nackedDeliveryIds.Should().ContainSingle(
            "answering a [CanBeIgnored] heartbeat re-posts forever against a permanently-gone owner "
            + "(the NotFound storm), and answering a DeliveryFailure with a DeliveryFailure loops — "
            + "while ordinary traffic must still get its NACK so hub.Observe fires OnError").Subject;
        answered.Should().Be(control.Id);
    }
}
