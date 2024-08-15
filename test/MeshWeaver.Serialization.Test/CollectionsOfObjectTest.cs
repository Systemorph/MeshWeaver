using System.Text.Json;
using FluentAssertions;
using FluentAssertions.Json;
using Microsoft.Extensions.DependencyInjection;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using MeshWeaver.ServiceProvider;
using Xunit.Abstractions;

namespace MeshWeaver.Serialization.Test;

public class CollectionsOfObjectTest : TestBase
{
    record ClientAddress;

    [Inject]
    private IMessageHub<ClientAddress> Client { get; set; }

    public CollectionsOfObjectTest(ITestOutputHelper output) : base(output)
    {
        Services.AddSingleton(sp => sp.CreateMessageHub(new ClientAddress(), ConfigureClient));
    }

    private static MessageHubConfiguration ConfigureClient(MessageHubConfiguration c)
        => c;

    [Fact]
    public void SerializeDictionatyOfObjectObject_IntString()
    {
        // arrange
        var data = new Dictionary<object, object>()
        {
            { 1, "One" },
            { 3, "Three" },
            { 5, "Five" },
        };
        var container = new ContainerRecord(data);

        // act
        var serialized = JsonSerializer.Serialize(container, Client.JsonSerializerOptions);

        // assert
        var actual = serialized.Should().NotBeNull().And.BeValidJson().Which;
        actual.Should().HaveElement("$type").Which.Should().HaveValue(typeof(ContainerRecord).FullName);
        var actualData = actual.Should().HaveElement("data").Which;
        actualData.Should().HaveElement("1").Which.Should().HaveValue("One");
        actualData.Should().HaveElement("3").Which.Should().HaveValue("Three");
        actualData.Should().HaveElement("5").Which.Should().HaveValue("Five");
    }
}

record ContainerRecord(object Data);
