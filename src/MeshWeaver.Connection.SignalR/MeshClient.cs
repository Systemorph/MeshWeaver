using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.Connection.SignalR;

public class MeshClient(object address, string url) : SignalRMeshClientBase<MeshClient>(address, url)
{
    public static MeshClient Configure(object address, string url)
        => new(address, url);

    public static MeshClient Configure(string url)
        => new(new SignalRClientAddress(), url);

    public IMessageHub Connect() => BuildHub();

}
