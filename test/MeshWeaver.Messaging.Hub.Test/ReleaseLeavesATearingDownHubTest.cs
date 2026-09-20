using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Subjects;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// The framework half of issue #3432: <b>a post marked <see cref="IReleasesRemoteState"/> must still
/// leave a hub that is tearing down, and an unmarked fire-and-forget must still be refused.</b>
///
/// <para>The teardown post guard in <c>MessageService.PostImplGeneric</c> refuses everything but
/// <c>ShutdownRequest</c> / <c>DisposeRequest</c> and a correlated reply once the posting hub reaches
/// <c>DisposeHostedHubs</c>. That is right for events — nobody awaits one, and the receiver recovers
/// from the loss through a snapshot, a re-subscribe or a change feed. It is wrong for a RELEASE,
/// which is the only thing that ever frees state the receiver holds: there is no requester to NACK,
/// no retry to trigger and no later probe that discovers the loss, so the refusal reads as success
/// and leaks the receiver's memory for the rest of its life. In production that was one
/// <c>RunLevel=Started</c> <c>sync/{id}</c> hub per subscription, forever.</para>
///
/// <para><b>The route is the production one, not a simulation.</b> The release is posted from a
/// disposable registered on a HOSTED hub, so it runs in that hub's <c>ShutDown</c> phase — which by
/// construction happens while its parent is in <c>DisposeHostedHubs</c>, because that phase is what
/// disposes it. This is exactly where <c>JsonSynchronizationStream.CreateExternalClient</c>'s
/// <c>UnsubscribeRequest</c> runs on a circuit close, a <c>DisposeRequest</c> or a recycle.</para>
///
/// <para><b>Neither arm can pass on no evidence.</b> Both read the POST's own verdict — the
/// refusing branch returns a <see cref="MessageDeliveryState.Failed"/> delivery classified
/// <see cref="ErrorType.ShuttingDown"/>, the carrying branch returns the submitted one — so
/// "refused" and "carried" are read off the mechanism rather than inferred from a wait. The release
/// arm additionally requires ARRIVAL at the sink, and the control's "never arrived" is settled by
/// ORDER, not by a timer: it was posted first and dropped at the source, so by the time the sink has
/// handled the release there is no queue left that could still deliver it.</para>
/// </summary>
public class ReleaseLeavesATearingDownHubTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>A release: the only thing that would ever free what the sink is holding.</summary>
    private record StreamRelease : IReleasesRemoteState;

    /// <summary>
    /// The growing-direction control. Same size, same route, same teardown phase — and NOT a
    /// release, so the historical refusal must still apply. Without this arm, "forward everything
    /// fire-and-forget through the parent" would pass, which is the storm shape the guard exists
    /// for.
    /// </summary>
    private record OrdinaryEvent;

    private readonly AsyncSubject<Unit> releaseArrived = new();
    private int ordinaryEventArrived;

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithTypes(typeof(StreamRelease), typeof(OrdinaryEvent))
            .WithHandler<StreamRelease>((_, delivery) =>
            {
                releaseArrived.OnNext(Unit.Default);
                releaseArrived.OnCompleted();
                return delivery.Processed();
            })
            .WithHandler<OrdinaryEvent>((_, delivery) =>
            {
                Interlocked.Exchange(ref ordinaryEventArrived, 1);
                return delivery.Processed();
            });

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .WithTypes(typeof(StreamRelease), typeof(OrdinaryEvent));

    [HubFact]
    public async Task AReleaseLeavesWhileAnOrdinaryEventIsStillRefused()
    {
        var sink = GetHost();
        var subscriber = GetClient();

        // A hosted hub whose ShutDown runs both posts — the shape of a client-side sync/{id} hub
        // releasing its owner-side twin.
        var hosted = subscriber.GetHostedHub(
            new Address("releasing-child", "1"),
            c => c.WithPostingIdentity(PostingIdentity.System)
                .WithTypes(typeof(StreamRelease), typeof(OrdinaryEvent)));

        IMessageDelivery? ordinaryVerdict = null;
        IMessageDelivery? releaseVerdict = null;
        hosted.RegisterForDisposal(Disposable.Create(() =>
        {
            // Ordinary FIRST, so its "never arrived" is decided by order rather than by a window.
            ordinaryVerdict = subscriber.Post(new OrdinaryEvent(), o => o.WithTarget(sink.Address));
            releaseVerdict = subscriber.Post(new StreamRelease(), o => o.WithTarget(sink.Address));
        }));

        subscriber.Dispose();

        await subscriber.DisposalCompleted.Should().Within(TestTimeouts.Convergence).Emit(
            "the subscribing hub must finish its own teardown — both posts happen inside it, so "
            + "until it has completed neither verdict has been produced");

        releaseVerdict.Should().NotBeNull(
            "the hosted hub's ShutDown must have run the disposable — otherwise this test measures "
            + "nothing about what a teardown may post");
        releaseVerdict!.State.Should().NotBe(MessageDeliveryState.Failed,
            "a release is the ONLY thing that frees what the receiver holds, so the teardown guard "
            + "must carry it through the parent instead of refusing it at the source (#3432)");

        await releaseArrived.Should().Within(TestTimeouts.Convergence).Emit(
            "and carrying it must mean ARRIVAL: a verdict that says 'not refused' while the message "
            + "goes nowhere is the same silent loss wearing a better label");

        ordinaryVerdict.Should().NotBeNull();
        ordinaryVerdict!.State.Should().Be(MessageDeliveryState.Failed,
            "an unmarked fire-and-forget event keeps the historical refusal — forwarding all of it "
            + "out of a disposing hub is the storm the guard exists to prevent");
        ordinaryVerdict.GetFailureErrorType(ErrorType.Unknown).Should().Be(ErrorType.ShuttingDown,
            "and the refusal stays TRANSIENT: the address may reactivate, so a terminal "
            + "classification would tell every consumer's recovery path the wrong thing");

        Volatile.Read(ref ordinaryEventArrived).Should().Be(0,
            "the event was posted BEFORE the release and dropped at the source, so once the sink "
            + "has handled the release there is no queue left that could still deliver it");
    }
}
