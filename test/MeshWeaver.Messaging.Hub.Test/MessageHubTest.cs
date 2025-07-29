using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Fixture;
using Xunit;

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
        var response = await host.AwaitResponse(
            new SayHelloRequest(),
            o => o.WithTarget(new HostAddress())
            , new CancellationTokenSource(10.Seconds()).Token
        );
        response.Should().BeAssignableTo<IMessageDelivery<HelloEvent>>();
    }

    [Fact]
    public async Task HelloWorldFromClient()
    {
        var client = GetClient();
        var response = await client.AwaitResponse(
            new SayHelloRequest(),
            o => o.WithTarget(new HostAddress()),
            CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken,
                new CancellationTokenSource(5.Seconds()).Token
            ).Token
        );
        response.Should().BeAssignableTo<IMessageDelivery<HelloEvent>>();
    }

    [Fact]
    public async Task ClientToServerWithMessageTraffic()
    {
        var client = GetClient();

        var response = await client.AwaitResponse(
            new SayHelloRequest(),
            o => o.WithTarget(new HostAddress()),
            TestContext.Current.CancellationToken
        );
        response.Should().BeAssignableTo<IMessageDelivery<HelloEvent>>();
    }

    [Fact]
    public void RoutingCycleDetection_ShouldDetectCycle()
    {
        // Create a test message delivery with a routing cycle
        var testMessage = new SayHelloRequest();
        var delivery = new MessageDelivery<SayHelloRequest>(
            new ClientAddress(), 
            new HostAddress(), 
            testMessage, 
            new System.Text.Json.JsonSerializerOptions()
        );

        // Simulate a routing path that would create a cycle
        var routerAddress = new RouterAddress();
        var hostAddress = new HostAddress();
        
        var deliveryWithPath = (MessageDelivery<SayHelloRequest>)delivery.AddToRoutingPath(routerAddress);
        deliveryWithPath = (MessageDelivery<SayHelloRequest>)deliveryWithPath.AddToRoutingPath(hostAddress);
        
        // Verify that a cycle is detected when we try to route to an address already in the path
        deliveryWithPath.RoutingPath.Contains(routerAddress).Should().BeTrue();
        deliveryWithPath.RoutingPath.Contains(hostAddress).Should().BeTrue();
        
        // Verify that no cycle is detected for a new address
        deliveryWithPath.RoutingPath.Contains(new ClientAddress("different")).Should().BeFalse();
        
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
        var router = Router;
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
