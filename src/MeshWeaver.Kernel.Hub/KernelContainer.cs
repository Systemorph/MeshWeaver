using System.Collections.Concurrent;
using System.Reactive.Linq;
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
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Kernel.Hub;

public class KernelContainer : IDisposable
{

    private readonly HashSet<object> subscriptions = new();
    private readonly IMeshCatalog meshCatalog;
    private readonly IMessageHub executionHub;
    private readonly ILogger<KernelContainer> logger;
    public KernelContainer(IServiceProvider serviceProvider, string id)
    {
        meshCatalog = serviceProvider.GetRequiredService<IMeshCatalog>();
        Kernel = CreateKernel(id);
        Hub = serviceProvider.CreateMessageHub(
            new KernelAddress(){Id = id}, 
            config => config
            .AddLayout(layout =>
                layout.WithView(ctx =>
                        areas.ContainsKey(ctx.Area),
                    (_, ctx) => areas.GetValueOrDefault(ctx.Area)
                )
            )
            .WithRoutes(routes => routes.WithHandler((d,ct)=>RouteToSubHubs(routes.Hub, d, ct)))
            .WithInitialization((_, _) => Task.WhenAll(Kernel.ChildKernels.OfType<CSharpKernel>().Select(k => k.SetValueAsync(nameof(Mesh), Hub, typeof(IMessageHub)))))
            .WithHandler<KernelCommandEnvelope>((_, request) => HandleKernelCommandEnvelope(request))
            .WithHandler<SubmitCodeRequest>((_, request) => HandleKernelCommand(request))
            .WithHandler<SubscribeKernelEventsRequest>((_, request) => HandleSubscribe(request))
            .WithHandler<UnsubscribeKernelEventsRequest>((_, request) => HandleUnsubscribe(request))
            .WithHandler<KernelEventEnvelope>((_, request) => HandleKernelEvent(request))
        );
        executionHub = Hub.ServiceProvider.CreateMessageHub(new KernelExecutionAddress());
        Kernel.KernelEvents.Subscribe(PublishEventToContext);
        Hub.RegisterForDisposal(this);
        logger = Hub.ServiceProvider.GetRequiredService<ILogger<KernelContainer>>();
    }

    private async Task<IMessageDelivery> RouteToSubHubs(IMessageHub kernelHub, IMessageDelivery request, CancellationToken cancellationToken)
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

            var (addressType, addressId) = MessageHubExtensions.GetAddressTypeAndId(hosted.Address);

            var meshNode = await meshCatalog.GetNodeAsync(addressType, addressId);
            if (meshNode == null)
                return DeliveryFailure(kernelHub, request, $"No mesh node was found for {hosted.Address}");

            if (meshNode.StartupScript is null)
                return DeliveryFailure(kernelHub, request, $"No startup script is defined for {hosted.Address}");

            var result = await Kernel.SendAsync(new SubmitCode(meshNode.StartupScript), cancellationToken);
            if (!result.Events.Any(e => e is CommandSucceeded))
            {
                var message = $"Startup script failed:\n{string.Join('\n',
                    result.Events.OfType<DiagnosticsProduced>().SelectMany(d => d.FormattedDiagnostics.Select(z => z.Value)))}";

                return DeliveryFailure(
                    kernelHub, 
                    new DeliveryFailure(request) { ErrorType = ErrorType.StartupScriptFailed, Message = message });
            }


            hub = kernelHub.GetHostedHub(hosted.Address, true);
            if (hub is not null)
            {
                hub.DeliverMessage(request.ForwardTo(hosted.Address));
                return request.Processed();
            }

            return DeliveryFailure(kernelHub, request, $"Could not start hub for {hosted.Address}");
        }
        catch (ObjectDisposedException)
        {
            logger.LogInformation("Trying to invoke kernel command on disposed kernel: {Address}: {Command}", Hub.Address, request);
            return request.Failed($"Kernel disposed");
        }
        catch (Exception e)
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

    private static IMessageDelivery DeliveryFailure(IMessageHub kernelHub, IMessageDelivery request, string message)
    {
        var deliveryFailure = new DeliveryFailure(request)
        {
            ExceptionType = "MeshNodeNotFound",
            Message = message,
        };
        return DeliveryFailure(kernelHub, deliveryFailure);
    }

    private static IMessageDelivery DeliveryFailure(IMessageHub kernelHub, DeliveryFailure deliveryFailure)
    {
        kernelHub.Post(deliveryFailure, o => o.ResponseFor(deliveryFailure.Delivery));
        return deliveryFailure.Delivery.Failed(deliveryFailure.Message);
    }

    private void PublishEventToContext(KernelEvent @event)
    {
        var eventEnvelope = Microsoft.DotNet.Interactive.Connection.KernelEventEnvelope.Create(@event);
        var eventEnvelopeSerialized = Microsoft.DotNet.Interactive.Connection.KernelEventEnvelope.Serialize(eventEnvelope);
        foreach (var a in subscriptions)
            Hub.Post(new KernelEventEnvelope(eventEnvelopeSerialized), o => o.WithTarget(a));
    }

    private readonly ConcurrentDictionary<string, IObservable<object>> areas = new();
    public IMessageHub Hub { get; }
    public CompositeKernel Kernel { get; }
    private string LayoutAreaUrl { get; set; } = "https://localhost:65260/area/";

    protected CompositeKernel CreateKernel(string id)
    {
        Formatter.SetPreferredMimeTypesFor(typeof(TabularDataResource), HtmlFormatter.MimeType, CsvFormatter.MimeType);

        var ret = new CSharpKernel()
            .UseKernelHelpers()
            .UseValueSharing();

        ret.KernelInfo.Uri = new Uri(ret.KernelInfo.Uri.ToString().Replace("local", "mesh"));

        ret.AddAssemblyReferences([typeof(IMessageHub).Assembly.Location, typeof(KernelAddress).Assembly.Location, typeof(UiControl).Assembly.Location, typeof(DataExtensions).Assembly.Location]);


        Formatter.Register<IObservable<UiControl>>(formatter: RenderLayoutArea, HtmlFormatter.MimeType);
        Formatter.Register<UiControl>(formatter: (c,cc)=>RenderLayoutArea(Observable.Return(c), cc), HtmlFormatter.MimeType);

        var composite = new CompositeKernel("mesh");
        composite.KernelInfo.Uri = new(composite.KernelInfo.Uri.ToString().Replace("local", "mesh"));
        composite.Add(ret);
        return composite;
    }

    private bool RenderLayoutArea(IObservable<UiControl> control, FormatContext context)
    {
        var viewId = Guid.NewGuid().AsString();
        areas[viewId] = control;
        if (control is null)
        {
            var nullView = new PocketView("null");
            nullView.WriteTo(context);
            return true;
        }

        var view = $"<iframe src='{LayoutAreaUrl}{Hub.Address}/{viewId}'></iframe>";
        context.Writer.Write(view);
        return true;
        //PublishEventToContext(new DisplayedValueProduced(view, KernelInvocationContext.Current.Command));
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

    public IMessageDelivery HandleKernelCommandEnvelope(IMessageDelivery<KernelCommandEnvelope> request)
    {
        subscriptions.Add(request.Sender);
        var envelope = Microsoft.DotNet.Interactive.Connection.KernelCommandEnvelope.Deserialize(request.Message.Command);
        var command = envelope.Command;
        executionHub.InvokeAsync(ct => SubmitCommand(request, ct, command)); 
        return request.Processed();
    }
    public IMessageDelivery HandleKernelCommand(IMessageDelivery<SubmitCodeRequest> request)
    {
        subscriptions.Add(request.Sender);
        var command = new SubmitCode(request.Message.Code);
        executionHub.InvokeAsync(ct => SubmitCommand(request, ct, command));
        return request.Processed();
    }

    private async Task<IMessageDelivery> SubmitCommand(IMessageDelivery request, CancellationToken ct, KernelCommand command)
    {
        try
        {
            await Kernel.SendAsync(command, ct);
            return request.Processed();
        }
        catch(Exception e)
        {
            return DeliveryFailure(Hub, Messaging.DeliveryFailure.FromException(request, e));
        }
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

    private record KernelExecutionAddress;
}
