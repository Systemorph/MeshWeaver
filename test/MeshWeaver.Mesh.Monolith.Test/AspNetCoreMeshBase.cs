using System.Collections.Concurrent;
using MeshWeaver.Connection.Notebook;
using MeshWeaver.Hosting.SignalR;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace MeshWeaver.Hosting.Monolith.Test
{
    public abstract class AspNetCoreMeshBase(ITestOutputHelper output) : ConfiguredMeshTestBase(output)
    {
        protected IHost Host;
        protected TestServer Server;
        public static string SignalREndPoint = "connection";
        public string SignalRUrl => $"{Server.BaseAddress}{SignalREndPoint}";
        public HttpMessageHandler HttpMessageHandler => Server.CreateHandler();
        public override async Task InitializeAsync()
        {
            await base.InitializeAsync();
            var builder = (MeshHostBuilder)ConfigureMesh(new MeshHostBuilder(new HostBuilder(), new MeshAddress()));
            Host = await builder.Host
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
                        EndpointRoutingApplicationBuilderExtensions.UseRouting(app);
                        EndpointRoutingApplicationBuilderExtensions.UseEndpoints(app, endpoints =>
                        {
                            HubEndpointRouteBuilderExtensions.MapHub<SignalRConnectionHub>(endpoints, $"/{SignalREndPoint}");
                        });
                    });
                })
                .StartAsync();

            Server = Host.GetTestServer();
            MeshConnection.ConfigurationOptions =
                connection => connection.WithUrl(SignalRUrl, options => options.HttpMessageHandlerFactory = (_ => Server.CreateHandler()));
        }

        protected readonly ConcurrentBag<IDisposable> Disposables = new();
        public override async Task DisposeAsync()
        {
            while (Disposables.TryTake(out var d))
                d.Dispose();
            await Host.StopAsync();
            Server.Dispose();
            Host.Dispose();
            await base.DisposeAsync();
        }

    }
}
