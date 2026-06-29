using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using MeshWeaver.Messaging.Serialization;
using Xunit;
using System.Linq;

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
            .WithTarget(CreateHostAddress())
            .WithProperties(
                new Dictionary<string, object>
                {
                    { "MyId", "394" },
                    { "MyAddress", CreateClientAddress() },
                    { "MyId2", "22394" },
                }
            );
        var subscribeRequest = new SubscribeRequest("123", new CollectionReference("TestCollection"));
        var delivery = new MessageDelivery<SubscribeRequest>(subscribeRequest, postOptions, client.JsonSerializerOptions);

        // act
        var serialized = JsonSerializer.Serialize(delivery, client.JsonSerializerOptions);

        // assert
        var actual = serialized.Should().NotBeNull().And.BeValidJson().Which;
        var actualMessage = actual.Should().HaveElement("message").Which;
        actualMessage.Should().HaveElement("$type").Which.Should().HaveValue(typeof(SubscribeRequest).FullName);

        // act
        var deserialized = JsonSerializer.Deserialize<MessageDelivery<RawJson>>(serialized, Mesh.JsonSerializerOptions);

        // assert
        deserialized.Should().NotBeNull()
            .And.NotBeSameAs(delivery)
            .And.BeEquivalentTo(delivery, Mesh.JsonSerializerOptions, o => o
                .Excluding(x => x.Message)
                .Excluding(x => x.Properties));
        var rawJsonContent = deserialized!.Message.Should().NotBeNull()
            .And.Subject.As<RawJson>()
                .Content.Should().NotBeNullOrWhiteSpace()
                .And.Subject;
        var jContent = rawJsonContent.Should().BeValidJson().Which;
        jContent.Should().HaveElement("$type").Which.Should().HaveValue(typeof(SubscribeRequest).FullName);
    }

    /// <summary>
    /// Regression repro for the 2026-06-23 atioz restart loop: a large opaque <see cref="RawJson"/>
    /// payload (a mesh-wide AI search result is exactly this shape) is read on every cross-grain
    /// Orleans deep-copy of the delivery. The old converter inflated it into a full mutable
    /// <c>JsonNode</c> DOM (one heap object per array element) and re-serialized it, producing a Gen0
    /// allocation storm that drove the runtime into a GC death-spiral and ultimately OutOfMemory.
    /// The reader must instead capture the raw slice in a single allocation. We pin that by bounding
    /// the allocation of the deserialize (the deep-copy hot path) to a small multiple of the payload —
    /// the old DOM-building impl allocated many multiples and fails this guard.
    /// </summary>
    [Fact]
    public void LargePayload_RoundTrips_WithoutDomInflation()
    {
        var client = GetClient();

        // arrange — an array of many small strings: the element COUNT is what made the old
        // JsonNode DOM blow up (one node per element), independent of element size.
        var largeJson = "[" + string.Join(",", Enumerable.Range(0, 200_000).Select(i => $"\"item{i:D6}\"")) + "]";
        var rawBytes = Encoding.UTF8.GetByteCount(largeJson);

        var postOptions = new PostOptions(client.Address).WithTarget(CreateHostAddress());
        var delivery = new MessageDelivery<RawJson>(new RawJson(largeJson), postOptions, client.JsonSerializerOptions);
        var serialized = JsonSerializer.Serialize(delivery, client.JsonSerializerOptions);

        // warm up the converter + JIT + ArrayPool so the measured pass reflects steady-state allocation.
        _ = JsonSerializer.Deserialize<MessageDelivery<RawJson>>(serialized, Mesh.JsonSerializerOptions);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var deserialized = JsonSerializer.Deserialize<MessageDelivery<RawJson>>(serialized, Mesh.JsonSerializerOptions);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // correctness — the blob survives the round-trip intact.
        var content = deserialized!.Message.Content;
        content.Should().BeValidJson();
        using (var doc = JsonDocument.Parse(content))
            doc.RootElement.GetArrayLength().Should().Be(200_000);

        // regression guard — reading the blob must NOT inflate it into a DOM. JsonDocument + GetRawText
        // copies the slice out once (~1x payload); the old JsonNode.Parse(...).ToJsonString() allocated
        // many multiples. 5x leaves head-room for the new path while still failing the old one.
        allocated.Should().BeLessThan((long)rawBytes * 5,
            "RawJsonConverter.Read must capture the raw slice, not build a JsonNode DOM (the atioz OOM restart loop)");
    }

    [Fact]
    public void WayBack()
    {
        var client = GetClient();
        // arrange
        var postOptions = new PostOptions(client.Address)
            .WithTarget(CreateHostAddress())
            .WithProperties(
                new Dictionary<string, object>
                {
                    { "MyId", "394" },
                    { "MyAddress", CreateClientAddress() },
                    { "MyId2", "22394" },
                }
            );
        var entityStore = new EntityStore();
        var entityStoreSerialized = JsonSerializer.Serialize(entityStore, Mesh.JsonSerializerOptions);
        var dataChanged = new DataChangedEvent("123", 10, new RawJson(entityStoreSerialized), ChangeType.Full, null);
        var delivery = new MessageDelivery<DataChangedEvent>(dataChanged, postOptions, Mesh.JsonSerializerOptions);
        var packedDelivery = delivery.Package();

        // act
        var serialized = JsonSerializer.Serialize(packedDelivery, Mesh.JsonSerializerOptions);

        // assert
        var actual = serialized.Should().NotBeNull().And.BeValidJson().Which;
        var actualMessage = actual.Should().HaveElement("message").Which;
        actualMessage.Should().HaveElement("$type").Which.Should().HaveValue(typeof(DataChangedEvent).FullName);

        // act
        var deserialized = JsonSerializer.Deserialize<IMessageDelivery>(serialized, client.JsonSerializerOptions);

        // assert
        deserialized.Should().NotBeNull()
            .And.NotBeSameAs(delivery)
            .And.BeEquivalentTo(delivery, client.JsonSerializerOptions, o => o
                .Excluding(x => x.Message)
                .Excluding(x => x.Properties));
        var rawJsonContent = deserialized!.Message.Should().NotBeNull()
            .And.Subject.As<RawJson>()
                .Content.Should().NotBeNullOrWhiteSpace()
                .And.Subject;
        var jContent = rawJsonContent.Should().BeValidJson().Which;
        jContent.Should().HaveElement("$type").Which.Should().HaveValue(typeof(DataChangedEvent).FullName);
        jContent.Should().HaveElement("version").Which.Should().HaveValue("10");
    }
}
