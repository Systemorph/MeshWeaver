using MeshWeaver.Hosting.SignalR.Client;
using MeshWeaver.Messaging;
using Microsoft.DotNet.Interactive.CSharp;
using Microsoft.DotNet.Interactive;

namespace MeshWeaver.Hosting.Notebook;

public class NotebookMeshClient(Kernel kernel, object address, string url) : SignalRMeshClientBase<NotebookMeshClient>(address, url)
{
    public async Task<IMessageHub> BuildAsync(CancellationToken ct = default)
    {
        var hub = BuildHub();
        foreach (var cSharpKernel in kernel.SubKernelsAndSelf().OfType<CSharpKernel>())
            await cSharpKernel.UseMeshWeaverAsync(hub, ct);
        return hub;
    }
}
