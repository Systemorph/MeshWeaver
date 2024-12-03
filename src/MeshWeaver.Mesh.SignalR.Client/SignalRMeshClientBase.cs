using MeshWeaver.Hosting;
using MeshWeaver.Hosting.SignalR.Client;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Http.Connections.Client;

namespace MeshWeaver.Hosting.SignalR.Client;

public class SignalRMeshClientBase<TClient> : HubBuilder<TClient>
    where TClient:SignalRMeshClientBase<TClient>
{
    public SignalRMeshClientBase(object address, string url) : base(address)
    {

        this.Url = url;
        ConfigureHub(config =>
            config
                .UseSignalRClient(url, options => HttpConnectionOptions.ForEach(o => o.Invoke(options))));

    }

    private List<Action<HttpConnectionOptions>> HttpConnectionOptions { get; init; } = [];

    public TClient WithHttpConnectionOptions(Action<HttpConnectionOptions> options)
    {
        HttpConnectionOptions.Add(options);
        return This;
    }
    public string Url { get;  }


}
