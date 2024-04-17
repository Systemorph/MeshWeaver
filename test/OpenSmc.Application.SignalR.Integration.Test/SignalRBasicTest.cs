using System.Diagnostics;
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
using static OpenSmc.Application.SignalR.Integration.Test.TestSetup.SignalRTestClientConfig;
using static OpenSmc.Application.SignalR.SignalRExtensions;

namespace OpenSmc.Application.SignalR.Integration.Test;

public class SignalRBasicTest : TestBase, IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> webAppFactory;
    private static readonly UiAddress ClientAddress = new(TestUiIds.HardcodedUiId);
    private static readonly ApplicationAddress ApplicationAddress = new(TestApplication.Name, TestApplication.Environment);

    private static readonly TimeSpan signalRServerDebugTimeout = TimeSpan.FromMinutes(7);

    [Inject] private IMessageHub Client;
    private HubConnection Connection { get; set; }

    public SignalRBasicTest(WebApplicationFactory<Program> webAppFactory, ITestOutputHelper toh) : base(toh)
    {
        this.webAppFactory = webAppFactory;
        Services.AddSingleton<IMessageHub>(sp => sp.CreateMessageHub(ClientAddress, ConfigureClient));
    }

    private MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => configuration
            .WithTypes(typeof(TestRequest), typeof(ApplicationAddress), typeof(UiAddress))
            .WithRoutes(forward => forward
                .RouteAddress<object>((_, d, cancellationToken) => SendThroughSignalR(d.Package(forward.Hub.SerializationOptions), Connection, cancellationToken))
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

        if (Debugger.IsAttached)
            Connection.ServerTimeout = signalRServerDebugTimeout;

        await Connection.StartAsync();
    }

    public override async Task DisposeAsync()
    {
        try
        {
            await Connection.StopAsync(); // TODO V10: think about timeout for this (2023/09/27, Dmitry Kalabin)
        }
        finally
        {
            await Connection.DisposeAsync();
        }

        await base.DisposeAsync();
    }
}
