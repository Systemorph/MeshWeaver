using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Fixture;
using OpenSmc.Messaging;
using Xunit;
using Xunit.Abstractions;

namespace OpenSmc.Application.SignalR.Integration.Test;

public class SignalRBasicTest : TestBase
{
    private static readonly UiAddress ClientAddress = new(Guid.NewGuid().ToString());

    public SignalRBasicTest(ITestOutputHelper toh) : base(toh)
    {
        Services.AddSingleton<IMessageHub>(sp => sp.CreateMessageHub(ClientAddress, ConfigureClient));
    }

    private MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration) => configuration;

    [Fact]
    public void RequestResponse()
    {
        // arrange

        // act

        // assert
    }
}
