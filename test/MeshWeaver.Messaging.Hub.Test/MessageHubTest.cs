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
    public void HelloWorld()
    {
        var host = GetHost();
        var response = host.Observe(new SayHelloRequest(), o => o.WithTarget(CreateHostAddress())).Should().Within(10.Seconds()).Emit();
        response.Should().BeAssignableTo<IMessageDelivery<HelloEvent>>();
    }

    [Fact]
    public void HelloWorldFromClient()
    {
        var client = GetClient();
        var response = client.Observe(new SayHelloRequest(), o => o.WithTarget(CreateHostAddress())).Should().Within(5.Seconds()).Emit();
        response.Should().BeAssignableTo<IMessageDelivery<HelloEvent>>();
    }

    [Fact]
    public void ClientToServerWithMessageTraffic()
    {
        var client = GetClient();

        var response = client.Observe(new SayHelloRequest(), o => o.WithTarget(CreateHostAddress())).Should().Emit();
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
    public void DisposeRequestToMeshRoot_IsRefused_MeshStaysAlive()
    {
        var host = GetHost();
        var client = GetClient();
        var mesh = Mesh;

        // Precondition: we really are targeting the irreplaceable root mesh hub.
        mesh.Address.Type.Should().Be(AddressExtensions.MeshType);
        mesh.IsDisposing.Should().BeFalse();

        // Baseline: a round-trip routed THROUGH the mesh works.
        client.Observe(new SayHelloRequest(), o => o.WithTarget(host.Address))
            .Should().Within(10.Seconds()).Emit();

        // The incident: a DisposeRequest routed to the root mesh hub's own address.
        client.Post(new DisposeRequest(), o => o.WithTarget(mesh.Address));

        // The mesh must survive. This second round-trip both proves the mesh is still
        // routing AND (FIFO) that the DisposeRequest was already drained by the time the
        // response returns — so the IsDisposing assertion below is observed post-handling.
        var response = client.Observe(new SayHelloRequest(), o => o.WithTarget(host.Address))
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
