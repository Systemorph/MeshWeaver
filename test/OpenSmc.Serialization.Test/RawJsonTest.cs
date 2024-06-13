using System.Text.Json;
using FluentAssertions;
using FluentAssertions.Json;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Data;
using OpenSmc.Fixture;
using OpenSmc.Messaging;
using OpenSmc.Messaging.Serialization;
using OpenSmc.ServiceProvider;
using Xunit.Abstractions;

namespace OpenSmc.Serialization.Test;

public class RawJsonTest : TestBase
{
    record RouterAddress;

    record HostAddress;

    record ClientAddress;

    [Inject]
    private IMessageHub<ClientAddress> Client { get; set; }

    [Inject]
    private IMessageHub Router { get; set; }

    public RawJsonTest(ITestOutputHelper output) : base(output)
    {
        Services.AddSingleton(sp => sp.CreateMessageHub(new ClientAddress(), ConfigureClient));
        Services.AddMessageHubs(
            new RouterAddress(),
            hubConf =>
                hubConf
                    .WithTypes(typeof(ClientAddress), typeof(HostAddress))
                    .WithSerialization(serialization =>
                        serialization.WithOptions(options =>
                        {
                            if (!options.Converters.Any(c => c is RawJsonConverter))
                                options.Converters.Insert(0, new RawJsonConverter());
                        })
                    )
        );
    }

    private static MessageHubConfiguration ConfigureClient(MessageHubConfiguration c)
        => c;

    [Fact]
    public void DeserializeToRawJson()
    {
        // arrange
        var postOptions = new PostOptions(Client.Address, Client)
            .WithTarget(new HostAddress())
            .WithProperties(
                new Dictionary<string, object>
                {
                    { "MyId", "394" },
                    { "MyAddress", new ClientAddress() },
                    { "MyId2", "22394" },
                }
            );
        var subscribeRequest = new SubscribeRequest(new CollectionReference("TestCollection"));
        var delivery = new MessageDelivery<SubscribeRequest>(subscribeRequest, postOptions);

        var serialized = JsonSerializer.Serialize(delivery, Client.JsonSerializerOptions);

        serialized.Should().NotBeNull();

        // act
        var deserialized = JsonSerializer.Deserialize<MessageDelivery<RawJson>>(serialized, Router.JsonSerializerOptions);

        // assert
        deserialized.Should().NotBeNull()
            .And.NotBeSameAs(delivery)
            .And.BeEquivalentTo(delivery, o => o.Excluding(x => x.Message));
        deserialized.Message.Should().NotBeNull().And.Subject.As<RawJson>().Content.Should().NotBeNullOrWhiteSpace();

        var serializedOnServer = JsonSerializer.Serialize(deserialized, Router.JsonSerializerOptions);

        serializedOnServer.Should().NotBeNull();
    }

    [Fact]
    public void WayBack()
    {
        // arrange
        var postOptions = new PostOptions(Client.Address, Client)
            .WithTarget(new HostAddress())
            .WithProperties(
                new Dictionary<string, object>
                {
                    { "MyId", "394" },
                    { "MyAddress", new ClientAddress() },
                    { "MyId2", "22394" },
                }
            );
        var dataChanged = new DataChangedEvent(new HostAddress(), new WorkspaceStateReference(), 10, new RawJson("{}"), ChangeType.Full, null);
        var delivery = new MessageDelivery<DataChangedEvent>(dataChanged, postOptions);
        var packedDelivery = delivery.Package(Router.JsonSerializerOptions);

        // act
        var serialized = JsonSerializer.Serialize(packedDelivery, Router.JsonSerializerOptions);

        // assert
        var actual = serialized.Should().NotBeNull().And.BeValidJson().Which;
        var actualMessage = actual.Should().HaveElement("message").Which;
        actualMessage.Should().HaveElement("$type").Which.Should().HaveValue(typeof(DataChangedEvent).FullName);
    }
}
