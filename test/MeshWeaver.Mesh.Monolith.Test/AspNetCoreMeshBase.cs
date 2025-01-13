using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using MeshWeaver.Connection.Notebook;
using MeshWeaver.Connection.SignalR;
using MeshWeaver.Hosting.SignalR;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace MeshWeaver.Hosting.Monolith.Test
{
    public abstract class AspNetCoreMeshBase(ITestOutputHelper output) : MonolithMeshTestBase(output)
    {
        protected IHost Host;
        protected TestServer Server;
        public static string SignalREndPoint = SignalRConnectionHub.EndPoint;
        public static string KernelEndPoint = KernelHub.EndPoint;
        public string SignalRUrl => $"{Server.BaseAddress}{SignalREndPoint}";
        public override async Task InitializeAsync()
        {
            await base.InitializeAsync();
            var builder = (MeshHostBuilder)ConfigureMesh(
                new MeshHostBuilder(new HostBuilder(), 
                    new MeshAddress())
                );
            Host = await builder.Host
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseTestServer();
                    webBuilder.ConfigureServices(services =>
                    {
                        services.AddScoped(builder.BuildHub);
                        services
                            .AddSignalR().AddJsonProtocol();
                        services.AddSingleton<IHubProtocol, JsonHubProtocol>(sp =>
                            new JsonHubProtocol(new OptionsWrapper<JsonHubProtocolOptions>(new()
                            {
                                PayloadSerializerOptions = sp.GetRequiredService<IMessageHub>().JsonSerializerOptions
                            })));
                        services.AddSignalRHubs();
                    });
                    webBuilder.Configure(app =>
                    {
                        app.UseRouting();
                        app.MapMeshWeaverHubs();
                    });
                })
                .StartAsync();

            Server = Host.GetTestServer();
            //for notebooks
            ConnectionSettings.HttpConnectionOptions = 
                // for signalR clients
                SignalRClientExtensions.HttpConnectionOptions = 
                    // map to test server
                    x => x.HttpMessageHandlerFactory = _ => Server.CreateHandler();
        }

        protected readonly ConcurrentBag<IDisposable> Disposables = new();
        public override async Task DisposeAsync()
        {
            while (Disposables.TryTake(out var d))
                d.Dispose();
            await base.DisposeAsync();
            await Host.StopAsync();
            Server.Dispose();
            Host.Dispose();
        }

    }
}
