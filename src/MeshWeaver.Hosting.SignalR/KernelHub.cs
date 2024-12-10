using System.Collections.Concurrent;
using System.Reactive.Subjects;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.AspNetCore.SignalR;
using Microsoft.DotNet.Interactive;
using Microsoft.DotNet.Interactive.Commands;
using Microsoft.DotNet.Interactive.Connection;
using Microsoft.DotNet.Interactive.CSharp;
using Microsoft.DotNet.Interactive.Events;
using Microsoft.DotNet.Interactive.Formatting;
using Microsoft.DotNet.Interactive.Formatting.Csv;
using Microsoft.DotNet.Interactive.Formatting.TabularData;

namespace MeshWeaver.Hosting.SignalR;

public class KernelHub(IMessageHub hub) : Hub
{

    private static readonly ConcurrentDictionary<string,NotebookHubAdapter> Kernels = new();


    public Task SubmitCommand(string kernelCommandEnvelope)
    {
        return KernelCommandFromServer(kernelCommandEnvelope);
    }

    public async Task KernelCommandFromServer(string kernelCommandEnvelope)
    {
        var envelope = KernelCommandEnvelope.Deserialize(kernelCommandEnvelope);
        var command = envelope.Command;
        var connectionId = Context.ConnectionId;
        if (!Kernels.TryGetValue(connectionId, out var notebookAdapter))
        {
            Kernels[Context.ConnectionId] = notebookAdapter = new NotebookHubAdapter(hub.ServiceProvider, connectionId);
            await notebookAdapter.ConnectAsync();
            notebookAdapter.Kernel.KernelEvents.Subscribe(x => PublishEventToContext(x, connectionId));
        };
        await notebookAdapter.SendAsync(command);
    }

    public Task KernelEvent(string kernelEventEnvelope)
    {
        return KernelEventFromServer(kernelEventEnvelope);
    }

    public async Task KernelEventFromServer(string kernelEventEnvelope)
    {
        var envelope = KernelEventEnvelope.Deserialize(kernelEventEnvelope);
        //await kernel.HandleKernelEventFromClientAsync(envelope);
    }

    public async Task Connect()
    {
        await Clients.Caller.SendAsync("connected");
        //disposables.Add(kernel.KernelEvents.Subscribe(PublishEventToContext));

    }

    private async void PublishEventToContext(KernelEvent @event, string connectionId)
    {
        var eventEnvelope = KernelEventEnvelope.Create(@event);

        var client = Clients.Client(connectionId);
        await client.SendAsync("kernelEventFromServer", KernelEventEnvelope.Serialize(eventEnvelope));

        //fis : remove this later
        await client.SendAsync("kernelEvent", KernelEventEnvelope.Serialize(eventEnvelope));

    }

    
    public override Task OnDisconnectedAsync(Exception exception)
    {
        if(Kernels.TryRemove(Context.ConnectionId, out var kernel))
            kernel.Dispose();
        return base.OnDisconnectedAsync(exception);
    }
}
public class NotebookHubAdapter : IDisposable
{
    public NotebookHubAdapter(IServiceProvider serviceProvider, string id)
    {
        Hub = serviceProvider.CreateMessageHub(new NotebookAddress(){Id = id}, config => config
            .AddLayout(layout =>
                layout.WithView(ctx =>
                        areas.ContainsKey(ctx.Area),
                    (_, ctx) => areas.GetValueOrDefault(ctx.Area)
                )
            )
        );
        Kernel = CreateKernel(id);
    }

    private string LayoutAreaUrl { get; set; } = "https://localhost:65260/area/";

    private readonly ConcurrentDictionary<string, UiControl> areas = new();
    public IMessageHub Hub { get; }
    public CompositeKernel Kernel { get; }

    protected CompositeKernel CreateKernel(string id)
    {
        Formatter.SetPreferredMimeTypesFor(typeof(TabularDataResource), HtmlFormatter.MimeType, CsvFormatter.MimeType);

        var ret = new CSharpKernel()
            .UseKernelHelpers()
            .UseValueSharing();
        var composite = new CompositeKernel("mesh");
        composite.Add(ret);
        return composite;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        var csharp = Kernel.ChildKernels.OfType<CSharpKernel>().First();
        csharp.AddAssemblyReferences(
            [
                typeof(MessageHub).Assembly.Location,
                typeof(UiControl).Assembly.Location,
                typeof(ApplicationAddress).Assembly.Location,
                typeof(DataExtensions).Assembly.Location,
                typeof(LayoutAreaReference).Assembly.Location,
            ]
        );

        var addressType = Hub.Configuration
                .TypeRegistry
            .GetCollectionName(Hub.Address.GetType())
            ;

        var addressId = Hub.Address.ToString();


        Formatter.Register<UiControl>(
            (control, writer) =>
            {
                var id = Guid.NewGuid().AsString();
                areas[id] = control;
                writer.Write($"<iframe src='{LayoutAreaUrl}{addressType}/{addressId}/{id}'></iframe>");
            }, HtmlFormatter.MimeType);

        await csharp.SetValueAsync(nameof(Mesh), Hub, typeof(IMessageHub));

    }

    private static string ReplaceLastSegmentWithArea(string url)
    {
        var uri = new Uri(url);
        var segments = uri.Segments;
        segments[^1] = "area/";
        return new Uri(uri, string.Join("", segments)).ToString();
    }

    public Task SendAsync(KernelCommand command)
    {
        return Kernel.SendAsync(command);
    }

    public void Dispose()
    {
        Hub?.Dispose();
        Kernel?.Dispose();
    }
}
