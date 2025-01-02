using System.Collections.Immutable;
using MeshWeaver.Hosting;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Connection.SignalR;

public class SignalRMeshClient : SignalRMeshClientBase<SignalRMeshClient>
{
    private SignalRMeshClient(string url, object address) : base(url, address)
    {
    }

    public static SignalRMeshClient Configure(string url, object address = null)
        => new(url, address ?? new SignalRClientAddress());




}

public class SignalRMeshClientBase<TClient> : HubBuilder<TClient> where TClient : SignalRMeshClientBase<TClient>
{
    public string Url { get; }

    protected SignalRMeshClientBase(string url, object address) : base(address)
    {
        Url = url;
    }
    

    protected ImmutableList<Func<SignalRMeshConnectionBuilder, SignalRMeshConnectionBuilder>> ConnectionConfiguration { get; set; } =
        [];

    public TClient ConfigureConnection(
        Func<SignalRMeshConnectionBuilder, SignalRMeshConnectionBuilder> connectionConfiguration)
    {
        ConnectionConfiguration = ConnectionConfiguration.Add(connectionConfiguration);
        return This;
    }
    protected override IMessageHub BuildHub()
    {
        ConfigureHub(config =>
            config
                .UseSignalRClient(Url, x =>
                    ConnectionConfiguration.Aggregate(x,
                        (c, cc) => cc.Invoke(c)
                    )
                )
        );
        return base.BuildHub();
    }
    public virtual async Task<IMessageHub> ConnectAsync(CancellationToken ct = default)
    {
        var ret = BuildHub();
        var logger = ret.ServiceProvider.GetRequiredService<ILogger<TClient>>();
        try
        {
            logger.LogInformation("Trying to connect {Address} to the mesh {Url}", ret.Address, Url);
            await ret.AwaitResponse(new PingRequest(), o => o.WithTarget(new MeshAddress()), ct);
            logger.LogInformation("Connection succeeded.");
        }
        catch (Exception e)
        {
            logger.LogError("Connection failed: {Exception}", e);
            throw;
        }
        return ret;
    }
}

