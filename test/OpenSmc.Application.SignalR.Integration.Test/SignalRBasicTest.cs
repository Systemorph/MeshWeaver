using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Application.Host;
using OpenSmc.Fixture;
using OpenSmc.Messaging;
using OpenSmc.ServiceProvider;
using Xunit;
using Xunit.Abstractions;
using static OpenSmc.Application.SignalR.SignalRExtensions;

namespace OpenSmc.Application.SignalR.Integration.Test;

public class SignalRBasicTest : TestBase, IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> webAppFactory;
    private static readonly UiAddress ClientAddress = new(Guid.NewGuid().ToString());
    private static readonly ApplicationAddress ApplicationAddress = new("testApp", "dev");

    [Inject] private IMessageHub Client;
    private HubConnection Connection { get; set; }

    public SignalRBasicTest(WebApplicationFactory<Program> webAppFactory, ITestOutputHelper toh) : base(toh)
    {
        this.webAppFactory = webAppFactory;
        Services.AddSingleton<IMessageHub>(sp => sp.CreateMessageHub(ClientAddress, ConfigureClient));
    }

    private MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration) => configuration;

    [Fact]
    public async Task RequestResponse()
    {
        // arrange

        // act
        var response = await Client.AwaitResponse(new TestRequest(), o => o.WithTarget(ApplicationAddress));

        // assert
        response.Should().BeAssignableTo<IMessageDelivery<TestResponse>>();
    }

    public async override Task InitializeAsync()
    {
        await base.InitializeAsync();

        var uriBuilder = new UriBuilder(webAppFactory.Server.BaseAddress) { Path = DefaultSignalREndpoint, };
        Connection = new HubConnectionBuilder()
            .WithUrl(uriBuilder.Uri,
                o =>
                {
                    o.HttpMessageHandlerFactory = _ => webAppFactory.Server.CreateHandler();
                }
            )
            .Build();

        await Connection.StartAsync();
    }

    record TestRequest : IRequest<TestResponse>;
    record TestResponse;
}
