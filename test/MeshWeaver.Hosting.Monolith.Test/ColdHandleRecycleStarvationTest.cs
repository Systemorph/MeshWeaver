using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// The COLD half of the recycle contract (issue #1360). <see cref="ResubscribeOnOwnerDisposeTest"/>
/// pins the WARM case — a handle that already holds a snapshot re-converges after its owner
/// recycles. This pins the half that used to have no recovery at all: a handle with <b>no initial
/// state yet</b> when the recycle lands.
///
/// <para><b>The production trace</b> (issue #1360, MeshWeaver.Plugins run 31645120599 attempt 1).
/// An install two seconds in:</para>
/// <code>
/// 22:03:44.045  ── Essentials: installing 36 file(s)…
/// 22:03:46.382  warn: JsonSynchronizationStream Stream Q-…: resubscribe failed.
///               DeliveryFailureException: Hub Essentials is shutting down
///               (RunLevel=DisposeHostedHubs) … Rejecting now.     [ ×4 within 11 ms ]
///               [ 28.043 s of TOTAL log silence — not one line ]
/// 22:04:49.222  [FAIL] Essentials (0 node(s), 0 type(s))  install: TimeoutException
/// </code>
///
/// <para><b>Why the budget is spent in 11 ms.</b> <c>JsonSynchronizationStream</c> answers a
/// <see cref="ErrorType.ShuttingDown"/> NACK by re-asking at once, from the failure callback
/// itself, bounded at <c>MaxRecycleReArms = 3</c>. Its source comment justifies the immediacy with
/// "by the time that verdict is produced the hub is already at RunLevel Dead — so the re-ask lands
/// on a FRESH activation". The trace says otherwise: the NACK names
/// <c>RunLevel=DisposeHostedHubs</c>, a phase in which the dying hub is still registered in its
/// parent's <c>HostedHubsCollection</c>. So routing resolves every re-ask to the SAME dying
/// instance, which NACKs it identically and synchronously — the whole budget burns inside one
/// teardown window, and the stream is orphaned for good.
///
/// <para>A WARM handle survives that: its replayed snapshot satisfies the reader, and the next
/// owner write re-converges it through the change-feed latch (~2.0 s). A COLD handle has nothing
/// to replay, so the orphaning is terminal — <c>UpdateRemote</c>'s initial-state wait runs out its
/// full 30 s and aborts with <i>"no initial state arrived for '…' within 30s"</i>. That is exactly
/// the abort #1360's install died on, and it is the only one of the two shapes that matches.</para>
///
/// <para><b>What this test asserts</b> is the contract, not the mechanism: a cold cross-hub write
/// issued while the owner is mid-recycle must land once the recycle finishes. <c>PackageInstaller</c>
/// already held itself to it (<c>RootTeardownSettled</c> gates its own re-ask on the dying
/// instance's <see cref="IMessageHub.DisposalCompleted"/>); the sync stream underneath it now does
/// the same, so each of its bounded re-asks is spent on a state that can answer. Against the
/// unfixed stream this test fails at 30 s; against the fixed one it passes in ~3 s and emits no
/// rejected re-ask at all.</para>
/// </summary>
public class ColdHandleRecycleStarvationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string NodeId = "cold-recycle-target";

    private static readonly Address GateChildAddress = new("teardown-gate", NodeId);

    /// <summary>
    /// How long the owner sits at <see cref="MessageHubRunLevel.DisposeHostedHubs"/>.
    ///
    /// <para>NOT a sleep and not a test-side wait: it is the gate child's own
    /// <c>QuiesceTimeout</c>, i.e. a hub budget the framework spends draining a pending response
    /// callback. Modelling it is the point — a real per-node hub has <c>sync/*</c> children with
    /// exactly such callbacks, which is why a production teardown occupies this phase at all
    /// (the trace's own NACK reads <c>RunLevel=DisposeHostedHubs</c>). The measured re-arm burst
    /// is 11 ms, so 1.5 s is ~136× the window it must outlast; lengthening it can only make the
    /// starvation MORE certain, never turn a real failure green. Every wait in the test body is
    /// on a condition.</para>
    /// </summary>
    private static readonly TimeSpan GateQuiesce = TimeSpan.FromMilliseconds(1500);

    /// <summary>Accepted by the gate child and never answered — the pending response callback is
    /// the ONLY thing the Quiescing phase counts, so it is what holds the phase open.</summary>
    private record StallRequest : IRequest<StallAck>;

    private record StallAck;

    /// <summary>SubscribeRequests that reached the owner's handler, i.e. were NOT NACKed.</summary>
    private int _subscribesServed;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder)
            .ConfigureDefaultNodeHub(config =>
            {
                if (!config.Address.ToString()!.EndsWith(NodeId, StringComparison.Ordinal))
                    return config;
                return config
                    // A hosted child that takes a realistic amount of time to quiesce. The
                    // parent's DisposeHostedHubs phase joins hostedHubs.DisposalCompleted with NO
                    // deadline of its own (#1317), so the owner sits in EXACTLY the RunLevel the
                    // production NACK reported for as long as the child takes — no stalled action
                    // block, no watchdog, no sleep. Created eagerly: WithHostedHub is lazy (first
                    // delivery) and the child has to EXIST before the parent tears down.
                    //
                    // Same lever as SubscribeDuringRecycleTest (MeshWeaver.Layout.Test), which
                    // parks an un-answered callback to hold a hub in its disposal window. The one
                    // difference is WHERE: that test needs Quiescing, this one needs the LATER
                    // DisposeHostedHubs, because that is where MessageService's shutdown gate
                    // starts NACKing (`RunLevel >= DisposeHostedHubs`) and what the production
                    // trace recorded. Parking the callback on a CHILD is what buys that phase.
                    .WithInitialization(h =>
                    {
                        var child = h.GetHostedHub(
                            GateChildAddress,
                            c => c
                                .WithTypes(typeof(StallRequest), typeof(StallAck))
                                .WithQuiesceTimeout(GateQuiesce)
                                // Longer than the gate window, so the callback is still pending
                                // when the child starts quiescing.
                                .WithRequestTimeout(TimeSpan.FromMinutes(2))
                                .WithHandler<StallRequest>((_, d) => d.Processed()),
                            HostedHubCreation.Always);
                        child?.Observe<StallAck>(new StallRequest(), o => o.WithTarget(GateChildAddress))
                            .Subscribe(_ => { }, _ => { });
                    })
                    // Passive counter: returns the delivery UNPROCESSED so the framework handler
                    // still runs. Only subscribes that got PAST the shutdown gate are counted.
                    .WithHandler<SubscribeRequest>((_, delivery) =>
                    {
                        Interlocked.Increment(ref _subscribesServed);
                        return delivery;
                    });
            });

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddData();

    [Fact(Timeout = 180_000)]
    public async Task ColdCrossHubWrite_LandsAfterTheOwnersRecycleCompletes()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = $"{TestPartition}/{NodeId}";

        await NodeFactory
            .CreateNode(new MeshNode(NodeId, TestPartition) { Name = "Original", NodeType = "Markdown" })
            .Should().Within(30.Seconds()).Emit();

        // Activate the owner hub WITHOUT touching GetMeshNodeStream: the per-path handle in
        // IMeshNodeStreamCache is shared process-wide, so reading it here would warm the very
        // handle the test needs cold. A bare fire-and-forget post activates the hub on arrival.
        Mesh.Post(new HeartBeatEvent(), o => o.WithTarget(new Address(path)));
        IMessageHub? owner = null;
        await WaitUntil(
            () => (owner = Mesh.GetHostedHub(new Address(path), HostedHubCreation.Never)) is not null,
            "the owner hub must activate before it can be recycled");
        Output.WriteLine($"owner activated: {owner!.Address} runLevel={owner.RunLevel}");

        // Recycle the owner exactly as the framework does — a DisposeRequest, not a direct
        // Dispose() — so the teardown is the production one.
        Mesh.Post(new DisposeRequest(), o => o.WithTarget(new Address(path)));
        await WaitUntil(
            () => owner.RunLevel >= MessageHubRunLevel.DisposeHostedHubs,
            "the owner must reach DisposeHostedHubs, where it NACKs every SubscribeRequest");
        Output.WriteLine($"owner parked at runLevel={owner.RunLevel} (gate child is quiescing)");

        var servedBeforeWrite = Volatile.Read(ref _subscribesServed);

        // THE COLD HANDLE. This client has never subscribed to the path, so its very first
        // SubscribeRequest is the one that lands on the disposing owner and is NACKed.
        var client = GetClient();
        var write = client.GetWorkspace()
            .GetMeshNodeStream(path)
            .Update(node => node with { Name = "Updated" })
            .FirstAsync()
            .ToTask(ct);

        // Nothing to release: the gate closes itself when the child finishes quiescing, and the
        // owner's fresh activation becomes reachable. WITHOUT the fix the subscriber has by then
        // already spent its whole re-arm budget against the dying instance and gone silent, so
        // this dies at UpdateRemote's 30 s initial-state bound —
        // "no initial state arrived for '…' within 30s", the abort #1360's install reported.
        var updated = await write;
        updated.Name.Should().Be("Updated",
            "a cold cross-hub write must land once the owner's recycle completes");

        Output.WriteLine(
            $"subscribes served after the write = {Volatile.Read(ref _subscribesServed) - servedBeforeWrite}");
        Volatile.Read(ref _subscribesServed).Should().BeGreaterThan(servedBeforeWrite,
            "the subscriber must re-ask the FRESH activation — a re-ask that only ever hit the "
            + "dying instance never reaches a handler at all");
    }

    /// <summary>
    /// Polls a public state flag (a hub's <see cref="IMessageHub.RunLevel"/>) — the actual
    /// condition, not a fixed settle. Same shape as <c>DisposalRaceNackTest.WaitUntil</c>.
    /// </summary>
    private static async Task WaitUntil(Func<bool> condition, string because)
    {
        for (var i = 0; i < 600; i++)
        {
            if (condition())
                return;
            await Task.Delay(50);
        }

        Assert.Fail($"Timed out waiting: {because}");
    }
}
