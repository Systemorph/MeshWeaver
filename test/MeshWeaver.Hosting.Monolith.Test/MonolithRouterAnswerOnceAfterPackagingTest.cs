using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Subjects;
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
    /// through the per-address activation FIFO — its answer is a real emission the assertion
    /// subscribes for, never a period the test waits out.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task PackagedFireAndForgetAndNacks_AreNotAnswered_WhileOrdinaryTrafficIs()
    {
        // ReplaySubject: the assertion subscribes after the dispatch and must still see the whole
        // history of what the router answered.
        var nacks = new ReplaySubject<string>();
        var answeredSoFar = nacks.Scan(ImmutableList<string>.Empty, (answered, id) => answered.Add(id));

        var client = GetClient(c => c.WithHandler<DeliveryFailure>((_, d) =>
        {
            nacks.OnNext(d.Message.Delivery.Id);
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

        // Cold observables: the subscribe IS the dispatch. RouteInMesh runs inline on subscribe, so
        // all three join the per-address activation FIFO in this order.
        RoutingService.DeliverMessage(heartBeat).Subscribe(_ => { });
        RoutingService.DeliverMessage(failure).Subscribe(_ => { });
        RoutingService.DeliverMessage(control).Subscribe(_ => { });

        var answered = await answeredSoFar.Should().Within(30.Seconds())
            .Match(a => a.Contains(control.Id),
                "ordinary traffic must still get its NACK so hub.Observe fires OnError");

        answered.Should().ContainSingle(
            "answering a [CanBeIgnored] heartbeat re-posts forever against a permanently-gone owner "
            + "(the NotFound storm), and answering a DeliveryFailure with a DeliveryFailure loops").Subject
            .Should().Be(control.Id);
        answered.Should().NotContain(heartBeat.Id);
        answered.Should().NotContain(failure.Id);
    }
}
