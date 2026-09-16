using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// Pins that the <see cref="DataContext"/> init time-box NAMES what it was waiting on
/// (Systemorph/MeshWeaver#1122).
///
/// <para><b>What went wrong.</b> The <see cref="TimeoutException"/> used to end <i>"— likely a stuck
/// NodeType compile, or a data source that never initialised"</i>: two candidates, neither measured,
/// and the first of them unreachable on the path that produces most of these (NodeType enrichment
/// runs in the ROUTING layer before the hub is built, is bounded at 3 s + 30 s and fails to a
/// compilation-error overlay). Because a <c>LogIncident</c> fingerprint is category + message
/// template + exception type, every cause folded into ONE issue under that sentence — 217
/// occurrences over five weeks spanning at least three unrelated populations, none of them
/// separable from the text.</para>
///
/// <para><b>What must be true instead.</b> The wait is nested — data source → its streams → the
/// type-source legs of its initial-store fan-out — and at the instant the box expires every layer is
/// inspectable. The message must therefore name the data source, the stream that never produced a
/// first frame, and the type-source leg that never settled, and must NOT name the legs that DID
/// settle. This test is structural on all four counts: it reads the recorded message, never a
/// duration.</para>
/// </summary>
public class DataContextInitTimeoutAttributionTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string DataSourceId = "answers-source";

    /// <summary>
    /// The time-box under test — a parameter of the code, not a convergence wait, so it is SHORT
    /// and every wait below dominates it several times over. Derived rather than written (1.5 s
    /// locally, CI-scaled there), which also gives a slow runner more room to START the fan-out
    /// before the box expires: a box that expired before the legs were claimed would name no leg.
    /// </summary>
    private static TimeSpan InitBound => TestTimeouts.Quick / 8;

    /// <summary>
    /// How long any wait below may take. Every wait here is either answered moments after
    /// <see cref="InitBound"/> or never, so this only has to dominate that bound — and it must stay
    /// well inside the attribute's 120 s so a regression fails on a NAMED assertion, never
    /// anonymously on the test timeout (the first negative control of this file did exactly that).
    /// </summary>
    private static TimeSpan AnswerBound => TestTimeouts.Quick;

    /// <summary>The leg that never emits — the one the diagnostic has to name.</summary>
    private record HangingItem(string Id);

    /// <summary>The leg that settles immediately — the one the diagnostic must NOT name.</summary>
    private record SettledItem(string Id);

    // NOT PingRequest: the DataContextInit gate deliberately exempts liveness pings
    // (DataExtensions.WithInitializationGate), so only a real request parks behind it.
    private record ProbeRequest : IRequest<ProbeResponse>;

    private record ProbeResponse;

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => configuration
            .WithTypes(typeof(ProbeRequest), typeof(ProbeResponse))
            .WithHandler<ProbeRequest>((hub, request) =>
            {
                hub.Post(new ProbeResponse(), o => o.ResponseFor(request));
                return request.Processed();
            })
            .AddData(data => data
                // The bound under test. Short so the terminal state is reached fast; prod is 120 s.
                .WithInitializationTimeout(InitBound)
                .AddSource(src => src
                    // One leg settles and one never does. Aggregate over the fan-out emits only when
                    // EVERY leg completes, so this hangs the whole data source — which is exactly
                    // the production shape, where one storage read behind a saturated pool gate
                    // holds a per-node hub while its siblings are long done.
                    .WithType<SettledItem>(t => t
                        .WithKey(i => i.Id)
                        .WithInitialData(() =>
                            Observable.Return<IEnumerable<SettledItem>>([new SettledItem("ok")])))
                    .WithType<HangingItem>(t => t
                        .WithKey(i => i.Id)
                        .WithInitialData(() => Observable.Never<IEnumerable<HangingItem>>())),
                    DataSourceId));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => configuration.WithTypes(typeof(ProbeRequest), typeof(ProbeResponse));

    // 120_000 ms, not TestTimeouts.TestMilliseconds: an attribute argument must be a constant, and
    // every wait in this class is bounded by AnswerBound, which stays far below it even on CI.
    [Fact(Timeout = 120_000)]
    public async Task TimedOutInit_NamesTheDataSourceTheStreamAndTheLegThatNeverSettled()
    {
        var host = GetHost();
        var client = GetClient();

        // Drive the hub to its terminal state through the same door production does: a request
        // parks behind the DataContextInit gate and is answered by the failed-state rejection.
        var act = () => client
            .Observe(new ProbeRequest(), o => o.WithTarget(host.Address))
            .FirstAsync().Timeout(AnswerBound).Await(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<Exception>(
            "a hub whose data-source init hung must answer requests with an error, not hang");

        var initError = host.GetWorkspace().DataContext.InitializationError;
        initError.Should().BeOfType<TimeoutException>(
            "a hung init reaches the terminal FAILED state through the time-box");

        var message = initError!.Message;

        message.Should().Contain("did not complete within",
            "the diagnostic must still surface the time-box expiry");

        message.Should().Contain(DataSourceId,
            "the message must name the DATA SOURCE that did not settle — without it every cause in "
            + "this bucket reads identically, which is why 217 occurrences folded into one issue");

        message.Should().Contain(nameof(HangingItem),
            "the message must name the type-source LEG that never settled — the innermost answer, "
            + "and the one that separates 'a storage read never came back' from 'a remote hub never "
            + "answered a subscribe'");

        message.Should().NotContain(nameof(SettledItem),
            "a leg that DID settle is not what the hub was waiting on; naming it would make the "
            + "diagnostic unreadable exactly where it has to be read");

        message.Should().NotContain("likely",
            "the diagnostic states what it measured — it must never go back to offering the reader "
            + "a list of candidate causes it did not check");
    }

    /// <summary>
    /// Pins the FAN-OUT half of #1122: a failed init errors EVERY stream the data source holds.
    ///
    /// <para>The failure used to be propagated with <c>ds.GetStreamForPartition(null).OnError</c>,
    /// which reaches exactly one stream. A second stream on the same source — here a partition
    /// stream, in production any partition stream of a partitioned source — was never told: its
    /// subscribers waited out their own unrelated deadlines, which is how one stall surfaced as
    /// several different-looking faults. Revert the fan-out and this test times out waiting for the
    /// second stream's terminal.</para>
    /// </summary>
    // 120_000 ms, not TestTimeouts.TestMilliseconds — see the sibling test above.
    [Fact(Timeout = 120_000)]
    public async Task TimedOutInit_ErrorsEveryStreamTheDataSourceHolds_NotOnlyThePrimary()
    {
        var host = GetHost();
        var client = GetClient();
        var dataSource = host.GetWorkspace().DataContext.DataSources.Single();

        // A second stream the source holds besides the primary one its init turn opens. Its own
        // initial load hangs on the same leg, so the only thing that can terminate it is the
        // DataContext failure being propagated to it.
        var second = dataSource.GetStreamForPartition("a-second-partition")!;

        // Capture the terminal BEFORE the failure, so its arrival cannot race the subscription.
        var secondTerminal = second.IgnoreElements().Materialize().FirstAsync().Replay(1);
        using var connection = secondTerminal.Connect();

        var act = () => client
            .Observe(new ProbeRequest(), o => o.WithTarget(host.Address))
            .FirstAsync().Timeout(AnswerBound).Await(TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<Exception>(
            "a hub whose data-source init hung must answer requests with an error, not hang");

        // The probe is answered only after SettleInitializationGate opened the gate, and that body
        // errors the streams BEFORE it opens the gate — so by now the terminal is either already
        // captured or never coming. The bound is a safety net; the named failure is the point.
        var terminal = await secondTerminal.Should().Within(AnswerBound).Emit(
            "the failed initialization must reach a SECOND stream the data source holds, not only "
            + "the one GetStreamForPartition(null) returns",
            TestContext.Current.CancellationToken);

        terminal.Kind.Should().Be(NotificationKind.OnError,
            "every stream the failed data source holds must be told the initialization failed — "
            + "not only the one GetStreamForPartition(null) happens to return");
        terminal.Exception.Should().BeOfType<TimeoutException>(
            "the stream carries the SAME attributed time-box failure the hub recorded");
    }
}

/// <summary>
/// Pins the PROPERTY the init-failure fan-out now depends on (Systemorph/MeshWeaver#1122):
/// <see cref="IDataSource.OpenStreams"/> reports presence, while
/// <see cref="IDataSource.GetStreamForPartition"/> CREATES on a miss — and creating a stream creates
/// a <c>sync/</c> sub-hub with it.
///
/// <para>That is why the failure path must not reach for the accessor. <c>DataContext</c> used to
/// error <c>GetStreamForPartition(null)</c>; on a data source with no null-partition stream
/// (<c>PartitionedHubDataSource</c> opens only its declared partitions) that MINTED a stream, and a
/// hub, for an activation that had just been declared FAILED.</para>
///
/// <para>Measured rather than reasoned: the sub-hub is read off the minted stream itself, so a
/// non-null <c>sync</c> hub IS the hub having been created.</para>
/// </summary>
public class DataSourceOpenStreamsIsPresenceOnlyTest(ITestOutputHelper output) : HubTestBase(output)
{
    private record Item(string Id);

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => configuration.AddData(data => data
            .AddSource(src => src.WithType<Item>(t => t
                .WithKey(i => i.Id)
                .WithInitialData(() => Observable.Return<IEnumerable<Item>>([new Item("one")])))));

    // 120_000 ms, not TestTimeouts.TestMilliseconds: an attribute argument must be a constant.
    [Fact(Timeout = 120_000)]
    public async Task GetStreamForPartitionCreatesAStreamAndItsHub_WhileOpenStreamsOnlyReports()
    {
        var host = GetHost();

        // Anchor on Started, never on "the constructor returned": data sources are started on the
        // hub's INIT TURN (DataExtensions.StartDataSourcesAndOpenGate), so reading the stream map
        // straight after GetHost() races the turn and reads an empty one.
        await host.Started.WaitAsync(TestTimeouts.Quick, TestContext.Current.CancellationToken);

        var dataSource = host.GetWorkspace().DataContext.DataSources.Single();

        var before = dataSource.OpenStreams;
        before.Should().NotBeEmpty(
            "the data source opened its primary stream during initialization");

        var minted = dataSource.GetStreamForPartition("a-partition-nothing-ever-opened");
        ((object?)minted).Should().NotBeNull(
            "GetStreamForPartition is a get-or-CREATE accessor — this is the behaviour that makes "
            + "it the wrong thing to call on a failure path");

        before.Should().NotContain(minted!,
            "the snapshot taken BEFORE cannot contain a stream created after it — OpenStreams "
            + "reports presence at the instant it is read and never creates");

        dataSource.OpenStreams.Count.Should().Be(before.Count + 1,
            "the accessor created a stream the data source now holds");

        // Measure the hub off the stream itself, never by composing an address from its id: the
        // sub-hub is addressed by the stream's CLIENT id, so a composed sync/{StreamId} names a hub
        // that does not exist. (This assertion first read `GetHostedHub(sync/{StreamId})` and failed
        // for exactly that reason — which is also what caught the same mistake in the diagnostic.)
        var subHub = minted!.HubIfHeld();
        ((object?)subHub).Should().NotBeNull(
            "creating a stream creates its sub-hub — so the old failure path built a hub, and a "
            + "second container, for a hub it had just declared FAILED");
        subHub!.Address.Type.Should().Be(SynchronizationAddress.AddressType,
            "the hub a stream creates is a sync/ sub-hub");
    }
}
