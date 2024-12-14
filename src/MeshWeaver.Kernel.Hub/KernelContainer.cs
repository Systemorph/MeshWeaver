using System.Collections.Concurrent;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.DotNet.Interactive;
using Microsoft.DotNet.Interactive.Commands;
using Microsoft.DotNet.Interactive.CSharp;
using Microsoft.DotNet.Interactive.Events;
using Microsoft.DotNet.Interactive.Formatting;
using Microsoft.DotNet.Interactive.Formatting.Csv;
using Microsoft.DotNet.Interactive.Formatting.TabularData;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Kernel.Hub;

public class KernelContainer : IDisposable
{

    private readonly HashSet<object> subscriptions = new();
    private readonly IMeshCatalog meshCatalog;
    public KernelContainer(IServiceProvider serviceProvider, string id)
    {
        meshCatalog = serviceProvider.GetRequiredService<IMeshCatalog>();
        Kernel = CreateKernel(id);
        Hub = serviceProvider.CreateMessageHub(new KernelAddress(){Id = id}, config => config
            .AddLayout(layout =>
                layout.WithView(ctx =>
                        areas.ContainsKey(ctx.Area),
                    (_, ctx) => areas.GetValueOrDefault(ctx.Area)
                )
            )
            .WithRoutes(routes => routes.WithHandler((d,ct)=>RouteToSubHubs(routes.Hub, d, ct)))
            .WithInitialization((_, ct) => Task.WhenAll(Kernel.ChildKernels.OfType<CSharpKernel>().Select(k => k.SetValueAsync(nameof(Mesh), Hub, typeof(IMessageHub)))))
            .WithHandler<KernelCommandEnvelope>((_, request, ct) => HandleKernelCommandEnvelopeAsync(request, ct))
            .WithHandler<SubmitCodeRequest>((_, request, ct) => HandleKernelCommandAsync(request, ct))
            .WithHandler<SubscribeKernelEventsRequest>((_, request) => HandleSubscribe(request))
            .WithHandler<UnsubscribeKernelEventsRequest>((_, request) => HandleUnsubscribe(request))
            .WithHandler<KernelEventEnvelope>((_, request) => HandleKernelEvent(request))
        );
        Kernel.KernelEvents.Subscribe(PublishEventToContext);
        Hub.RegisterForDisposal(this);
    }

    private async Task<IMessageDelivery> RouteToSubHubs(IMessageHub kernelHub, IMessageDelivery request, CancellationToken cancellationtoken)
    {
        try
        {
            if (request.Target is not HostedAddress hosted || !kernelHub.Address.Equals(hosted.Host))
                return request;

            var hub = kernelHub.GetHostedHub(hosted.Address, true);
            if (hub is not null)
            {
                hub.DeliverMessage(request);
                return request.Processed();
            }

            var (addressType, addressId) = SerializationExtensions.GetAddressTypeAndId(hosted.Address);

            var meshNode = await meshCatalog.GetNodeAsync(addressType, addressId);
            if (meshNode == null)
                return DeliveryFailure(kernelHub, request, $"No mesh node was found for {hosted.Address}", hosted);

            if (meshNode.StartupScript is null)
                return DeliveryFailure(kernelHub, request, $"No startup script is defined for {hosted.Address}",
                    hosted);

            var result = await Kernel.SendAsync(new SubmitCode(meshNode.StartupScript), cancellationtoken);
            if(!result.Events.Any(e => e is CommandSucceeded))
                return DeliveryFailure(
                    kernelHub, request,
                    new DeliveryFailure(request)
                {
                    ErrorType = ErrorType.StartupScriptFailed,
                    Message = $"Startup script failed: {string.Join(',', result.Events.OfType<DiagnosticsProduced>().Select(d => d.FormattedDiagnostics))}",

                });


            hub = kernelHub.GetHostedHub(hosted.Address, true);
            if (hub is not null)
            {
                hub.DeliverMessage(request);
                return request.Processed();
            }
            return DeliveryFailure(kernelHub, request, $"Could not start hub for {hosted.Address}", hosted);
        }
        catch(Exception e)
        {
            kernelHub.Post(new DeliveryFailure(request)
            {
                ErrorType = ErrorType.Exception,
                ExceptionType = e.GetType().Name,
                Message = e.Message,
            }, o => o.ResponseFor(request));
            return request.Failed($"No mesh node was found for {request.Target}");
        }
    }

    private static IMessageDelivery DeliveryFailure(IMessageHub kernelHub, IMessageDelivery request, string message,
        HostedAddress hosted)
    {
        var deliveryFailure = new DeliveryFailure(request)
        {
            ExceptionType = "MeshNodeNotFound",
            Message = message,
        };
        return DeliveryFailure(kernelHub, request, deliveryFailure);
    }

    private static IMessageDelivery DeliveryFailure(IMessageHub kernelHub, IMessageDelivery request, DeliveryFailure deliveryFailure)
    {
        kernelHub.Post(deliveryFailure, o => o.ResponseFor(request));
        return request.Failed(deliveryFailure.Message);
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
        ret.AddAssemblyReferences([typeof(IMessageHub).Assembly.Location, typeof(KernelAddress).Assembly.Location]);
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

    public Task<IMessageDelivery> HandleKernelCommandEnvelopeAsync(IMessageDelivery<KernelCommandEnvelope> request, CancellationToken ct)
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
