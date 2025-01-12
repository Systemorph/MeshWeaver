using System.Collections.Concurrent;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
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
using Microsoft.DotNet.Interactive.PackageManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Kernel.Hub;

public class KernelContainer : IDisposable
{

    private readonly HashSet<Address> subscriptions = new();
    private IMeshCatalog meshCatalog;
    private IMessageHub executionHub;
    private ILogger<KernelContainer> logger;

    private void Initialize(IMessageHub hub)
    {
        this.meshCatalog = hub.ServiceProvider.GetRequiredService<IMeshCatalog>();
        this.logger = hub.ServiceProvider.GetRequiredService<ILogger<KernelContainer>>();

        Hub = hub;
        Kernel = CreateKernel();
        Kernel.KernelEvents.Subscribe(PublishEventToContext);

        executionHub = Hub.ServiceProvider.CreateMessageHub(new KernelExecutionAddress());
        Hub.RegisterForDisposal(this);
        var timer = new Timer(_ => Dispose(), this, DisconnectTimeout, DisconnectTimeout);
        Hub.Register<object>(d =>
        {
            timer.Change(DisconnectTimeout, DisconnectTimeout);
            return d;
        });
    }

    public MessageHubConfiguration ConfigureHub(MessageHubConfiguration config)
    {
        return config
            .AddLayout(layout =>
                layout.WithView(ctx =>
                        areas.ContainsKey(ctx.Area),
                    (_, ctx) => areas.GetValueOrDefault(ctx.Area)
                )
            )
            .WithRoutes(routes => routes.WithHandler((d, ct) => RouteToSubHubs(routes.Hub, d, ct)))
            .WithInitialization((hub, _) =>
            {
                Initialize(hub);
                return Task.WhenAll(Kernel.ChildKernels.OfType<CSharpKernel>()
                    .Select(k => k.SetValueAsync(nameof(Mesh), Hub, typeof(IMessageHub))));
            })
            .WithHandler<KernelCommandEnvelope>((_, request) => HandleKernelCommandEnvelope(request))
            .WithHandler<SubmitCodeRequest>((_, request) => HandleKernelCommand(request))
            .WithHandler<SubscribeKernelEventsRequest>((_, request) => HandleSubscribe(request))
            .WithHandler<UnsubscribeKernelEventsRequest>((_, request) => HandleUnsubscribe(request))
            .WithHandler<KernelEventEnvelope>((_, request) => HandleKernelEvent(request));
    }


    private static readonly TimeSpan DisconnectTimeout = TimeSpan.FromMinutes(15);

    private async Task<IMessageDelivery> RouteToSubHubs(IMessageHub kernelHub, IMessageDelivery request, CancellationToken cancellationToken)
    {
        try
        {
            if (request.Target is not HostedAddress hosted || !kernelHub.Address.Equals(hosted.Host))
                return request;

            var hub = kernelHub.GetHostedHub(hosted.Address, HostedHubCreation.Never);
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
                    kernelHub, request,
                    new DeliveryFailure(request) { ErrorType = ErrorType.StartupScriptFailed, Message = message });
            }


            hub = kernelHub.GetHostedHub(hosted.Address, HostedHubCreation.Never);
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
            Hub.Dispose();
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
        return DeliveryFailure(kernelHub, request, deliveryFailure);
    }

    private static IMessageDelivery DeliveryFailure(IMessageHub kernelHub, IMessageDelivery request, DeliveryFailure deliveryFailure)
    {
        kernelHub.Post(deliveryFailure, o => o.ResponseFor(request));
        return request.Failed(deliveryFailure.Message);
    }

    private void PublishEventToContext(KernelEvent @event)
    {
        var eventEnvelope = Microsoft.DotNet.Interactive.Connection.KernelEventEnvelope.Create(@event);
        var eventEnvelopeSerialized = Microsoft.DotNet.Interactive.Connection.KernelEventEnvelope.Serialize(eventEnvelope);
        foreach (var a in subscriptions)
            Hub.Post(new KernelEventEnvelope(eventEnvelopeSerialized), o => o.WithTarget(a));
    }

    private readonly ConcurrentDictionary<string, UiControl> areas = new();
    private IMessageHub Hub { get; set; }
    public CompositeKernel Kernel { get; set; }
    private string LayoutAreaUrl { get; set; } = "https://localhost:65260/area/";

    protected CompositeKernel CreateKernel()
    {
        Formatter.SetPreferredMimeTypesFor(typeof(TabularDataResource), HtmlFormatter.MimeType, CsvFormatter.MimeType);

        var ret = new CSharpKernel()
            .UseKernelHelpers()
            .UseValueSharing()
            .UseNugetDirective(OnResolve)
            ;

        ret.KernelInfo.Uri = new Uri(ret.KernelInfo.Uri.ToString().Replace("local", "mesh"));

        ret.AddAssemblyReferences([typeof(IMessageHub).Assembly.Location, typeof(KernelAddress).Assembly.Location, typeof(UiControl).Assembly.Location, typeof(DataExtensions).Assembly.Location]);


        Formatter.Register<UiControl>(
            formatter: FormatControl, HtmlFormatter.MimeType);
        Formatter.Register<IRenderableObject>(
            formatter: (obj,ctx) => FormatControl(obj.ToControl(), ctx), HtmlFormatter.MimeType);

        var composite = new CompositeKernel("mesh").UseNugetDirective(OnResolve);
        composite.KernelInfo.Uri = new(composite.KernelInfo.Uri.ToString().Replace("local", "mesh"));
        composite.Add(ret);
        return composite;
    }

    private Task OnResolve(CompositeKernel arg1, IReadOnlyList<ResolvedPackageReference> arg2)
    {
        return Task.CompletedTask;
    }

    private Task OnResolve(CSharpKernel kernel, IReadOnlyList<ResolvedPackageReference> packages)
    {
        return Task.CompletedTask;
    }

    private bool FormatControl(UiControl control, FormatContext context)
    {
        var viewId = Guid.NewGuid().AsString();
        areas[viewId] = control;
        if (control is null)
        {
            var nullView = new PocketView("null");
            nullView.WriteTo(context);
            return true;
        }

        var style = control.Style?.ToString() ?? string.Empty;
        if (!style.Contains("display"))
            style += "display: block; ";
        if (!style.Contains("margin"))
            style += "margin: 0 auto; ";
        if (!style.Contains("width"))
            style += "width: 100%; ";
        if (!style.Contains("height"))
            style += "height: 500px; ";

        var view = $@"<iframe id='{viewId}' src='{LayoutAreaUrl}{Hub.Address}/{viewId}' style='{style}'></iframe>";
        context.Writer.Write(view);
        return true;
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
        Hub.Dispose();
    }

    private record KernelExecutionAddress() : Address("ke", Guid.NewGuid().AsString());
}
