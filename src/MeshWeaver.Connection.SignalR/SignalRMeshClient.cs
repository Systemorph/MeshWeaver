using MeshWeaver.Hosting;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.SignalR.Client;

namespace MeshWeaver.Connection.SignalR;

public class SignalRMeshClient : HubBuilder<SignalRMeshClient>
{
    private SignalRMeshClient(object address, Func<HubConnectionBuilder, IHubConnectionBuilder> connectionConfiguration) : base(address)
    {
        ConfigureHub(config =>
            config
                .UseSignalRClient(connectionConfiguration)
        );

    }
    
    public static SignalRMeshClient Configure(object address, Func<HubConnectionBuilder, IHubConnectionBuilder> connectionConfiguration)
        => new(address, connectionConfiguration);


    public IMessageHub Connect()
    {
        return BuildHub();
    }
}
