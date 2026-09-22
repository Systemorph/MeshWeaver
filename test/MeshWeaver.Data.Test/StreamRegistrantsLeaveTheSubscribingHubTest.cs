using System;
using System.Linq;
using System.Reactive.Disposables;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Data.TestDomain;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// 🚨 <b>A remote stream used to leave one permanent registrant on the SUBSCRIBING hub — the
/// long-lived one — and nothing ever took it back out.</b> Systemorph/MeshWeaver#3432.
///
/// <para><b>The root, in one line.</b> <c>JsonSynchronizationStream.CreateExternalClient</c>
/// registered the stream's owner-protocol <c>SubscribeRequest</c> subscription twice: once on the
/// stream (correct) and once more on <c>hub</c>, the subscribing hub, under the comment
/// <i>"Belt-and-suspenders: dispose the subscription when the HUB tears down too (idempotent)"</i>.
/// A hub's registrant composite is APPEND-ONLY — <c>CompositeDisposable.Add</c> never prunes, and
/// no code path removes an entry — so that second registration held the subscription, and through
/// its closure the entire <see cref="SynchronizationStream{TStream}"/> (its <c>Store</c> and that
/// store's last snapshot, its reference, its captured AccessContext, and its <c>Hub</c> until
/// #3321's release ran), for the subscribing hub's whole life. One per remote stream ever opened —
/// on a portal hub that is one per rendered layout area and per node read.</para>
///
/// <para><b>Why the second registration bought nothing.</b> The hub-teardown route it claimed to
/// cover already reaches the subscription, and strictly EARLIER:
/// <c>ISynchronizationStream.RegisterForDisposal</c> hooks the stream's own composite onto the
/// stream's <c>sync/{id}</c> sub-hub, which is a HOSTED hub of the subscribing hub — so the
/// subscribing hub's <c>DisposeHostedHubs</c> phase disposes it BEFORE its own <c>ShutDown</c>
/// phase walks its registrants. The duplicate could only ever fire on an already-disposed
/// subscription.</para>
///
/// <para><b>The instrument.</b> <see cref="MessageHub.DisposalRegistrantCount"/> — the size of the
/// composite <c>DisposeImpl</c> walks. It is a retention reading, not a tidiness one, and it is the
/// only thing in the process that can see this class of root, which is why it is also printed in
/// the hub's disposal diagnostics.</para>
///
/// <para><b>Controls on both sides.</b> The measuring arm asserts the count RETURNS TO ITS FLOOR;
/// <see cref="TheRegistrantCounter_MovesWhenSomethingIsActuallyRegistered"/> is the
/// growing-direction control that proves a flat reading is not vacuous — without it, "the count did
/// not grow" would also pass against a counter wired to a constant. The measured control on the
/// unfixed build (the duplicate registration restored) is reported in the pull request.</para>
/// </summary>
public class StreamRegistrantsLeaveTheSubscribingHubTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>Long enough that no heartbeat fires during the measurement, so every registrant
    /// counted is one this test caused.</summary>
    private static readonly TimeSpan LongHeartbeat = TimeSpan.FromMinutes(5);

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddData(data => data.AddSource(src => src
                .WithType<BusinessUnit>(t => t.WithInitialData(TestData.BusinessUnits))
                .WithType<LineOfBusiness>(t => t.WithInitialData(TestData.LinesOfBusiness))));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .WithServices(services => services.Configure<SyncStreamOptions>(o =>
            {
                o.HeartbeatInterval = LongHeartbeat;
                o.ChangeFeedResubscribeWindow = LongHeartbeat;
                o.ChangeFeedStalenessGrace = LongHeartbeat;
            }))
            .AddData(data => data.AddHubSource(CreateHostAddress(),
                ds => ds.WithType<BusinessUnit>().WithType<LineOfBusiness>()));

    /// <summary>
    /// 🚨 <b>THE DEFECT, and the fix.</b> Opening a remote stream and disposing it must leave the
    /// subscribing hub exactly as it found it. On the unfixed build the count never comes back down:
    /// the duplicate registration is still in the composite, holding the disposed subscription and
    /// the stream graph behind it, for the rest of the hub's life.
    /// </summary>
    [HubFact]
    public async Task OpeningAndDisposingARemoteStream_LeavesNoRegistrantOnTheSubscribingHub()
    {
        var (workspace, client) = await StartAndSettleAsync();

        var floor = RegistrantCount(client);
        var syncHubFloor = LiveSyncHubs(client);

        // Two DISTINCT keys, so this is two separate CreateExternalClient calls and not one cached
        // stream handed back twice — the growth being measured is per STREAM.
        var first = workspace.GetRemoteStream<InstanceCollection, CollectionReference>(
            CreateHostAddress(), new CollectionReference(nameof(BusinessUnit)));
        var second = workspace.GetRemoteStream<InstanceCollection, CollectionReference>(
            CreateHostAddress(), new CollectionReference(nameof(LineOfBusiness)));

        // Positive signal that both streams really were built here: two more sync/ sub-hubs on the
        // subscribing hub. Without this, a run in which GetRemoteStream returned nothing at all
        // would report a flat registrant count and read as a pass.
        var afterOpen = RegistrantCount(client);
        var syncHubsOpened = LiveSyncHubs(client) - syncHubFloor;
        Output.WriteLine($"DIAG floor={floor} afterOpen={afterOpen} syncHubsOpened=+{syncHubsOpened}");
        syncHubsOpened.Should().Be(2,
            "each GetRemoteStream on a fresh key builds one SynchronizationStream, which mints "
            + "exactly one hosted sync/{ClientId} hub — so the measurement below is taken over two "
            + "streams that provably exist");

        first.Dispose();
        second.Dispose();

        var afterDispose = RegistrantCount(client);
        Output.WriteLine($"DIAG afterDispose={afterDispose} (floor={floor})");

        afterDispose.Should().Be(floor,
            "a remote stream owns its own disposal composite, which its sub-hub carries — so "
            + "opening and disposing one must leave NOTHING registered on the subscribing hub. A "
            + "count that stays above the floor is one retained subscription per stream ever "
            + "opened, each holding the whole stream graph, for this hub's entire life (#3432)");
    }

    /// <summary>
    /// 🚨 <b>THE GROWING-DIRECTION CONTROL.</b> The arm above asserts an absence, so it is only
    /// worth something if the counter can report a presence. One real registration must move it by
    /// exactly one, and the hub's teardown must take it back to zero.
    /// </summary>
    [HubFact]
    public async Task TheRegistrantCounter_MovesWhenSomethingIsActuallyRegistered()
    {
        var (_, client) = await StartAndSettleAsync();

        var before = RegistrantCount(client);
        client.RegisterForDisposal(Disposable.Create(() => { }));
        var after = RegistrantCount(client);
        Output.WriteLine($"DIAG control before={before} after={after}");

        after.Should().Be(before + 1,
            "one registered cleanup is one entry in the composite DisposeImpl walks — without this "
            + "the sibling test's 'the count returned to its floor' could also be read off a "
            + "counter that never moves at all");
    }

    /// <summary>
    /// 🚨 <b>THE COVERAGE CONTROL.</b> What the deleted line CLAIMED to provide: a stream nobody
    /// disposes must still be torn down by the subscribing hub's own teardown, with its outstanding
    /// <c>SubscribeRequest</c> callback released rather than left pending into the quiesce budget.
    /// That route runs through the stream's <c>sync/</c> sub-hub — a hosted hub of this one — which
    /// is why the duplicate registration was never the thing providing it.
    /// </summary>
    [HubFact]
    public async Task AnUndisposedRemoteStream_IsStillTornDownByTheSubscribingHub()
    {
        var (workspace, client) = await StartAndSettleAsync();

        var syncHubFloor = LiveSyncHubs(client);
        _ = workspace.GetRemoteStream<InstanceCollection, CollectionReference>(
            CreateHostAddress(), new CollectionReference(nameof(BusinessUnit)));
        (LiveSyncHubs(client) - syncHubFloor).Should().Be(1,
            "the stream under test must exist before its teardown means anything");

        client.Dispose();

        await client.DisposalCompleted.Should().Within(TestTimeouts.Convergence)
            .Emit("the subscribing hub must finish its own teardown with a live remote stream "
                + "attached — the route that reaches the stream's subscription is its sync/ "
                + "sub-hub's teardown, in the DisposeHostedHubs phase");

        client.AnyHubQuiescingTimedOut().Should().BeFalse(
            "a SubscribeRequest callback still pending when the quiesce budget elapsed is exactly "
            + "the leak the registration was supposed to prevent — it is released by the stream's "
            + "own composite, carried on the sync/ sub-hub");
        RegistrantCount(client).Should().Be(0,
            "a hub that has finished disposing has walked and emptied its registrant composite");
    }

    /// <summary>Activates both hubs and waits for the owner's initial snapshot, so the client's
    /// data-context init gate is open before any count is taken.</summary>
    private async Task<(IWorkspace Workspace, IMessageHub Client)> StartAndSettleAsync()
    {
        GetHost();
        var client = GetClient();
        var workspace = client.ServiceProvider.GetRequiredService<IWorkspace>();

        await workspace.GetObservable<BusinessUnit>()
            .Should().Within(10.Seconds())
            .Match(x => x.Count > 0, "the owner must serve the initial snapshot",
                cancellationToken: TestContext.Current.CancellationToken);

        return (workspace, client);
    }

    /// <summary>The retention reading under test — see
    /// <see cref="MessageHub.DisposalRegistrantCount"/>.</summary>
    private static int RegistrantCount(IMessageHub hub) => ((MessageHub)hub).DisposalRegistrantCount;

    /// <summary>#3432's own metric: one hosted <c>sync/{ClientId}</c> hub per
    /// <see cref="SynchronizationStream{TStream}"/>, so this is the live client-side mirror count.</summary>
    private static int LiveSyncHubs(IMessageHub hub) =>
        hub.ServiceProvider.GetRequiredService<HostedHubsCollection>()
            .Hubs.Count(h => h.Address.Type == SynchronizationAddress.AddressType);
}
