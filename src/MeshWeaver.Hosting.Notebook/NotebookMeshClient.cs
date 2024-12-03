using System.Collections.Concurrent;
using MeshWeaver.Hosting.SignalR.Client;
using MeshWeaver.Messaging;
using Microsoft.DotNet.Interactive.CSharp;
using Microsoft.DotNet.Interactive;
using MeshWeaver.Layout;
using Microsoft.DotNet.Interactive.Commands;
using Microsoft.DotNet.Interactive.Events;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Hosting.Notebook;

public class NotebookMeshClient : SignalRMeshClientBase<NotebookMeshClient>
{
    private IMessageHub hub;
    private string addressType;
    private string addressId;
    public async Task<IMessageHub> ConnectAsync(CancellationToken ct = default)
    {
        hub = BuildHub();
        addressType = hub.ServiceProvider.GetRequiredService<ITypeRegistry>().GetCollectionName(address1.GetType());
        addressId = address1.ToString();

        foreach (var cSharpKernel in kernel1.SubKernelsAndSelf().OfType<CSharpKernel>())
        {
            await cSharpKernel.UseMeshWeaverAsync(hub, ct);
            cSharpKernel.KernelEvents.OfType<ReturnValueProduced>().Subscribe(async e =>
            {
                await InspectResultsMiddleware(cSharpKernel, e);
            });
        }
        return hub;
    }

    private async Task InspectResultsMiddleware(CSharpKernel kernel, ReturnValueProduced returnValueProduced)
    {
        var context = KernelInvocationContext.Current;
        if (context?.Command is SubmitCode submitCode && returnValueProduced.Value is UiControl control)
        {
            var id = Guid.NewGuid().AsString();
            cells[id] = control;
            var url = new LayoutAreaReference(nameof(Cell)) { Id = id }.ToHref(addressType, addressId);
            var iframeHtml = $"<iframe src='{url}'></iframe>";
            kernel.Display(iframeHtml, "text/html");
            context.Complete(submitCode);
        }
    }
    private readonly ConcurrentDictionary<string, UiControl> cells = new();
    private readonly Kernel kernel1;
    private readonly object address1;

    public NotebookMeshClient(Kernel kernel, object address, string url) : base(address, url)
    {
        kernel1 = kernel;
        address1 = address;
        ConfigureHub(config => config.AddLayout(layout => layout.WithView(
            nameof(Cell),
            (host, ctx) => Cell(host.Reference.Id.ToString()))));
    }


    private UiControl Cell(string id)
        => cells.GetValueOrDefault(id);
}

