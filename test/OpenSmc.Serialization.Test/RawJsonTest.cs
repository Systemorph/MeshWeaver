using OpenSmc.Data;
using OpenSmc.Fixture;
using Xunit.Abstractions;

namespace OpenSmc.Serialization.Test;

public class RawJsonTest : TestBase
{
    record RouterAddress;

    record HostAddress;

    record ClientAddress;

    public RawJsonTest(ITestOutputHelper output) : base(output)
    {
    }

    [Fact]
    public void DeserializeToRawJson()
    {
        // arrange
        var subscribeRequest = new SubscribeRequest(new CollectionReference("TestCollection"));

        // act

        // assert
    }
}
