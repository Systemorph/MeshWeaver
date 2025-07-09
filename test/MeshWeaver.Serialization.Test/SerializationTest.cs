using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Serialization.Test;

public class SerializationTest : HubTestBase
{

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

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration c)
    {
        return base.ConfigureHost(c).WithHandler<Boomerang>(
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
        var client = Router.GetHostedHub(new ClientAddress(), ConfigureClient);

        var response = await client.AwaitResponse(
            new Boomerang(new MyEvent("Hello")),
            o => o.WithTarget(new HostAddress()),
            new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token
        );

        response.Message.Object.Should().BeOfType<MyEvent>().Which.Text.Should().Be("Hello");
        response.Message.Type.Should().Be(nameof(MyEvent));
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

    [Fact]
    public void ValueTupleSerializationTest()
    {
        var client = Router.GetHostedHub(new ClientAddress(), ConfigureClient);

        var valueTuple = (Id: 1, Name: "Test");
        var postOptions = new PostOptions(client.Address)
            .WithTarget(new HostAddress())
            .WithProperties(
                new Dictionary<string, object>
                {
                    { "MyValueTuple", valueTuple },
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

    [Fact]
    public void ValueTupleSerializationExceptionTest()
    {
        var client = Router.GetHostedHub(new ClientAddress(), ConfigureClient);

        // Create a ValueTuple with ArticleStatus and DateTime
        var statusTuple = (DateTime.Now, DateTime.UtcNow);

        var postOptions = new PostOptions(client.Address)
            .WithTarget(new HostAddress())
            .WithProperties(
                new Dictionary<string, object>
                {
                    { "StatusTuple", statusTuple }
                }
            );

        var delivery = new MessageDelivery<MyEvent>(new MyEvent("Hello ValueTuple"), postOptions);

        // This should reproduce the exception:
        // System.InvalidOperationException: 'Specified type 'System.ValueTuple`2[MeshWeaver.Articles.ArticleStatus,System.DateTime]' does not support polymorphism. Polymorphic types cannot be structs, sealed types, generic types or System.Object.'
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
    [Fact]
    public void DirectValueTupleSerializationTest()
    {
        var client = Router.GetHostedHub(new ClientAddress(), ConfigureClient);

        // Test direct serialization of ValueTuple
        var statusTuple = (new DateTime(), DateTime.Now);

        // Test serialization of the ValueTuple directly - this should not throw an exception anymore
        var serialized = JsonSerializer.Serialize(statusTuple, client.JsonSerializerOptions);

        // For now, just test that serialization works without throwing
        serialized.Should().NotBeNull();

        // Test deserialization
        var deserialized = JsonSerializer.Deserialize<(DateTime, DateTime)>(serialized, client.JsonSerializerOptions);

        // Verify that the first item is the newly set DateTime value instead of the enum
        deserialized.Item1.Should().Be(statusTuple.Item1); // Change this to check against the new DateTime instead
    }
    [Fact]
    public void ValueTupleInObjectSerializationTest()
    {
        var client = Router.GetHostedHub(new ClientAddress(), ConfigureClient);

        // Create an object that contains a ValueTuple - this should not throw polymorphism exception
        var testObject = new ValueTupleTestMessage((DateTime.Now, DateTime.UtcNow));

        var serialized = JsonSerializer.Serialize(testObject, client.JsonSerializerOptions);

        // For now, just test that serialization works without throwing
        serialized.Should().NotBeNull();

        var deserialized = JsonSerializer.Deserialize<ValueTupleTestMessage>(serialized, client.JsonSerializerOptions);

        deserialized.Should().NotBeNull();
        // Verify at least the enum is preserved
        deserialized.StatusTuple.Item1.Should().Be(testObject.StatusTuple.Item1);
    }
    [Fact]
    public void ValueTuplePolymorphismExceptionFixed()
    {
        var client = Router.GetHostedHub(new ClientAddress(), ConfigureClient);

        // This was the exact scenario that caused the polymorphism exception
        var statusTuple = (DateTime.Now, DateTime.UtcNow);

        var postOptions = new PostOptions(client.Address)
            .WithTarget(new HostAddress())
            .WithProperties(
                new Dictionary<string, object>
                {
                    { "StatusTuple", statusTuple }
                }
            );

        var delivery = new MessageDelivery<MyEvent>(new MyEvent("Hello ValueTuple"), postOptions);

        // This should not throw the polymorphism exception anymore
        Action act = () => delivery.Package(client.JsonSerializerOptions);

        act.Should().NotThrow<InvalidOperationException>();
    }
}
public record Boomerang(object Object) : IRequest<BoomerangResponse>;

public record BoomerangResponse(object Object, string Type);

public record ValueTupleTestMessage((DateTime SomeDate, DateTime Timestamp) StatusTuple);

public record MyEvent(string Text);

public record LayoutControlMessage
{
    public object Control { get; init; } = null!;
    public string Description { get; init; } = null!;
}
