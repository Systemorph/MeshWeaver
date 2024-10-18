using System.Text.Json;
using FluentAssertions;
using FluentAssertions.Json;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using MeshWeaver.Messaging.Serialization;
using Xunit.Abstractions;

namespace MeshWeaver.Serialization.Test;

public class RawJsonTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .WithTypes(typeof(RawJson), typeof(MessageDelivery<>))
            .WithSerialization(serialization =>
                serialization.WithOptions(options =>
                {
                    if (!options.Converters.Any(c => c is RawJsonConverter))
                        options.Converters.Insert(0, new RawJsonConverter());
                })
            );


    [Fact]
    public void WayForward_DeserializeToRawJson()
    {
        var client = GetClient();
        // arrange
        var postOptions = new PostOptions(client.Address)
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
        var serialized = JsonSerializer.Serialize(delivery, client.JsonSerializerOptions);

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
        var client = GetClient();
        // arrange
        var postOptions = new PostOptions(client.Address)
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
        var dataChanged = new DataChangedEvent(new HostAddress(), new CollectionsReference(), 10, new RawJson(entityStoreSerialized), ChangeType.Full, null);
        var delivery = new MessageDelivery<DataChangedEvent>(dataChanged, postOptions);
        var packedDelivery = delivery.Package(Router.JsonSerializerOptions);

        // act
        var serialized = JsonSerializer.Serialize(packedDelivery, Router.JsonSerializerOptions);

        // assert
        var actual = serialized.Should().NotBeNull().And.BeValidJson().Which;
        var actualMessage = actual.Should().HaveElement("message").Which;
        actualMessage.Should().HaveElement("$type").Which.Should().HaveValue(typeof(DataChangedEvent).FullName);

        // act
        var deserialized = JsonSerializer.Deserialize<IMessageDelivery>(serialized, client.JsonSerializerOptions);

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
