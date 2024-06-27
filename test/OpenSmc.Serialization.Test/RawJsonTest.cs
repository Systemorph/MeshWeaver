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
                                options.Converters.Insert(0, new RawJsonConverter(serialization.Hub.ServiceProvider.GetRequiredService<ITypeRegistry>()));
                        })
                    )
        );
    }

    private static MessageHubConfiguration ConfigureClient(MessageHubConfiguration c)
        => c
            .WithTypes(typeof(RawJson), typeof(MessageDelivery<>))
            .WithSerialization(serialization =>
                serialization.WithOptions(options =>
                {
                    if (!options.Converters.Any(c => c is RawJsonConverter))
                        options.Converters.Insert(0, new RawJsonConverter(serialization.Hub.ServiceProvider.GetRequiredService<ITypeRegistry>()));
                })
            );

    [Fact]
    public void WayForward_DeserializeToRawJson()
    {
        // arrange
        var postOptions = new PostOptions(Client.Address)
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

        // act
        var serialized = JsonSerializer.Serialize(delivery, Client.JsonSerializerOptions);

        // assert
        var actual = serialized.Should().NotBeNull().And.BeValidJson().Which;
        var actualMessage = actual.Should().HaveElement("message").Which;
        actualMessage.Should().HaveElement("$type").Which.Should().HaveValue(typeof(SubscribeRequest).FullName);

        // act
        var deserialized = JsonSerializer.Deserialize<MessageDelivery<RawJson>>(serialized, Router.JsonSerializerOptions);

        // assert
        deserialized.Should().NotBeNull()
            .And.NotBeSameAs(delivery)
            .And.BeEquivalentTo(delivery, o => o.Excluding(x => x.Message));
        var rawJsonContent = deserialized.Message.Should().NotBeNull()
            .And.Subject.As<RawJson>()
                .Content.Should().NotBeNullOrWhiteSpace()
                .And.Subject;
        var jContent = rawJsonContent.Should().BeValidJson().Which;
        jContent.Should().HaveElement("$type").Which.Should().HaveValue(typeof(SubscribeRequest).FullName);
    }

    [Fact]
    public void WayBack()
    {
        // arrange
        var postOptions = new PostOptions(Client.Address)
            .WithTarget(new HostAddress())
            .WithProperties(
                new Dictionary<string, object>
                {
                    { "MyId", "394" },
                    { "MyAddress", new ClientAddress() },
                    { "MyId2", "22394" },
                }
            );
        var entityStore = new EntityStore();
        var entityStoreSerialized = JsonSerializer.Serialize(entityStore, Router.JsonSerializerOptions);
        var dataChanged = new DataChangedEvent(new HostAddress(), new WorkspaceStateReference(), 10, new RawJson(entityStoreSerialized), ChangeType.Full, null);
        var delivery = new MessageDelivery<DataChangedEvent>(dataChanged, postOptions);
        var packedDelivery = delivery.Package(Router.JsonSerializerOptions);

        // act
        var serialized = JsonSerializer.Serialize(packedDelivery, Router.JsonSerializerOptions);

        // assert
        var actual = serialized.Should().NotBeNull().And.BeValidJson().Which;
        var actualMessage = actual.Should().HaveElement("message").Which;
        actualMessage.Should().HaveElement("$type").Which.Should().HaveValue(typeof(DataChangedEvent).FullName);

        // act
        var deserialized = JsonSerializer.Deserialize<IMessageDelivery>(serialized, Client.JsonSerializerOptions);

        // assert
        deserialized.Should().NotBeNull()
            .And.NotBeSameAs(delivery)
            .And.BeEquivalentTo(delivery, o => o.Excluding(x => x.Message));
        var rawJsonContent = deserialized.Message.Should().NotBeNull()
            .And.Subject.As<RawJson>()
                .Content.Should().NotBeNullOrWhiteSpace()
                .And.Subject;
        var jContent = rawJsonContent.Should().BeValidJson().Which;
        jContent.Should().HaveElement("$type").Which.Should().HaveValue(typeof(DataChangedEvent).FullName);
        jContent.Should().HaveElement("version").Which.Should().HaveValue("10");
    }
}
