using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// A reply's own journey is written onto the REQUEST's trail (#4072; the merge-queue failure of
/// 2026-09-13 on <c>DanglingNodeTypeUpdateTest</c>, whose trail ended
/// <c>RESPONSE_POSTED … ⇒ the reply was lost between the responder and the requester, so chase the
/// response delivery</c> — with nothing to chase it BY, because a reply is posted under a fresh id
/// nobody awaits). The ledger now aliases the reply's id to the request, so every stage the reply
/// passes lands on the request's trail as a reply sub-trail, and the verdict reads it.
/// </summary>
public class ReplyTrailFollowsTheRequestTest(ITestOutputHelper output) : HubTestBase(output)
{
    private record Probe : IRequest<ProbeAck>;

    private record ProbeAck;

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithTypes(typeof(Probe), typeof(ProbeAck))
            .WithHandler<Probe>((hub, delivery) =>
            {
                hub.Post(new ProbeAck(), o => o.ResponseFor(delivery));
                return delivery.Processed();
            });

    [Fact(Timeout = 120_000)]
    public async Task TheReplysOwnStagesAreRenderedOnTheRequestsTrail()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = GetHost();
        var client = GetClient();
        var requestId = Guid.NewGuid().ToString("N");

        // Cross-hub on purpose: the reply has to travel client ← mesh ← host, so its sub-trail has
        // more than one hub on it and a "lost between the responder and the requester" verdict
        // would have several places to point at.
        var response = client.Observe((object)new Probe(), o => o.WithTarget(host.Address), requestId)!
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);
        (await response).Message.Should().BeOfType<ProbeAck>();

        var trail = client.DescribeRequestFate(requestId);
        Output.WriteLine($"fate trail: {trail}");

        trail.Should().Contain("RESPONSE_POSTED type=ProbeAck", "the responder's post is on the request's trail");
        trail.Should().Contain("↩ reply#1:", "the reply's own journey is rendered as a sub-trail of the request");
        var replyTrail = trail[(trail.IndexOf("↩ reply#1:", StringComparison.Ordinal) + 1)..];
        replyTrail.Should().Contain($"RECEIVED runLevel=Started@{host.Address}",
            "the reply enters the responder's own intake first");
        replyTrail.Should().Contain($"RECEIVED runLevel=Started@{client.Address}",
            "…and the requester's intake last");
        replyTrail.Should().Contain($"HANDLER_EXIT state=Processed@{client.Address}",
            "the requester's callback rule ran on it");
        trail.Should().NotContain("chase the response delivery",
            "a trail that carries the reply's journey no longer has to ask the reader to chase it");
    }
}
