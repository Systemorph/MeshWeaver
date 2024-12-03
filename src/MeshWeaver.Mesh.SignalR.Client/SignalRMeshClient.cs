using MeshWeaver.Hosting;
using MeshWeaver.Hosting.SignalR.Client;
using MeshWeaver.Mesh;
using Microsoft.AspNetCore.Http.Connections.Client;

public class SignalRMeshClient : HubBuilder<SignalRMeshClient>
{
    public SignalRMeshClient(object address, string url) : base(address)
    {

        this.Url = url;
        WithHubConfiguration(config =>
            config
                .UseSignalRClient(url, options => HttpConnectionOptions.ForEach(o => o.Invoke(options))));

    }

    private List<Action<HttpConnectionOptions>> HttpConnectionOptions { get; init; } = [];

    public SignalRMeshClient WithHttpConnectionOptions(Action<HttpConnectionOptions> options)
    {
        HttpConnectionOptions.Add(options);
        return this;
    }
    public string Url { get;  }


    public static SignalRMeshClient Configure(object address, string url)
        => new(address, url);

    public static SignalRMeshClient Configure(string url)
        => new(new SignalRClientAddress(), url);
}
