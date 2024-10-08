using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using MeshWeaver.ServiceProvider;
using Xunit.Abstractions;

namespace MeshWeaver.Serialization.Test;

public class SerializationTest : TestBase
{
    record RouterAddress; // TODO V10: can we use implicitly some internal address and not specify it outside? (23.01.2024, Alexander Yolokhov)

    record HostAddress;

    record ClientAddress;

    [Inject]
    private IMessageHub Router { get; set; }

    public SerializationTest(ITestOutputHelper output)
        : base(output)
    {
        Services.AddMessageHubs(
            new RouterAddress(),
            hubConf =>
                hubConf.WithRoutes(f =>
                    f.RouteAddress<HostAddress>(
                            (routedAddress, d) =>
                            {
                                var hostedHub = f.Hub.GetHostedHub(routedAddress, ConfigureHost);
                                var packagedDelivery = d.Package(f.Hub.JsonSerializerOptions);
                                hostedHub.DeliverMessage(packagedDelivery);
                                return d.Forwarded();
                            }
                        )
                        .RouteAddress<ClientAddress>(
                            (routedAddress, d) =>
                            {
                                var hostedHub = f.Hub.GetHostedHub(routedAddress, ConfigureClient);
                                var packagedDelivery = d.Package(f.Hub.JsonSerializerOptions);
                                hostedHub.DeliverMessage(packagedDelivery);
                                return d.Forwarded();
                            }
                        )
                )
        );
    }

    private static MessageHubConfiguration ConfigureHost(MessageHubConfiguration c)
    {
        return c.WithHandler<Boomerang>(
            (hub, request) =>
            {
                hub.Post(
                    new BoomerangResponse(
                        request.Message.Object,
                        request.Message.Object.GetType().Name
                    ),
                    o => o.ResponseFor(request)
                );
                return request.Processed();
            }
        );
    }

    private static MessageHubConfiguration ConfigureClient(MessageHubConfiguration c)
    {
        return c;
    }

    /// <summary>
    /// This tests the serialization of a message with a nested object,
    /// whereby the nested object is not registered in the host.
    /// The host can resolve the type by using Type.GetType, as it is actually deployed.
    /// We should set up another test in a different AssemblyLoadContext, where the nested type is not deployed.
    /// </summary>
    /// <returns></returns>
    [Fact]
    public async Task BoomerangTest()
    {
        var host = Router.GetHostedHub(new HostAddress(), ConfigureHost);
        var client = Router.GetHostedHub(new ClientAddress(), ConfigureClient);

        var response = await client.AwaitResponse(
            new Boomerang(new MyEvent("Hello")),
            o => o.WithTarget(new HostAddress())
        );

        response.Message.Object.Should().BeOfType<MyEvent>().Which.Text.Should().Be("Hello");
        response.Message.Type.Should().Be(typeof(MyEvent).Name);
    }

    [Fact]
    public void MessageDeliveryPropertiesTest()
    {
        var client = Router.GetHostedHub(new ClientAddress(), ConfigureClient);

        var postOptions = new PostOptions(client.Address)
            .WithTarget(new HostAddress())
            .WithProperties(
                new Dictionary<string, object>
                {
                    { "MyId", "394" },
                    { "MyAddress", new ClientAddress() },
                    { "NestedObjs", new Boomerang(new MyEvent("Hello nested")) },
                    { "MyId2", "22394" },
                }
            );

        var delivery = new MessageDelivery<MyEvent>(new MyEvent("Hello Delivery"), postOptions);

        var packedDelivery = delivery.Package(client.JsonSerializerOptions);

        var serialized = JsonSerializer.Serialize(packedDelivery, client.JsonSerializerOptions);

        var deserialized = JsonSerializer.Deserialize<MessageDelivery<RawJson>>(
            serialized,
            client.JsonSerializerOptions
        );

        deserialized
            .Should()
            .NotBeNull()
            .And.NotBeSameAs(packedDelivery)
            .And.BeEquivalentTo(packedDelivery);
    }
}

public record Boomerang(object Object) : IRequest<BoomerangResponse>;

public record BoomerangResponse(object Object, string Type);

public record HostAddress();

public record MyEvent(string Text);
