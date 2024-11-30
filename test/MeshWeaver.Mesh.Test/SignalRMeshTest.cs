using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Application;
using MeshWeaver.Mesh.SignalR.Client;
using MeshWeaver.Mesh.SignalR.Server;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Mesh.Test
{
    public class SignalRMeshTest(ITestOutputHelper output) : ConfiguredMeshTestBase(output)
    {
        private IHost host;
        private TestServer server;

        public HttpMessageHandler HttpMessageHandler => server.CreateHandler();

        protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration config)
        {
            return config
                .AddSignalRClient("http://localhost/connection", HttpMessageHandler)
                .WithTypes(typeof(Ping), typeof(Pong));
        }

        public override async Task InitializeAsync()
        {
            host = await new HostBuilder()
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseTestServer();
                    webBuilder.ConfigureServices(services =>
                    {
                        services.AddSignalR();
                    });
                    webBuilder.Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints =>
                        {
                            endpoints.MapHub<SignalRConnectionHub>(SignalRConnectionHub.UrlPattern);
                        });
                    });
                })
            .StartAsync();

            server = host.GetTestServer();

            await base.InitializeAsync();
        }

        [Fact]
        public async Task BasicMessage()
        {
            var address = new ClientAddress();
            var client = ServiceProvider.CreateMessageHub(address, ConfigureClient);

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
}
