using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// 🚨 <b>A <c>GetMeshNode</c> re-probe must post under the identity the READ was issued with</b>
/// (#5227, #5231, #5232, #5431, #5433–#5435, #5438, #5439 — the never-null guard's
/// <c>hub=portal/reads-{meshId}, message=GetDataRequest, target=&lt;page path&gt;</c> family).
///
/// <para><b>The defect.</b> <c>GetMeshNodeOutcome</c> answers a <see cref="ErrorType.ShuttingDown"/>
/// NACK — the owner is recycling — by issuing the <see cref="GetDataRequest"/> AGAIN. The first
/// probe is posted on the subscribing thread, where the caller's identity is ambient. The re-probe is
/// posted from the NACK's <c>OnError</c> callback (and, from the second NACK on, from a pacing timer),
/// and it READ THE AMBIENT AGAIN there. <c>hub.Observe</c>'s identity restore covers <c>OnNext</c>
/// only, so the <c>OnError</c> callback runs with whatever identity the NACK delivery itself
/// carries — and the Orleans router's NACK for a deactivating target grain
/// (<c>RoutingGrain.PostFailure</c>) carries NONE. On <c>portal/reads-{meshId}</c>, a
/// <see cref="PostingIdentity.User"/> hub with no user, that is no identity at all. The re-probe was failed closed by the never-null guard and the read settled Unavailable:
/// every page whose root was recycling (a package install, an auto-recycle on a stale build, a roll)
/// logged one Error per reader and lost its pre-rendered body.</para>
///
/// <para><b>The shape reproduced here</b> is exactly that: an issuing hub with the default
/// <see cref="PostingIdentity.User"/> and NO standing identity; a caller whose identity is ambient
/// only for the call; an owner that NACKs the first probe <c>ShuttingDown</c> WITHOUT an
/// AccessContext (as <c>RoutingGrain.PostFailure</c> does) and answers the second. A NACK posted
/// through <c>ResponseFor</c> carries the requester's identity back and hides the defect — that
/// variant passes on the unfixed code, and it is not what the Orleans router sends.</para>
/// </summary>
public class GetMeshNodeReProbeKeepsCallerIdentityTest(ITestOutputHelper output) : HubTestBase(output)
{
    private static readonly AccessContext Reader = new() { ObjectId = "reader-5227", Name = "Reader 5227" };

    [HubFact]
    public async Task ReProbeAfterShuttingDownNack_CarriesTheCallersIdentity()
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var received = new ConcurrentQueue<string>();
        var probes = 0;

        // The OWNER: NACKs the first probe as recycling, answers the second with the node.
        var owner = Mesh.GetHostedHub(
            CreateClientAddress(),
            c => c
                .WithHandler<GetDataRequest>((hub, d) =>
                {
                    received.Enqueue(d.AccessContext?.ObjectId ?? "(null)");
                    if (Interlocked.Increment(ref probes) == 1)
                    {
                        // Posted the way RoutingGrain.PostFailure posts it when the target grain is
                        // deactivating: correlated by request id, carrying NO AccessContext
                        // (DeliveryFailure is exempt from the never-null guard). The ambient is
                        // cleared for the post so the owner's handler identity cannot leak onto it.
                        using (access.SwitchAccessContext(null))
                            hub.Post(
                                new DeliveryFailure(d)
                                {
                                    ErrorType = ErrorType.ShuttingDown,
                                    Message = $"hub {hub.Address} is shutting down",
                                },
                                o => o.WithTarget(d.Sender).WithRequestIdFrom(d));
                    }
                    else
                        hub.Post(new GetDataResponse(new MeshNode(hub.Address.Id, hub.Address.Type), 1),
                            o => o.ResponseFor(d));
                    return d.Processed();
                })
                .WithPostingIdentity(PostingIdentity.System));
        owner.Should().NotBeNull();
        await owner!.Started.WaitAsync(TestTimeouts.Convergence, TestContext.Current.CancellationToken);

        // The ISSUING hub: default PostingIdentity.User and no standing identity — the
        // portal/reads-{meshId} configuration (MeshExtensions.MeshReadHub).
        var issuing = Mesh.GetHostedHub(CreateClientAddress(), c => c);
        issuing.Should().NotBeNull();
        await issuing!.Started.WaitAsync(TestTimeouts.Convergence, TestContext.Current.CancellationToken);

        // The caller's identity is ambient ONLY for the call — exactly as on a request thread.
        IObservable<NodeReadOutcome> read;
        var outcomes = new System.Reactive.Subjects.ReplaySubject<NodeReadOutcome>();
        using (access.SwitchAccessContext(Reader))
        {
            read = issuing.GetMeshNodeOutcome(owner.Address.ToString(), TestTimeouts.Convergence);
            read.Subscribe(outcomes);
        }

        var outcome = await outcomes.Should().Within(TestTimeouts.Convergence).Emit(
            "the read must settle — Present after the re-probe, or Unavailable when the re-probe "
            + "was refused");
        Output.WriteLine($"[TEST] outcome={outcome.Status}; probes received as: {string.Join(", ", received)}");

        received.Should().NotBeEmpty();
        received.First().Should().Be(Reader.ObjectId,
            "PRECONDITION: the first probe is posted on the subscribing thread, where the caller is ambient");
        outcome.Status.Should().Be(NodeReadStatus.Present,
            "a ShuttingDown NACK is 'ask again' — the re-probe must reach the owner and read the node. "
            + "Unavailable here is the production symptom: the re-probe was failed closed by the "
            + "never-null guard because it re-read an ambient identity the NACK callback does not have");
        received.Should().Equal([Reader.ObjectId, Reader.ObjectId],
            "the re-probe is the SAME read, so it must carry the SAME identity the read was issued "
            + "with — never none, and never one borrowed from the thread the NACK happened to land on");
    }
}
