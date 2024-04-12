using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Application.Host;
using OpenSmc.Fixture;
using OpenSmc.Messaging;
using OpenSmc.ServiceProvider;
using Xunit;
using Xunit.Abstractions;

namespace OpenSmc.Application.SignalR.Integration.Test;

public class SignalRBasicTest : TestBase, IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> webAppFactory;
    private static readonly UiAddress ClientAddress = new(Guid.NewGuid().ToString());
    private static readonly ApplicationAddress ApplicationAddress = new("testApp", "dev");

    [Inject] private IMessageHub Client;

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
    }

    record TestRequest : IRequest<TestResponse>;
    record TestResponse;
}
