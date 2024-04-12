using OpenSmc.Fixture;
using Xunit;
using Xunit.Abstractions;

namespace OpenSmc.Application.SignalR.Integration.Test;

public class SignalRBasicTest : TestBase
{
    private static readonly UiAddress ClientAddress = new(Guid.NewGuid().ToString());

    public SignalRBasicTest(ITestOutputHelper toh) : base(toh)
    {
    }

    [Fact]
    public void RequestResponse()
    {
        // arrange

        // act

        // assert
    }
}
