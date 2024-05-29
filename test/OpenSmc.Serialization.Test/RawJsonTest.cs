using System.Text.Json;
using FluentAssertions;
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
    }
}
