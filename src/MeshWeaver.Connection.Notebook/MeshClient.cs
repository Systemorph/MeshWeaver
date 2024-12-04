using MeshWeaver.Connection.SignalR;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.DotNet.Interactive;
using Microsoft.DotNet.Interactive.CSharp;

namespace MeshWeaver.Connection.Notebook;

public class MeshClient : SignalRMeshClientBase<MeshClient>
{
    private IMessageHub hub;
    private ProxyKernel proxy;
    public static MeshClient Configure(string url) => new (url);


    public async Task<IMessageHub> ConnectAsync(CancellationToken ct = default)
    {
        hub = BuildHub();
        var innerKernel = await CreateInnerKernelAsync(ct);

        proxy = new ProxyKernel("mesh", innerKernel, hub);
        return hub;
    }

    protected virtual Task<Kernel> CreateInnerKernelAsync(CancellationToken ct)
    {
        Kernel kernel = new CSharpKernel().UseValueSharing();
        return Task.FromResult(kernel);
    }



    public MeshClient(string url) : base(ConnectionContext.Address ?? new NotebookAddress(), url)
    {
        ConfigureHub(config => 
            config.AddLayout(layout => layout.WithView(
            nameof(Cell),
            (host, _) => Cell(host.Reference.Id.ToString()))));
    }


    internal UiControl Cell(string id)
        => proxy.GetCell(id);
}

