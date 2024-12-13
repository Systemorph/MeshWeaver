using System.Collections.Concurrent;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.DotNet.Interactive;
using Microsoft.DotNet.Interactive.Commands;
using Microsoft.DotNet.Interactive.CSharp;
using Microsoft.DotNet.Interactive.Events;
using Microsoft.DotNet.Interactive.Formatting;
using Microsoft.DotNet.Interactive.Formatting.Csv;
using Microsoft.DotNet.Interactive.Formatting.TabularData;

namespace MeshWeaver.Kernel.Hub;

public class KernelContainer : IDisposable
{

    private readonly HashSet<object> subscriptions = new();
    public KernelContainer(IServiceProvider serviceProvider, string id)
    {
        Hub = serviceProvider.CreateMessageHub(new KernelAddress(){Id = id}, config => config
            .AddLayout(layout =>
                layout.WithView(ctx =>
                        areas.ContainsKey(ctx.Area),
                    (_, ctx) => areas.GetValueOrDefault(ctx.Area)
                )
            )
            .WithHandler<KernelCommandEnvelope>((_, request, ct) => HandleKernelCommandAsync(request, ct))
            .WithHandler<SubmitCodeRequest>((_, request, ct) => HandleKernelCommandAsync(request, ct))
            .WithHandler<SubscribeKernelEventsRequest>((_, request) => HandleSubscribe(request))
            .WithHandler<UnsubscribeKernelEventsRequest>((_, request) => HandleUnsubscribe(request))
            .WithHandler<KernelEventEnvelope>((_, request) => HandleKernelEvent(request))
        );
        Kernel = CreateKernel(id);
        Kernel.KernelEvents.Subscribe(PublishEventToContext);
        Hub.RegisterForDisposal(this);
    }

    private void PublishEventToContext(KernelEvent @event)
    {
        var eventEnvelope = Microsoft.DotNet.Interactive.Connection.KernelEventEnvelope.Serialize(Microsoft.DotNet.Interactive.Connection.KernelEventEnvelope.Create(@event));
        foreach (var a in subscriptions)
            Hub.Post(new KernelEventEnvelope(eventEnvelope), o => o.WithTarget(a));
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


    private IMessageDelivery HandleKernelEvent(IMessageDelivery<KernelEventEnvelope> @event)
    {
        // TODO V10: here we need to see what to do. cancellation will come through here. (12.12.2024, Roland Bürgi)
        return @event.Processed();
    }

    public Task<IMessageDelivery> HandleKernelCommandAsync(IMessageDelivery<KernelCommandEnvelope> request, CancellationToken ct)
    {
        subscriptions.Add(request.Sender);
        var envelope = Microsoft.DotNet.Interactive.Connection.KernelCommandEnvelope.Deserialize(request.Message.Command);
        var command = envelope.Command;
        return SubmitCommand(request, ct, command);
    }
    public Task<IMessageDelivery> HandleKernelCommandAsync(IMessageDelivery<SubmitCodeRequest> request, CancellationToken ct)
    {
        subscriptions.Add(request.Sender);
        var command = new SubmitCode(request.Message.Code);
        return SubmitCommand(request, ct, command);
    }

    private async Task<IMessageDelivery> SubmitCommand(IMessageDelivery request, CancellationToken ct, KernelCommand command)
    {
        await Kernel.SendAsync(command, ct);
        return request.Processed();
    }

    public IMessageDelivery HandleSubscribe(IMessageDelivery<SubscribeKernelEventsRequest> request)
    {
        subscriptions.Add(request.Sender);
        return request.Processed();
    }
    public IMessageDelivery HandleUnsubscribe(IMessageDelivery<UnsubscribeKernelEventsRequest> request)
    {
        subscriptions.Remove(request.Sender);
        return request.Processed();
    }

    public void Dispose()
    {
        Kernel.Dispose();
    }
}
