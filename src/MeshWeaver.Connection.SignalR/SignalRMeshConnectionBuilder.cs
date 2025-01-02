using System.Collections.Immutable;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeshWeaver.Connection.SignalR
{
    public record SignalRMeshConnectionBuilder
    {
        private readonly IServiceProvider serviceProvider;

        public SignalRMeshConnectionBuilder(IServiceProvider serviceProvider, object address, string url)
        {
            this.serviceProvider = serviceProvider;
            Address = address;
            Url = url;
            ConnectionConfigurations = []; //[x => x.WithUrl(Url).WithAutomaticReconnect()];
        }


        public object Address { get; }
        public string Url { get; }

        public SignalRMeshConnectionBuilder WithSignalRConfiguration(
            Func<IHubConnectionBuilder, IHubConnectionBuilder> connectionConfiguration)
            => this with { ConnectionConfigurations = ConnectionConfigurations.Add(connectionConfiguration) };

        public ImmutableList<Func<IHubConnectionBuilder, IHubConnectionBuilder>> ConnectionConfigurations { get; init; }

        public HubConnection Build() => BuildHubConnection(serviceProvider);

        private HubConnection BuildHubConnection(IServiceProvider serviceProvider)
        {
            var builder = ConnectionConfigurations.Aggregate<Func<IHubConnectionBuilder, IHubConnectionBuilder>, IHubConnectionBuilder>(new HubConnectionBuilder(),
                (builder, connectionConfiguration) => connectionConfiguration.Invoke(builder));
            builder.Services.AddSingleton<IHubProtocol>(_ =>
                new JsonHubProtocol(new OptionsWrapper<JsonHubProtocolOptions>(new()
                {
                    PayloadSerializerOptions =
                        serviceProvider.GetRequiredService<IMessageHub>().JsonSerializerOptions
                })));

            var hubConnection = builder.Build();

            var clientId = Address.ToString();
            var logger = serviceProvider.GetRequiredService<ILogger<SignalRMeshConnectionBuilder>>();
            hubConnection.Reconnecting += async (exception) =>
            {
                if (exception is not null)
                    logger.LogWarning("Disconnected from SignalR connection for {Address}:\n{Exception}", Address, exception);

                try
                {
                    var connected =
                        await hubConnection.InvokeAsync<MeshConnection>(
                            "Connect",
                            clientId);

                    if (connected.Status != ConnectionStatus.Connected)
                        throw new MeshException("Couldn't connect.");

                }
                catch (Exception ex)
                {
                    logger.LogError("Unable connecting SignalR connection for {Address} :\n{Exception}", Address, ex);
                    throw;
                }
                // Your callback logic here
                Console.WriteLine("Reconnecting...");
            };
            return hubConnection;
        }
    }
}
