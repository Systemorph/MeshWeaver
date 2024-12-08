using System.Collections.Immutable;
using MeshWeaver.Hosting;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.SignalR.Client;

namespace MeshWeaver.Connection.SignalR;

public class SignalRMeshClient : SignalRMeshClientBase<SignalRMeshClient>
{
    private SignalRMeshClient(string url, object address) : base(url, address)
    {
    }

    public static SignalRMeshClient Configure(string url, object address = null)
        => new(url, address ?? new SignalRClientAddress());



    public IMessageHub Connect() => BuildHub();

}

public class SignalRMeshClientBase<TClient> : HubBuilder<TClient> where TClient : SignalRMeshClientBase<TClient>
{
    public string Url { get; }

    protected SignalRMeshClientBase(string url, object address) : base(address)
    {
        Url = url;
    }
    

    protected ImmutableList<Func<IHubConnectionBuilder, IHubConnectionBuilder>> ConnectionConfiguration { get; set; } =
        [];

    public TClient ConfigureConnection(
        Func<IHubConnectionBuilder, IHubConnectionBuilder> connectionConfiguration)
    {
        ConnectionConfiguration = ConnectionConfiguration.Add(connectionConfiguration);
        return This;
    }
    protected override IMessageHub BuildHub()
    {
        ConfigureHub(config =>
            config
                .UseSignalRClient(x =>
                    ConnectionConfiguration.Aggregate(
                        x.WithUrl(Url),
                        (c, cc) => cc.Invoke(c)
                    )
                )
        );
        return base.BuildHub();
    }

}
