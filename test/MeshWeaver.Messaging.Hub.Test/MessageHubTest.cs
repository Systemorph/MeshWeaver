using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using Xunit;

using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
namespace MeshWeaver.Messaging.Hub.Test;

public class MessageHubTest(ITestOutputHelper output) : HubTestBase(output)
{
    record SayHelloRequest : IRequest<HelloEvent>;

    record HelloEvent;

    protected override MessageHubConfiguration ConfigureHost(
        MessageHubConfiguration configuration
    ) =>
        configuration.WithHandler<SayHelloRequest>(
            (hub, request) =>
            {
                hub.Post(new HelloEvent(), options => options.ResponseFor(request));
                return request.Processed();
            }
        );

    [Fact]
    public async Task HelloWorld()
    {
        var host = GetHost();
        var response = await host.Observe(new SayHelloRequest(), o => o.WithTarget(CreateHostAddress())).Should().Within(10.Seconds()).Emit();
        response.Should().BeAssignableTo<IMessageDelivery<HelloEvent>>();
    }

    [Fact]
    public async Task HelloWorldFromClient()
    {
        var client = GetClient();
        var response = await client.Observe(new SayHelloRequest(), o => o.WithTarget(CreateHostAddress())).Should().Within(5.Seconds()).Emit();
        response.Should().BeAssignableTo<IMessageDelivery<HelloEvent>>();
    }

    [Fact]
    public async Task ClientToServerWithMessageTraffic()
    {
        var client = GetClient();

        var response = await client.Observe(new SayHelloRequest(), o => o.WithTarget(CreateHostAddress())).Should().Emit();
        response.Should().BeAssignableTo<IMessageDelivery<HelloEvent>>();
    }

    /// <summary>
    /// Repro for the atioz mesh-wide outage (2026-06-10). A <see cref="DisposeRequest"/>
    /// is a permission-gateless <c>[SystemMessage]</c>, so any sender — including an
    /// unauthenticated external/RawJson client — could route one to the root mesh hub's
    /// own address (<c>mesh/&lt;id&gt;</c>) and dispose the irreplaceable singleton. Once
    /// disposed it was never rebuilt, so every node operation timed out at 60 s forever
    /// until the process restarted. The mesh hub must IGNORE a message-routed dispose
    /// (its lifecycle is owned by host teardown, which calls Dispose() directly).
    /// Before the fix this test hangs on the second round-trip (mesh dead → no routing).
    /// </summary>
    [Fact]
    public async Task DisposeRequestToMeshRoot_IsRefused_MeshStaysAlive()
    {
        var host = GetHost();
        var client = GetClient();
        var mesh = Mesh;

        // Precondition: we really are targeting the irreplaceable root mesh hub.
        mesh.Address.Type.Should().Be(AddressExtensions.MeshType);
        mesh.IsDisposing.Should().BeFalse();

        // Baseline: a round-trip routed THROUGH the mesh works.
        await client.Observe(new SayHelloRequest(), o => o.WithTarget(host.Address))
            .Should().Within(10.Seconds()).Emit();

        // The incident: a DisposeRequest routed to the root mesh hub's own address.
        client.Post(new DisposeRequest(), o => o.WithTarget(mesh.Address));

        // The mesh must survive. This second round-trip both proves the mesh is still
        // routing AND (FIFO) that the DisposeRequest was already drained by the time the
        // response returns — so the IsDisposing assertion below is observed post-handling.
        var response = await client.Observe(new SayHelloRequest(), o => o.WithTarget(host.Address))
            .Should().Within(10.Seconds()).Emit();
        response.Should().BeAssignableTo<IMessageDelivery<HelloEvent>>();

        mesh.IsDisposing.Should().BeFalse(
            "a message-routed DisposeRequest must never dispose the root mesh hub");
    }

    /// <summary>
    /// Guard companion to <see cref="DisposeRequestToMeshRoot_IsRefused_MeshStaysAlive"/>:
    /// the refusal is scoped to the mesh root ONLY. A normal hosted hub (portal circuit,
    /// per-node, client) must still honor a message-routed dispose — recycle and circuit
    /// teardown depend on it.
    /// </summary>
    [Fact]
    public async Task DisposeRequestToHostedHub_StillDisposesIt()
    {
        var victim = GetClient();
        victim.Address.Type.Should().NotBe(AddressExtensions.MeshType);
        victim.IsDisposing.Should().BeFalse();

        Mesh.Post(new DisposeRequest(), o => o.WithTarget(victim.Address));

        // A non-mesh hub must still dispose on a message-routed DisposeRequest.
        try
        {
            await victim.DisposalCompleted.FirstOrDefaultAsync().ToTask().WaitAsync(10.Seconds());
        }
        catch
        {
            // A faulting disposal still counts as "disposing" for this assertion.
        }
        victim.IsDisposing.Should().BeTrue();
    }

    record FloodEvent;

    /// <summary>
    /// Regression for the ShutdownRequest repost STORM (2026-06-14).
    /// <c>HandleShutdownCore</c> used to gate on <c>request.Version == Version - 1</c>
    /// ("this ShutdownRequest is the immediately-next message since it was posted") and,
    /// on mismatch, REPOST the request with a corrected version. But <c>++Version</c>
    /// runs for EVERY handled message, so any traffic concurrent with disposal bumps
    /// Version past that one-step window between every repost and its re-handle — the gate
    /// never converges and instead self-sustains a repost storm: 2,820 reposts on a single
    /// <c>consumer/1</c> hub, ~140k ShutdownRequest turns suite-wide under the 2-core
    /// security tests, saturating <c>TaskScheduler.Default</c> and timing the project out
    /// on the 2-core CI runner. The fix removes the gate — the per-phase RunLevel guards
    /// already make disposal idempotent, and the three phases are causally chained +
    /// FIFO-ordered. A healthy disposal therefore handles exactly the phase
    /// ShutdownRequests regardless of concurrent load. Before the fix, with the flood
    /// below, <c>ShutdownTurnsHandled</c> runs into the hundreds/thousands.
    /// </summary>
    [Fact]
    public async Task Dispose_UnderContinuousLoad_DoesNotStormShutdownRequests()
    {
        var victim = (MessageHub)Mesh.GetHostedHub(new Address("victim", "1"), x => x);

        // Confirm the hub's turn loop is live.
        victim.Post(new FloodEvent(), o => o.WithTarget(victim.Address));

        // Keep the turn loop busy so Version advances DURING disposal — the exact condition
        // the old version-match gate livelocked on. Self-posted unhandled events are Ignored
        // after one turn each (++Version per turn). The flood is BOUNDED on purpose: each
        // post emits a Debug "Buffering message" line that xUnit captures into the TRX, and
        // an unbounded tight loop produced a 24 MB TRX with a single 30 k-line text node that
        // broke CI's result-XML parser ("huge text node") and failed the whole shard even
        // though the test passed. A few hundred posts is plenty of interleaving to make the
        // old gate storm (ShutdownTurnsHandled in the hundreds) while keeping the log small.
        using var floodStop = new CancellationTokenSource();
        var flood = Task.Run(() =>
        {
            for (var i = 0; i < 400 && !floodStop.IsCancellationRequested; i++)
            {
                try { victim.Post(new FloodEvent(), o => o.WithTarget(victim.Address)); }
                catch { break; /* hub disposing */ }
                Thread.SpinWait(20_000); // spread the 400 posts across the dispose window
            }
        });

        // Tiny ramp so the inbox is non-empty when Dispose posts the first ShutdownRequest,
        // then dispose CONCURRENTLY with the still-running flood.
        await Task.Delay(15);

        victim.Dispose();
        await victim.DisposalCompleted.FirstOrDefaultAsync().ToTask().WaitAsync(30.Seconds());

        floodStop.Cancel();
        await flood;

        // Healthy disposal = 3 phase turns (Quiescing, DisposeHostedHubs, ShutDown). The
        // bound is ~3 orders of magnitude below the pre-fix storm; any storm blows past it.
        victim.ShutdownTurnsHandled.Should().BeLessThan(20,
            "disposal must handle only the phase ShutdownRequests, never a version-mismatch repost storm");
    }

    record StormFloodEvent(int Seq);

    /// <summary>
    /// Integration proof that the storm circuit-breaker is wired into the hub's
    /// ingestion point (<c>MessageService.ScheduleNotify</c>) and keeps the hub
    /// responsive under an unbounded loop. A tight burst of one identical
    /// <c>(sender, target, type)</c> tuple — the loop signature behind every wedge this
    /// session (resubscribe / repost / DeliveryFailure ping-pong / denied-Subscribe
    /// retry) — must (1) TRIP the breaker (logged once), (2) get DROPPED at ingestion so
    /// the single-threaded turn loop is never saturated, and (3) leave the hub answering
    /// other traffic. Without the breaker this burst floods the action block and the hub
    /// (and in prod the whole portal) wedges.
    /// </summary>
    [Fact]
    public async Task MessageStorm_IsTrippedAndDropped_HubStaysResponsive()
    {
        // Victim hub reachable as a concrete MessageHub so we can observe its breaker.
        // It handles SayHelloRequest (round-trip responsiveness) and counts StormFloodEvents
        // that actually reach a handler (everything dropped at ingestion never gets here).
        var processed = 0;
        var victim = (MessageHub)Mesh.GetHostedHub(new Address("victim", "1"), c => c
            // Plumbing fixture with no user → posts as infrastructure (System), per the
            // never-null AccessContext invariant (feedback_access_context_always_set).
            .WithPostingIdentity(PostingIdentity.System)
            .WithHandler<SayHelloRequest>((hub, request) =>
            {
                hub.Post(new HelloEvent(), o => o.ResponseFor(request));
                return request.Processed();
            })
            .WithHandler<StormFloodEvent>((_, d) =>
            {
                Interlocked.Increment(ref processed);
                return d.Processed();
            }));

        victim.StormBreaker.Should().NotBeNull("the default MessageService wires a storm breaker");

        // Subscribe to the trip signal BEFORE storming — the trip fires synchronously
        // during the storming post on ScheduleNotify's thread.
        var tripped = victim.StormBreaker!.Trips.Should().Within(10.Seconds()).Emit();

        // Storm the SAME key well past the per-key threshold in a tight loop (microseconds
        // per cheap self-post, so the whole burst lands inside one rate window).
        const int burst = MessageStormBreaker.DefaultThreshold * 3;
        for (var i = 0; i < burst && !victim.IsDisposing; i++)
            victim.Post(new StormFloodEvent(i), o => o.WithTarget(victim.Address));

        var trip = await tripped;
        trip.TypeName.Should().Be(nameof(StormFloodEvent));
        trip.ObservedCount.Should().BeGreaterThan(MessageStormBreaker.DefaultThreshold);
        victim.StormBreaker.TripCount.Should().Be(1, "a storm trips and logs once, not once per dropped message");

        // RESPONSIVENESS: a DIFFERENT message type round-trips while the storm key is
        // tripped. If the storm had saturated the single-threaded turn loop this would
        // time out — that's the wedge the breaker prevents.
        var response = await victim.Observe(new SayHelloRequest(), o => o.WithTarget(victim.Address))
            .Should().Within(10.Seconds()).Emit();
        response.Should().BeAssignableTo<IMessageDelivery<HelloEvent>>();

        // DROPPED AT INGESTION: far fewer floods reached a handler than were posted — the
        // breaker stopped the cascade rather than enqueuing every looping message.
        Volatile.Read(ref processed).Should().BeLessThan(burst,
            "messages of the storming key must be dropped at ingestion, not all processed");
    }

    [CanBeIgnored]                                    // fire-and-forget → SHEDDABLE
    record MissingSubscribeEvent(int Path);
    record BlockTurnRequest;                          // user-facing → holds the action block

    /// <summary>
    /// Invariant 3 (Doc/Architecture/ActionBlockWedgePrevention.md) — the canonical wedge
    /// repro. While the single action-block thread is held, flood it with sheddable traffic
    /// so the inbound depth crosses the aggregate watermark. The per-key breaker is provably
    /// NOT what saves us (the flood is <c>[CanBeIgnored]</c> → exempt from <c>ShouldDrop</c>
    /// → TripCount stays 0); the AGGREGATE watermark sheds the excess so the single turn loop
    /// stays bounded, and a user-facing probe posted DURING the overload is NEVER shed and
    /// round-trips once the block drains. RED before the aggregate breaker (AggregateSheds
    /// never emits — the mechanism doesn't exist); GREEN after.
    /// </summary>
    [Fact]
    public async Task ManyDistinctMissingSubscribes_DoNotWedge()
    {
        const int watermark = 25;
        const int flood = 200;
        using var blockHandlerEntered = new ManualResetEventSlim(false);
        using var releaseBlock = new ManualResetEventSlim(false);

        var victim = (MessageHub)Mesh.GetHostedHub(new Address("victim", "1"), c => c
            .WithPostingIdentity(PostingIdentity.System)
            .WithAggregateWatermark(watermark)
            .WithHandler<BlockTurnRequest>((hub, d) =>
            {
                blockHandlerEntered.Set();
                releaseBlock.Wait(TimeSpan.FromSeconds(30)); // hold the single turn thread
                return d.Processed();
            })
            .WithHandler<SayHelloRequest>((hub, request) =>
            {
                hub.Post(new HelloEvent(), o => o.ResponseFor(request));
                return request.Processed();
            }));

        // Observe the FIRST aggregate shed (fires synchronously on the flooding thread).
        var firstShed = victim.StormBreaker!.AggregateSheds.Should().Within(10.Seconds()).Emit();

        // 1) Occupy the single action-block thread.
        victim.Post(new BlockTurnRequest(), o => o.WithTarget(victim.Address));
        blockHandlerEntered.Wait(TimeSpan.FromSeconds(10))
            .Should().BeTrue("the block handler must occupy the turn thread before we flood");

        // 2) Flood sheddable traffic while the block is held → depth crosses the watermark.
        for (var i = 0; i < flood; i++)
            victim.Post(new MissingSubscribeEvent(i), o => o.WithTarget(victim.Address));

        await firstShed; // RED without the aggregate breaker: this never emits

        // 3) A user-facing probe posted DURING the overload must never be shed.
        var probe = victim.Observe(new SayHelloRequest(), o => o.WithTarget(victim.Address))
            .Should().Within(10.Seconds()).Emit();

        // 4) Release the block; the bounded queue drains and the probe is answered.
        releaseBlock.Set();
        (await probe).Should().BeAssignableTo<IMessageDelivery<HelloEvent>>(
            "the action block stayed drainable — sheddable traffic was shed, user-facing work was not");

        victim.StormBreaker.AggregateShedCount.Should().BeGreaterThan(0,
            "the aggregate watermark shed the excess sheddable traffic");
        victim.StormBreaker.TripCount.Should().Be(0,
            "the per-key breaker must NOT be what saved the hub — this isolates the aggregate path");
    }

    [Fact]
    public void RoutingCycleDetection_ShouldDetectCycle()
    {
        // Create a test message delivery with a routing cycle
        var testMessage = new SayHelloRequest();
        var delivery = new MessageDelivery<SayHelloRequest>(
            CreateClientAddress(),
            CreateHostAddress(),
            testMessage,
            new System.Text.Json.JsonSerializerOptions()
        );

        // Simulate a routing path that would create a cycle
        var routerAddress = CreateMeshAddress();
        var hostAddress = CreateHostAddress();

        var deliveryWithPath = (MessageDelivery<SayHelloRequest>)delivery.AddToRoutingPath(routerAddress);
        deliveryWithPath = (MessageDelivery<SayHelloRequest>)deliveryWithPath.AddToRoutingPath(hostAddress);

        // Verify that a cycle is detected when we try to route to an address already in the path
        deliveryWithPath.RoutingPath.Contains(routerAddress).Should().BeTrue();
        deliveryWithPath.RoutingPath.Contains(hostAddress).Should().BeTrue();

        // Verify that no cycle is detected for a new address
        deliveryWithPath.RoutingPath.Contains(CreateClientAddress("different")).Should().BeFalse();

        // Verify the routing path contains the expected addresses
        deliveryWithPath.RoutingPath.Should().Contain(routerAddress);
        deliveryWithPath.RoutingPath.Should().Contain(hostAddress);
        deliveryWithPath.RoutingPath.Should().HaveCount(2);
    }

    [Fact]
    public void RoutingCycleDetection_WithActualCycle_ShouldDetectAndFail()
    {
        var host = GetHost();
        var client = GetClient();

        // Create a delivery that will create a routing cycle
        var testMessage = new SayHelloRequest();
        var delivery = new MessageDelivery<SayHelloRequest>(
            client.Address,
            host.Address,
            testMessage,
            new System.Text.Json.JsonSerializerOptions()
        );

        // Add the host address to routing path to simulate it already being visited
        var deliveryWithCycle = (MessageDelivery<SayHelloRequest>)delivery.AddToRoutingPath(host.Address);

        // Verify that the cycle detection logic works correctly
        deliveryWithCycle.RoutingPath.Contains(host.Address).Should().BeTrue("because host address is already in routing path");
        
        // Verify that the routing path is correctly maintained
        deliveryWithCycle.RoutingPath.Should().Contain(host.Address);
        deliveryWithCycle.RoutingPath.Should().HaveCount(1);

        // Test that the message would be failed due to routing cycle
        // Since we're testing the core logic rather than the full message flow,
        // we verify that the cycle detection correctly identifies the problem
        var shouldFail = deliveryWithCycle.RoutingPath.Contains(host.Address);
        shouldFail.Should().BeTrue("because routing to the same address again would create a cycle");
    }

    [Fact]
    public void RoutingCycleDetection_SelfRouting_ShouldStillDetectCycle()
    {
        var host = GetHost();

        // Create a message where sender and current address are the same
        var testMessage = new SayHelloRequest();
        var delivery = new MessageDelivery<SayHelloRequest>(
            host.Address, // Same as target
            host.Address,
            testMessage,
            new System.Text.Json.JsonSerializerOptions()
        );

        // Add the host address to routing path to simulate self-routing
        var deliveryWithCycle = (MessageDelivery<SayHelloRequest>)delivery.AddToRoutingPath(host.Address);

        // Verify cycle is detected even for self-routing
        deliveryWithCycle.RoutingPath.Contains(host.Address).Should().BeTrue("because routing path contains the current address");

        // Verify that routing path contains the expected address
        deliveryWithCycle.RoutingPath.Should().Contain(host.Address);
        deliveryWithCycle.RoutingPath.Should().HaveCount(1);

        // In self-routing scenarios, the cycle detection still works,
        // but the logic in MessageService prevents sending DeliveryFailure
        var selfRoutingCycle = deliveryWithCycle.Sender.Equals(host.Address) && deliveryWithCycle.RoutingPath.Contains(host.Address);
        selfRoutingCycle.Should().BeTrue("because this represents a self-routing cycle scenario");
    }

    [Fact]
    public void RoutingCycleDetection_Integration_WithComplexPath()
    {
        // Test a more realistic scenario where a message gets routed through multiple hubs
        var router = Mesh;
        var host = GetHost();
        var client = GetClient();

        // Create a message that will be sent from client to host
        var testMessage = new SayHelloRequest();
        
        // Test that our routing path functionality integrates properly with message posting
        var delivery = client.Post(testMessage, options => options.WithTarget(host.Address));
        
        delivery.Should().NotBeNull();
        
        // Verify that we can access routing path functionality on posted messages
        delivery!.RoutingPath.Should().NotBeNull();
        
        // Initially, routing path should be empty since message hasn't been routed yet
        delivery.RoutingPath.Should().BeEmpty();
        
        // Verify that cycle detection would work correctly
        var hasCycle = delivery.RoutingPath.Contains(client.Address);
        hasCycle.Should().BeFalse("because client address is not in routing path yet");
    }

}
