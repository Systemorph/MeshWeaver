using System;
using System.Linq;
using System.Reactive.Disposables;
using System.Threading;
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
/// cover already reaches the subscription, and strictly EARLIER — by TWO routes, measured (see
/// <see cref="AnUndisposedRemoteStreamsComposite_IsWalkedByTheSubscribingHubsTeardown"/>):
/// <c>ISynchronizationStream.RegisterForDisposal</c> hooks the stream's own composite onto the
/// stream's <c>sync/{id}</c> sub-hub, a HOSTED hub of the subscribing hub, torn down in its
/// <c>DisposeHostedHubs</c> phase; and <c>Workspace.Dispose</c> disposes every cached remote stream,
/// which its own comment says exists to release exactly this <c>SubscribeRequest</c> callback. A hub
/// walks its OWN registrants only later, in <c>ShutDown</c> — so the duplicate was the third route
/// and the last to fire, and could only ever reach an already-disposed subscription.</para>
///
/// <para><b>The instrument.</b> <see cref="MessageHub.DisposalRegistrantCount"/> — the size of the
/// composite <c>DisposeImpl</c> walks. It is a retention reading, not a tidiness one, and it is the
/// only thing in the process that can see this class of root, which is why it is also printed in
/// the hub's disposal diagnostics and why
/// <see cref="TheRegistrantCounter_MovesWhenSomethingIsActuallyRegistered"/> asserts that printed
/// form in BOTH readers: without that, deleting either append would leave every test here green
/// while the production census loses its only reading of this retention class.</para>
///
/// <para><b>Controls on both sides.</b> The measuring arm asserts the count RETURNS TO ITS FLOOR;
/// the counter arm is the growing-direction control that proves a flat reading is not vacuous; and
/// <see cref="AnUndisposedRemoteStreamsComposite_IsWalkedByTheSubscribingHubsTeardown"/> measures
/// the coverage claim directly with a probe registered ON the stream, so "the hub teardown still
/// reaches it" is an observation rather than an argument. The measured control on the unfixed build
/// (the duplicate registration restored) is reported in the pull request.</para>
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
    /// 🚨 <b>THE GROWING-DIRECTION CONTROL, and the DIAGNOSTIC CONTRACT.</b> The arm above asserts
    /// an absence, so it is only worth something if the counter can report a presence: one real
    /// registration must move it by exactly one.
    ///
    /// <para>And the count is only useful to the production census if it is PRINTED, so both
    /// readers are asserted here against a nonzero count. Without this, deleting the
    /// <c>Registrants=</c> append from either <c>GetPendingRequestDiagnostics</c> or
    /// <c>AppendDiagnostics</c> would leave every test in this file green while the only in-process
    /// reading of this retention class disappeared from the line the census parses.</para>
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
        after.Should().BeGreaterThan(0,
            "the two diagnostic assertions below are only meaningful over a NONZERO count — "
            + "'Registrants=0' would also be printed by a hub that registered nothing");

        var pending = client.GetPendingRequestDiagnostics();
        Output.WriteLine($"DIAG pendingLine={pending}");
        pending.Should().Contain($"Registrants={after}",
            "the per-hub retention reading has to be IN the one-line snapshot a live request "
            + "timeout attaches — that line is what the in-process census parses");

        // The root hub's line is the first of the recursive walk (depth 0), so this is exact rather
        // than 'some hub in the tree happens to carry this number'.
        var disposalRootLine = client.GetDisposalDiagnostics().Split('\n')[0];
        Output.WriteLine($"DIAG disposalRootLine={disposalRootLine}");
        disposalRootLine.Should().Contain($"Registrants={after}",
            "and in the multi-line disposal snapshot too — that is the reader the disposal-state "
            + "census and every dispose-timeout report go through");
    }

    /// <summary>
    /// 🚨 <b>THE COVERAGE CONTROL — what the deleted line CLAIMED to provide, measured directly.</b>
    /// A probe registered ON the stream, and nothing else: the stream is never disposed, the
    /// subscribing hub is, and the probe must come back disposed before the hub's
    /// <c>DisposalCompleted</c>.
    ///
    /// <para>🚨 <b>It measures the OUTCOME, not one route — because the outcome turned out to be
    /// OVER-DETERMINED, which is a stronger statement than the one this test was first written to
    /// make.</b> The first draft claimed the probe proved the <c>sync/</c> sub-hub route
    /// specifically (the stream's composite hooked onto its sub-hub by
    /// <c>ISynchronizationStream.RegisterForDisposal</c>, disposed in the parent's
    /// <c>DisposeHostedHubs</c> phase). Running the negative control refuted that: with that hook
    /// removed from <c>SynchronizationStream.RegisterForDisposal</c> and the tree rebuilt, this test
    /// still PASSES — because <c>Workspace.Dispose</c> disposes every cached remote stream too, and
    /// its own comment says why in as many words (<i>"Each SynchronizationStream registers its own
    /// SubscribeRequest hub.Observe callback for disposal here; without this loop the parent hub's
    /// responseSubjects entry for each open SubscribeRequest leaks"</i>). So at least TWO routes
    /// reach that subscription on a hub teardown, and the line this change deletes was a THIRD —
    /// and the latest of them, since a hub walks its own registrants in <c>ShutDown</c>, after both
    /// of the others. Do not re-word this test into a claim about one route; if you want to pin a
    /// route, break every other one first.</para>
    ///
    /// <para>🚨 The pre-assertion that the probe is UNDISPOSED before the teardown is what makes the
    /// 0 → 1 transition a measurement rather than a reading of a value that was already 1:
    /// <c>RegisterForDisposal</c> disposes a registrant IMMEDIATELY when the stream is already dead,
    /// and a probe disposed at registration time would satisfy the final assertion without any
    /// teardown happening at all. Nothing else runs between the two assertions but
    /// <c>client.Dispose()</c>.</para>
    ///
    /// <para>🚨 <b>Why NOT an unanswered <c>SubscribeRequest</c> as the precondition.</b> That shape
    /// (the owner swallows the subscribe, so the client's pending callback can only be closed by a
    /// teardown) measures a different thing and would assert something this change does not claim:
    /// BOTH the deleted registration and the surviving one run strictly AFTER the Quiescing phase —
    /// the deleted one in <c>ShutDown</c>, the surviving one in <c>DisposeHostedHubs</c> — so
    /// neither rescues the quiesce budget, and the pending callback is released there by
    /// <c>CancelCallbacks</c> either way. The claim this change makes is narrower and exact: the
    /// surviving route reaches the stream's composite, and it reaches it EARLIER. That is what the
    /// probe reads.</para>
    /// </summary>
    [HubFact]
    public async Task AnUndisposedRemoteStreamsComposite_IsWalkedByTheSubscribingHubsTeardown()
    {
        var (workspace, client) = await StartAndSettleAsync();

        var syncHubFloor = LiveSyncHubs(client);
        var stream = workspace.GetRemoteStream<InstanceCollection, CollectionReference>(
            CreateHostAddress(), new CollectionReference(nameof(BusinessUnit)));
        (LiveSyncHubs(client) - syncHubFloor).Should().Be(1,
            "the stream under test must exist before its teardown means anything");

        var probeDisposed = 0;
        stream.RegisterForDisposal(Disposable.Create(() => Interlocked.Exchange(ref probeDisposed, 1)));

        Volatile.Read(ref probeDisposed).Should().Be(0,
            "RegisterForDisposal disposes a registrant ON THE SPOT when the stream is already dead, "
            + "so a probe that was disposed at registration time would satisfy the final assertion "
            + "with no teardown having happened at all");

        client.Dispose();

        await client.DisposalCompleted.Should().Within(TestTimeouts.Convergence)
            .Emit("the subscribing hub must finish its own teardown with a live remote stream "
                + "attached",
                cancellationToken: TestContext.Current.CancellationToken);

        Volatile.Read(ref probeDisposed).Should().Be(1,
            "the stream's own composite IS reached by the subscribing hub's teardown, and by more "
            + "than one route — its sync/ sub-hub's own teardown in the DisposeHostedHubs phase, "
            + "and Workspace.Dispose's loop over the cached remote streams. Both run before the "
            + "hub walks its OWN registrants in ShutDown, which is where the deleted line sat: it "
            + "was the third route and the last to fire, so it reclaimed nothing and retained "
            + "everything");
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
