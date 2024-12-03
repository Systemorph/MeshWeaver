using MeshWeaver.Hosting.SignalR.Client;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

public class SignalRMeshClient(object address, string url) : SignalRMeshClientBase<SignalRMeshClient>(address, url)
{
    public static SignalRMeshClient Configure(object address, string url)
        => new(address, url);

    public static SignalRMeshClient Configure(string url)
        => new(new SignalRClientAddress(), url);

    public IMessageHub Build() => BuildHub();

}
