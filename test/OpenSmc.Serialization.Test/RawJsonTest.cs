using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Data;
using OpenSmc.Fixture;
using OpenSmc.Messaging;
using Xunit.Abstractions;

namespace OpenSmc.Serialization.Test;

public class RawJsonTest : TestBase
{
    record RouterAddress;

    record HostAddress;

    record ClientAddress;

    public RawJsonTest(ITestOutputHelper output) : base(output)
    {
        Services.AddSingleton(sp => sp.CreateMessageHub(new ClientAddress(), ConfigureClient));
    }

    private static MessageHubConfiguration ConfigureClient(MessageHubConfiguration c)
        => c;

    [Fact]
    public void DeserializeToRawJson()
    {
        // arrange
        var subscribeRequest = new SubscribeRequest(new CollectionReference("TestCollection"));

        // act

        // assert
    }
}
