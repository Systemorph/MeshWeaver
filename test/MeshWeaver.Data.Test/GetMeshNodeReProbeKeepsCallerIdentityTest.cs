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
///
/// <para><b>It also pins #7395.</b> The owner here NACKs in well under a millisecond, so the NACK
/// regularly reaches the issuing hub before the first probe's <c>Subscribe</c> has returned: the
/// response <c>AsyncSubject</c> replays it synchronously and the re-probe is issued NESTED inside the
/// first probe. When the probe's subscription was assigned to the serial slot only after
/// <c>Subscribe</c> returned, that late assignment disposed the nested re-probe, its response
/// subject was removed, the owner's answer was dropped ("No subject found for response") and the
/// read errored "still recycling after 1 probe(s)" at the end of its budget — on every local run of
/// core main at the time.</para>
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

    /// <summary>
    /// 🚨 <b>A re-probe that is NEVER ANSWERED is not "still recycling"</b> (#7395, after #7418).
    ///
    /// <para>The owner here refuses the first probe <see cref="ErrorType.ShuttingDown"/> and then
    /// receives the re-probe and answers nothing — the shape memex-cloud logged on a build that
    /// contains #7418: one NACK at +0.7 s, then 14 s of silence, reported as
    /// <c>still recycling (ShuttingDown) after 1 probe(s) over 15.0s</c>. That sentence claims a
    /// recycler outlasted the budget, which it did not, and it carried none of the reader/target
    /// diagnostics the plain timeout prints, so the reading could not tell where the re-probe went.
    /// The verdict must say the re-probe was issued and never answered, how long it was outstanding,
    /// and carry those diagnostics.</para>
    /// </summary>
    [HubFact]
    public async Task ReProbeNeverAnswered_IsReportedAsUnanswered_NotAsStillRecycling()
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var probes = 0;

        // The OWNER: NACKs the first probe as recycling, takes the second and never answers it.
        var owner = Mesh.GetHostedHub(
            CreateClientAddress(),
            c => c
                .WithHandler<GetDataRequest>((hub, d) =>
                {
                    if (Interlocked.Increment(ref probes) == 1)
                        using (access.SwitchAccessContext(null))
                            hub.Post(
                                new DeliveryFailure(d)
                                {
                                    ErrorType = ErrorType.ShuttingDown,
                                    Message = $"hub {hub.Address} is shutting down",
                                },
                                o => o.WithTarget(d.Sender).WithRequestIdFrom(d));
                    return d.Processed();
                })
                .WithPostingIdentity(PostingIdentity.System));
        owner.Should().NotBeNull();
        await owner!.Started.WaitAsync(TestTimeouts.Convergence, TestContext.Current.CancellationToken);

        var issuing = Mesh.GetHostedHub(CreateClientAddress(), c => c);
        issuing.Should().NotBeNull();
        await issuing!.Started.WaitAsync(TestTimeouts.Convergence, TestContext.Current.CancellationToken);

        var outcomes = new System.Reactive.Subjects.ReplaySubject<NodeReadOutcome>();
        using (access.SwitchAccessContext(Reader))
            issuing.GetMeshNodeOutcome(owner.Address.ToString(), TestTimeouts.Quick, ReadTimeoutBehavior.EmitNull)
                .Subscribe(outcomes);

        var outcome = await outcomes.Should().Within(TestTimeouts.Convergence).Emit(
            "the read must settle when its budget ends");
        Output.WriteLine($"[TEST] outcome={outcome.Status}; probes received={Volatile.Read(ref probes)}; "
                         + $"error={outcome.Failure?.Message}");

        Volatile.Read(ref probes).Should().Be(2,
            "PRECONDITION: the re-probe reached the owner — what is under test is how its silence is reported");
        outcome.Status.Should().Be(NodeReadStatus.Unavailable,
            "the address was recycling and then silent: indeterminate, never absent");
        var error = outcome.Failure.Should().BeOfType<AddressRecyclingException>(
            "it is still an availability fact the compile pipeline classifies as Unavailable").Which;
        error.Message.Should().Contain("NEVER ANSWERED",
            "the last probe got no answer at all — reporting it as 'still recycling' is the false "
            + "diagnosis memex-cloud logged after #7418");
        error.Message.Should().Contain("2 probe(s) issued",
            "the verdict must count the probes it ISSUED, not only the NACKs it received");
        error.Message.Should().NotContain("still recycling (ShuttingDown) after 1 probe(s)",
            "one NACK followed by silence is not a recycler that refused for the whole budget");
        error.Message.Should().Contain("Reader:",
            "an unanswered probe carries the reader/target diagnostics the plain timeout carries, so "
            + "the next reading can say where the re-probe went");

        // 🚨 The TARGET half must find the owner where per-node hubs live — under the MESH hub —
        // not in the caller's own hosted collection (#7395). The reader here is a SIBLING of the
        // owner, exactly as portal/reads-{meshId} is a sibling of every per-node hub; looked up on
        // the reader, the owner was always "NO LOCAL HUB", so the diagnostics #7600 added could
        // never tell the dying activation from a successor still activating.
        error.Message.Should().NotContain("NO LOCAL HUB",
            "the owner IS hosted on this mesh — reporting it absent sends the reader to another silo");
        error.Message.Should().Contain($"Target: Hub {owner.Address} RunLevel=",
            "the owner's own state is what decides where the unanswered re-probe went");
        error.Message.Should().Contain(ShutdownNack.FormatActivationTag(owner),
            "the target's activation token is the one a ShuttingDown NACK carries, so the reading can "
            + "say whether the re-probe sits at the activation that refused (the dying hub) or a successor");
        error.Message.Should().Contain("IsShuttingDown=False",
            "a dying activation and a live successor differ here, and the owner in this test is live");
    }
}
