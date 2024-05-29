using OpenSmc.Data;

namespace OpenSmc.Serialization.Test;

public class RawJsonTest
{
    record RouterAddress;

    record HostAddress;

    record ClientAddress;

    [Fact]
    public void DeserializeToRawJson()
    {
        // arrange
        var subscribeRequest = new SubscribeRequest(new CollectionReference("TestCollection"));

        // act

        // assert
    }
}
