using FluentAssertions;
using MeshWeaver.Mesh.SignalR;
using MeshWeaver.Mesh.SignalR.Client;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR.Client;
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
        private HubConnection hubConnection;
        private TestServer server;

        protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration config)
        {
            return config
                .WithRoutes(routes => routes.AddSignalRClient("http://localhost/connection"))
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
                            endpoints.MapHub<ConnectionHub>("/notebook");
                        });
                    });
                })
            .StartAsync();

            server = host.GetTestServer();

            hubConnection = new HubConnectionBuilder()
                .WithUrl("http://localhost/connection/{addressType}/{id}", options =>
                {
                    options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                })
                .Build();

            await hubConnection.StartAsync();
        }



        [Fact]
        public async Task BasicMessage()
        {
            var address = new ClientAddress();
            var hub = MeshHub.ServiceProvider.CreateMessageHub(address, ConfigureClient);

        }
        public override async Task DisposeAsync()
        {
            await hubConnection.DisposeAsync();
            await host.StopAsync();
            server.Dispose();
            host.Dispose();
            await base.DisposeAsync();
        }

    }
}
