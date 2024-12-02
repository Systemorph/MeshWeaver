using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Application;
using MeshWeaver.Hosting;
using MeshWeaver.Mesh.Contract;
using MeshWeaver.Mesh.SignalR.Client;
using MeshWeaver.Mesh.SignalR.Server;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Mesh.Test;

public class SignalRMeshTest(ITestOutputHelper output) : ConfiguredMeshTestBase(output)
{
    private IHost host;
    private TestServer server;

    public HttpMessageHandler HttpMessageHandler => server.CreateHandler();

    protected MessageHubConfiguration ConfigureClient(MessageHubConfiguration config)
    {
        return config
            .UseSignalRMesh("http://localhost/connection",
                options =>
            {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
            })
            .WithTypes(typeof(Ping), typeof(Pong));
    }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        var builder = (MeshHostBuilder)ConfigureMesh(new MeshHostBuilder(new HostBuilder(), new MeshAddress()));
        host = await builder.Host
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(services =>
                {
                    services.AddScoped(CreateMesh);
                    services
                        .AddSignalR().AddJsonProtocol();
                    services.AddSingleton<IHubProtocol, JsonHubProtocol>(sp =>
                        new JsonHubProtocol(new OptionsWrapper<JsonHubProtocolOptions>(new()
                        {
                            PayloadSerializerOptions = sp.GetRequiredService<IMessageHub>().JsonSerializerOptions
                        })));

                });
                webBuilder.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapHub<SignalRConnectionHub>("/connection");
                    });
                });
            })
            .StartAsync();

        server = host.GetTestServer();

    }


    [Fact]
    public async Task PingPong()
    {
        var address = new ClientAddress();
        var client = ServiceProvider.CreateMessageHub(address, config => config
            .UseSignalRMesh("http://localhost/connection",
                options =>
            {
                options.HttpMessageHandlerFactory = (_ => server.CreateHandler());
            })
            .WithTypes(typeof(Ping), typeof(Pong)));

        var response = await client.AwaitResponse(new Ping(),
            o => o.WithTarget(new ApplicationAddress(TestApplicationAttribute.Test)),
            new CancellationTokenSource(3000.Seconds()).Token);
        response.Message.Should().BeOfType<Pong>();
    }

    public override async Task DisposeAsync()
    {
        await host.StopAsync();
        server.Dispose();
        host.Dispose();
        await base.DisposeAsync();
    }
}
