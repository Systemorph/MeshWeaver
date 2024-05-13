using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Application.Host;
using OpenSmc.Application.Orleans;
using OpenSmc.Fixture;
using OpenSmc.Messaging;
using OpenSmc.Messaging.Serialization;
using OpenSmc.ServiceProvider;
using Xunit;
using Xunit.Abstractions;
using static OpenSmc.Application.SignalR.SignalRExtensions;
using static OpenSmc.SignalR.Fixture.SignalRClientHubExtensions;

namespace OpenSmc.Application.SignalR.Integration.Test;

public class SignalRBasicTest : TestBase, IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> webAppFactory;
    private static readonly UiAddress ClientAddress = new(Guid.NewGuid().ToString());
    private static readonly ApplicationAddress ApplicationAddress = new(TestApplication.Name, TestApplication.Environment);

    [Inject] private IMessageHub Client;

    public SignalRBasicTest(WebApplicationFactory<Program> webAppFactory, ITestOutputHelper toh) : base(toh)
    {
        this.webAppFactory = webAppFactory;
        Services.AddSingleton<IMessageHub>(sp => sp.CreateMessageHub(ClientAddress, ConfigureClient));
    }

    private MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => configuration
            .WithTypes(typeof(TestRequest), typeof(ApplicationAddress), typeof(UiAddress))
            .AddSignalRClient(conf => conf
                .WithHubConnectionConfiguration(connectionConfig => connectionConfig
                    .WithUrl(new UriBuilder(webAppFactory.Server.BaseAddress) { Path = DefaultSignalREndpoint, }.Uri,
                        o =>
                        {
                            o.HttpMessageHandlerFactory = _ => webAppFactory.Server.CreateHandler();
                        }
                    )
                )
            );

    [Fact]
    public async Task RequestResponse()
    {
        // arrange

        // act
        var response = await Client.AwaitResponse(new TestRequest(), o => o.WithTarget(ApplicationAddress));

        // assert
        response.Should().BeAssignableTo<IMessageDelivery<TestResponse>>();
    }

    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
    }
}
