using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
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

public class KernelContainer(IServiceProvider serviceProvider) : IDisposable
{

    private readonly HashSet<Address> subscriptions = new();
    private readonly IMeshCatalog meshCatalog = serviceProvider.GetRequiredService<IMeshCatalog>();
    private readonly ILogger<KernelContainer> logger = serviceProvider.GetRequiredService<ILogger<KernelContainer>>();
    private IUiControlService uiControlService = null!;

    private void Initialize(IMessageHub hub)
    {
        Hub = hub;
        uiControlService = hub.ServiceProvider.GetRequiredService<IUiControlService>();
        Kernel = CreateKernel();
        Kernel.KernelEvents.Subscribe(PublishEventToContext);
        AreasStream =
            new SynchronizationStream<ImmutableDictionary<string, object>>(
                new(Guid.NewGuid().AsString(), Hub.Address),
                Hub,
                new AggregateWorkspaceReference(),
                new ReduceManager<ImmutableDictionary<string, object>>(Hub),
                x => x
                    .WithInitialization((_, _) => Task.FromResult(ImmutableDictionary<string, object>.Empty))
            );

        Hub.RegisterForDisposal(this);
        DisposeOnTimeout();
    }

    private ISynchronizationStream<ImmutableDictionary<string, object>> AreasStream { get; set; } = null!;


    /// <summary>
    /// When the kernel does not receive messages in a given timeout,
    /// it will dispose itself.
    /// </summary>
    private void DisposeOnTimeout()
    {
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
                layout.WithView(_ => true,
                    (_, ctx) => AreasStream
                        .Select(a =>
                        {
                            var valueOrDefault = a.Value!.GetValueOrDefault(ctx.Area);
                            if (valueOrDefault is null)
                                return null;
                            return uiControlService.Convert(valueOrDefault);
                        })
                )
            )
            .AddMeshTypes()
            .WithRoutes(routes => routes.WithHandler((d, ct) => Task.FromResult(RaiseRouteRequest(routes.Hub, d))))
            .WithInitialization((hub, _) =>
            {
                Initialize(hub);
                return Task.WhenAll(Kernel.ChildKernels.OfType<CSharpKernel>()
                    .Select(k => k.SetValueAsync(nameof(Mesh), Hub, typeof(IMessageHub))));
            })
            .WithHandler<KernelCommandEnvelope>((_, request, ct) => HandleKernelCommandEnvelope(request, ct))
            .WithHandler<SubmitCodeRequest>((_, request, ct) => HandleKernelCommand(request, ct))
            .WithHandler<SubscribeKernelEventsRequest>((_, request) => HandleSubscribe(request))
            .WithHandler<UnsubscribeKernelEventsRequest>((_, request) => HandleUnsubscribe(request))
            .WithHandler<KernelEventEnvelope>((_, request) => HandleKernelEvent(request))
            .WithHandler<RoutingRequest>(RouteToSubHubs);

    }

    private IMessageDelivery RaiseRouteRequest(IMessageHub kernelHub, IMessageDelivery request)
    {
        if (request.Target is not HostedAddress hosted || !kernelHub.Address.Equals(hosted.Host))
            return request;
        kernelHub.Post(new RoutingRequest(request));
        return request.Forwarded();
    }


    private static readonly TimeSpan DisconnectTimeout = TimeSpan.FromMinutes(15);

    private async Task<IMessageDelivery> RouteToSubHubs(IMessageHub kernelHub, IMessageDelivery<RoutingRequest> routeRequest, CancellationToken cancellationToken)
    {
        var request = routeRequest.Message.OriginalRequest;
        var hosted = (HostedAddress)request.Target!;
        try
        {
            var hub = kernelHub.GetHostedHub(hosted.Address, HostedHubCreation.Never);
            if (hub is not null)
            {
                hub.DeliverMessage(request);
                return request.Processed();
            }

            var meshNode = await meshCatalog.GetNodeAsync(hosted.Address);
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
        return request.Failed(deliveryFailure.Message ?? string.Empty);
    }

    private void PublishEventToContext(KernelEvent @event)
    {
        var isNotebookKernel = IsNotebookKernel(@event);
        if (isNotebookKernel)
            HandleNotebookEvent(@event);
        else
            HandleInteractiveMarkdownEvent(@event);
    }

    private void HandleInteractiveMarkdownEvent(KernelEvent @event)
    {
        var viewId = GetViewId(@event.Command);

        if (viewId is null)
            return;

        var view = @event switch
        {
            ReturnValueProduced ret => ret.Value,
            StandardOutputValueProduced stdOut => stdOut.Value,
            CommandFailed failed => Controls.Markdown($"**Execution failed**:\n{failed.Message}"),
            _ => null,
        };
        if (view is not null)
            UpdateView(viewId, view);
    }

    private string? GetViewId(KernelCommand command)
    {
        if (command is not SubmitCode submit)
            return null;

        if (submit.Parameters.TryGetValue(ViewId, out var ret))
            return ret;

        return command.Parent != null ? GetViewId(command.Parent) : null;
    }

    private void UpdateView(string viewId, object view)
    {
        AreasStream.Update(x =>
            (
                    AreasStream.Current
                    ?? new ChangeItem<ImmutableDictionary<string, object>>(
                        ImmutableDictionary<string, object>.Empty,
                        Hub.Address,
                        AreasStream.StreamId,
                        ChangeType.Full,
                        0,
                        []))
                with
            {
                Value = (x ?? ImmutableDictionary<string, object>.Empty).SetItem(viewId, view)
            }
        , _ => Task.CompletedTask);
    }

    private void HandleNotebookEvent(KernelEvent @event)
    {
        if (@event is ReturnValueProduced retProduced
            && @event.Command is SubmitCode submit
            && submit.Parameters.TryGetValue(ViewId, out var viewId)
           )
        {
            UpdateView(viewId, retProduced.Value);
            if (submit.Parameters.TryGetValue(IframeUrl, out var iframeUrl))
                @event = new ReturnValueProduced(retProduced.Value, retProduced.Command, [new("text/html", FormatControl(retProduced.Value as UiControl, iframeUrl, viewId))]);
        }
        var eventEnvelope = Microsoft.DotNet.Interactive.Connection.KernelEventEnvelope.Create(@event);
        var eventEnvelopeSerialized = Microsoft.DotNet.Interactive.Connection.KernelEventEnvelope.Serialize(eventEnvelope);

        foreach (var a in subscriptions)
            Hub.Post(new KernelEventEnvelope(eventEnvelopeSerialized), o => o.WithTarget(a));
    }

    private static bool IsNotebookKernel(KernelEvent @event)
    {
        // TODO V10: may need to find a better differentiator for notebook kernel. (26.01.2025, Roland Bürgi)
        return @event.Command is SubmitCode submit3 && submit3.Parameters.ContainsKey(IframeUrl);
    }

    private IMessageHub Hub { get; set; } = null!;
    public CompositeKernel Kernel { get; set; } = null!;

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

        var composite = new CompositeKernel("mesh")
            .UseNugetDirective(OnResolve);
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
        var assemblies = packages.SelectMany(p => p.AssemblyPaths).Distinct().ToArray();
        try
        {
            // Use the correct method to add assembly references
            kernel.AddAssemblyReferences(assemblies);
            logger.LogInformation("Added assembly reference: {Assembly}", string.Join(',', assemblies));
        }
        catch (Exception ex)
        {
            logger.LogWarning("Failed to add assembly reference {Assembly}: {Message}",
                string.Join(',', assemblies), ex.Message);
        }

        return Task.CompletedTask;
    }
    private string FormatControl(UiControl? control, string iframeUrl, string viewId)
    {

        var style = control?.Style?.ToString() ?? string.Empty;
        if (!style.Contains("display"))
            style += "display: block; ";
        if (!style.Contains("margin"))
            style += "margin: 0 auto; ";
        if (!style.Contains("width"))
            style += "width: 100%; ";
        if (!style.Contains("height"))
            style += "height: 500px; ";

        var view = $@"<iframe id='{viewId}' src='{iframeUrl}/{Hub.Address}/{viewId}' style='{style}'></iframe>";
        return view;
    }


    private IMessageDelivery HandleKernelEvent(IMessageDelivery<KernelEventEnvelope> @event)
    {
        // TODO V10: here we need to see what to do. cancellation will come through here. (12.12.2024, Roland Bürgi)
        return @event.Processed();
    }

    private const string ViewId = "viewId";
    private const string IframeUrl = "iframeUrl";
    public async Task<IMessageDelivery> HandleKernelCommandEnvelope(IMessageDelivery<KernelCommandEnvelope> request, CancellationToken ct)
    {
        subscriptions.Add(request.Sender);
        var envelope = Microsoft.DotNet.Interactive.Connection.KernelCommandEnvelope.Deserialize(request.Message.Command);
        var command = envelope.Command;
        if (command is SubmitCode submit)
        {
            submit.Parameters[ViewId] = request.Message.ViewId;
            if (!string.IsNullOrEmpty(request.Message.IFrameUrl))
                submit.Parameters[IframeUrl] = request.Message.IFrameUrl;
        }
        await SubmitCommand(request, ct, command);
        return request.Processed();
    }
    public async Task<IMessageDelivery> HandleKernelCommand(IMessageDelivery<SubmitCodeRequest> request, CancellationToken ct)
    {
        subscriptions.Add(request.Sender);
        var command = new SubmitCode(request.Message.Code)
        {
            Parameters = { [ViewId] = request.Message.Id }
        };
        if (!string.IsNullOrEmpty(request.Message.IFrameUrl))
            command.Parameters[IframeUrl] = request.Message.IFrameUrl;
        await SubmitCommand(request, ct, command);
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

}
