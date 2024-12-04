using MeshWeaver.Hosting;

namespace MeshWeaver.Connection.SignalR;

public class SignalRMeshClientBase<TClient> : HubBuilder<TClient>
    where TClient:SignalRMeshClientBase<TClient>
{
    public SignalRMeshClientBase(object address, string url) : base(address)
    {

        this.Url = url;
        ConfigureHub(config =>
            config
                .UseSignalRClient(url)
            );

    }


    public string Url { get;  }


}
