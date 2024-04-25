using System.Reactive.Linq;
using System.Text.Json;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using OpenSmc.Fixture;
using OpenSmc.Hub.Fixture;
using OpenSmc.Messaging;
using OpenSmc.ServiceProvider;
using Xunit.Abstractions;

namespace OpenSmc.Serialization.Test;

public class SerializationTest : TestBase
{
    record RouterAddress; // TODO V10: can we use implicitly some internal address and not specify it outside? (23.01.2024, Alexander Yolokhov)
    record HostAddress;
    record ClientAddress;

    [Inject] private IMessageHub Router { get; set; }

    public SerializationTest(ITestOutputHelper output) : base(output)
    {
        Services.AddMessageHubs(new RouterAddress(), hubConf => hubConf
            .WithRoutes(f => f
                .RouteAddress<HostAddress>((routedAddress, d) =>
                    {
                        var hostedHub = f.Hub.GetHostedHub(routedAddress, ConfigureHost);
                        var packagedDelivery = d.Package(f.Hub.JsonSerializerOptions);
                        hostedHub.DeliverMessage(packagedDelivery);
                        return d.Forwarded();
                    })
                .RouteAddress<ClientAddress>((routedAddress, d) =>
                {
                    var hostedHub = f.Hub.GetHostedHub(routedAddress, ConfigureClient);
                    var packagedDelivery = d.Package(f.Hub.JsonSerializerOptions);
                    hostedHub.DeliverMessage(packagedDelivery);
                    return d.Forwarded();

                })
            ));
    }

    private static MessageHubConfiguration ConfigureHost(MessageHubConfiguration c)
    {
        return c.WithHandler<Boomerang>((hub, request) =>
        {
            hub.Post(request.Message.Object, o => o.ResponseFor(request));
            return request.Processed();
        });
    }

    private static MessageHubConfiguration ConfigureClient(MessageHubConfiguration c)
    {
        return c; 
    }

    [Fact]
    public async Task SimpleTest()
    {
        var host = Router.GetHostedHub(new HostAddress(), ConfigureHost);
        var client = Router.GetHostedHub(new ClientAddress(), ConfigureClient);
        var hostOut = host.AddObservable();
        var messageTask = hostOut.Where(h => h.Message is not ShutdownRequest).ToArray().GetAwaiter();
        
        client.Post(new MyEvent("Hello"), o => o.WithTarget(new HostAddress()));

        await Task.Delay(300);

        await Router.DisposeAsync();

        var events = await messageTask;
        events.Should().HaveCount(1);
        var message = events.Single().Message;
        var rawJson = message.Should().BeOfType<JObject>().Subject;
        await VerifyJson(rawJson.ToString());


    }

    [Fact]
    public async Task BoomerangTest()
    {
        // problem 1: if we post Message instead of Message.Object ==> many events
        var host = Router.GetHostedHub(new HostAddress(), ConfigureHost);
        var client = Router.GetHostedHub(new ClientAddress(), ConfigureClient);
        var hostOut = host.AddObservable();
        var messageTask = hostOut.Where(h => h.Message is not ShutdownRequest).ToArray().GetAwaiter();

        var response = await client.AwaitResponse(new Boomerang(new MyEvent("Hello")), o => o.WithTarget(new HostAddress()));

        await Router.DisposeAsync();


        var events = await messageTask;
        events.Should().HaveCount(1);
        var message = events.Single().Message;
        var boomerang = message.Should().BeOfType<Boomerang>().Subject;
        boomerang.Object.Should().BeOfType<JObject>();

        response.Message.Should().BeOfType<MyEvent>();

    }

    [Fact]
    public void MessageDeliveryPropertiesTest()
    {
        var client = Router.GetHostedHub(new ClientAddress(), ConfigureClient);

        var postOptions = new PostOptions(client.Address, client)
            .WithTarget(new HostAddress())
            .WithProperties(new Dictionary<string, object> {
                { "MyId", "394" },
                { "MyAddress", new ClientAddress() },
                { "NestedObjs", new Boomerang(new MyEvent("Hello nested")) },
                { "MyId2", "22394" },
            });

        var delivery = new MessageDelivery<MyEvent>(new MyEvent("Hello Delivery"), postOptions);

        var packedDelivery = delivery.Package(client.JsonSerializerOptions);

        var serialized = JsonSerializer.Serialize(packedDelivery, client.JsonSerializerOptions);

        var deserialized = JsonSerializer.Deserialize<MessageDelivery<RawJson>>(serialized, client.JsonSerializerOptions);

        deserialized.Should().NotBeNull()
            .And.NotBeSameAs(packedDelivery)
            .And.BeEquivalentTo(packedDelivery);
    }
}

public record Boomerang(object Object) : IRequest<object>;

public record HostAddress();
public record MyEvent(string Text);