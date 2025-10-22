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

public class KernelContainer(IServiceProvider serviceProvider)
{

    private readonly HashSet<Address> subscriptions = new();
    private readonly IMeshCatalog meshCatalog = serviceProvider.GetRequiredService<IMeshCatalog>();
    private readonly ILogger<KernelContainer> logger = serviceProvider.GetRequiredService<ILogger<KernelContainer>>();

    /// <summary>
    /// When the kernel does not receive messages in a given timeout,
    /// it will dispose itself.
    /// </summary>
    private void DisposeOnTimeout(IMessageHub hub)
    {
        var timer = new Timer(_ => hub.Dispose(), this, DisconnectTimeout, DisconnectTimeout);
        hub.Register<object>(d =>
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
                    (host, ctx) => GetAreaStream(host.Hub.ServiceProvider)
                        .Select(a =>
                        {
                            var valueOrDefault = a.Value!.GetValueOrDefault(ctx.Area);
                            if (valueOrDefault is null)
                                return null;
                            var uiControlService = host.Hub.ServiceProvider.GetRequiredService<IUiControlService>();
                            return uiControlService.Convert(valueOrDefault);
                        })
                )
            )
            .AddMeshTypes()
            .WithRoutes(routes => routes.WithHandler((d, ct) => RouteToSubHubs(routes.Hub, d, ct)))
            .WithServices(services => services
                .AddScoped(CreateKernelAsync)
                .AddScoped(CreateAreaStream)
            )
            .WithInitialization((hub, _) =>
            {
                DisposeOnTimeout(hub);
                return Task.CompletedTask;
            })
            .WithHandler<KernelCommandEnvelope>(HandleKernelCommandEnvelope)
            .WithHandler<SubmitCodeRequest>(HandleKernelCommand)
            .WithHandler<SubscribeKernelEventsRequest>((_, request) => HandleSubscribe(request))
            .WithHandler<UnsubscribeKernelEventsRequest>((_, request) => HandleUnsubscribe(request))
            .WithHandler<KernelEventEnvelope>((_, request) => HandleKernelEvent(request));

    }

    ISynchronizationStream<ImmutableDictionary<string, object>> GetAreaStream(IServiceProvider sp)
        => sp.GetRequiredService<ISynchronizationStream<ImmutableDictionary<string, object>>>();
    private ISynchronizationStream<ImmutableDictionary<string, object>> CreateAreaStream(IServiceProvider sp)
    {
        var hub = sp.GetRequiredService<IMessageHub>();
        return new SynchronizationStream<ImmutableDictionary<string, object>>(
            new(Guid.NewGuid().AsString(), hub.Address),
            hub,
            new AggregateWorkspaceReference(),
            new ReduceManager<ImmutableDictionary<string, object>>(hub),
            x => x
                .WithInitialization((_, _) => Task.FromResult(ImmutableDictionary<string, object>.Empty))
        );

    }

    private async Task<IMessageDelivery> RouteToSubHubs(IMessageHub kernelHub, IMessageDelivery request, CancellationToken cancellationToken)
    {
        if (request.Target is not HostedAddress hosted || !kernelHub.Address.Equals(hosted.Host))
            return request;
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

            var kernel = await kernelHub.ServiceProvider.GetRequiredService<Task<CompositeKernel>>();
            var result = await kernel.SendAsync(new SubmitCode(meshNode.StartupScript), cancellationToken);
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
            logger.LogInformation("Trying to invoke kernel command on disposed kernel: {Address}: {Command}", kernelHub.Address, request);
            kernelHub.Dispose();
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


    private static readonly TimeSpan DisconnectTimeout = TimeSpan.FromMinutes(15);


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

    private void PublishEventToContext(IMessageHub hub, KernelEvent @event)
    {
        var isNotebookKernel = IsNotebookKernel(@event);
        if (isNotebookKernel)
            HandleNotebookEvent(hub, @event);
        else
            HandleInteractiveMarkdownEvent(hub, @event);
    }

    private void HandleInteractiveMarkdownEvent(IMessageHub hub, KernelEvent @event)
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
            UpdateView(hub, viewId, view);
    }

    private string? GetViewId(KernelCommand command)
    {
        if (command is not SubmitCode submit)
            return null;

        if (submit.Parameters.TryGetValue(ViewId, out var ret))
            return ret;

        return command.Parent != null ? GetViewId(command.Parent) : null;
    }

    private void UpdateView(IMessageHub hub, string viewId, object view)
    {
        var areasStream = GetAreaStream(hub.ServiceProvider);
        areasStream.Update(x =>
            (
                    areasStream.Current
                    ?? new ChangeItem<ImmutableDictionary<string, object>>(
                        ImmutableDictionary<string, object>.Empty,
                        hub.Address,
                        areasStream.StreamId,
                        ChangeType.Full,
                        0,
                        []))
                with
            {
                Value = (x ?? ImmutableDictionary<string, object>.Empty).SetItem(viewId, view)
            }
        , _ => Task.CompletedTask);
    }

    private void HandleNotebookEvent(IMessageHub hub, KernelEvent @event)
    {
        if (@event is ReturnValueProduced retProduced
            && @event.Command is SubmitCode submit
            && submit.Parameters.TryGetValue(ViewId, out var viewId)
           )
        {
            UpdateView(hub, viewId, retProduced.Value);
            if (submit.Parameters.TryGetValue(IframeUrl, out var iframeUrl))
                @event = new ReturnValueProduced(retProduced.Value, retProduced.Command, [new("text/html", FormatControl(hub, retProduced.Value as UiControl, iframeUrl, viewId))]);
        }
        var eventEnvelope = Microsoft.DotNet.Interactive.Connection.KernelEventEnvelope.Create(@event);
        var eventEnvelopeSerialized = Microsoft.DotNet.Interactive.Connection.KernelEventEnvelope.Serialize(eventEnvelope);

        foreach (var a in subscriptions)
            hub.Post(new KernelEventEnvelope(eventEnvelopeSerialized), o => o.WithTarget(a));
    }

    private static bool IsNotebookKernel(KernelEvent @event)
    {
        // TODO V10: may need to find a better differentiator for notebook kernel. (26.01.2025, Roland Bürgi)
        return @event.Command is SubmitCode submit3 && submit3.Parameters.ContainsKey(IframeUrl);
    }


    protected async Task<CompositeKernel> CreateKernelAsync(IServiceProvider sp)
    {
        Formatter.SetPreferredMimeTypesFor(typeof(TabularDataResource), HtmlFormatter.MimeType, CsvFormatter.MimeType);
        var hub = sp.GetRequiredService<IMessageHub>();

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
        await Task.WhenAll(composite.ChildKernels.OfType<CSharpKernel>()
            .Select(k => k.SetValueAsync(nameof(Mesh), hub, typeof(IMessageHub))));
        composite.KernelEvents.Subscribe(e => PublishEventToContext(hub, e));

        hub.RegisterForDisposal(composite);
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
    private string FormatControl(IMessageHub hub, UiControl? control, string iframeUrl, string viewId)
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

        var view = $@"<iframe id='{viewId}' src='{iframeUrl}/{hub.Address}/{viewId}' style='{style}'></iframe>";
        return view;
    }


    private IMessageDelivery HandleKernelEvent(IMessageDelivery<KernelEventEnvelope> @event)
    {
        // TODO V10: here we need to see what to do. cancellation will come through here. (12.12.2024, Roland Bürgi)
        return @event.Processed();
    }

    private const string ViewId = "viewId";
    private const string IframeUrl = "iframeUrl";
    public async Task<IMessageDelivery> HandleKernelCommandEnvelope(IMessageHub hub, IMessageDelivery<KernelCommandEnvelope> request, CancellationToken ct)
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
        await SubmitCommand(hub, request, ct, command);
        return request.Processed();
    }
    public async Task<IMessageDelivery> HandleKernelCommand(IMessageHub hub, IMessageDelivery<SubmitCodeRequest> request, CancellationToken ct)
    {
        subscriptions.Add(request.Sender);
        var command = new SubmitCode(request.Message.Code)
        {
            Parameters = { [ViewId] = request.Message.Id }
        };
        if (!string.IsNullOrEmpty(request.Message.IFrameUrl))
            command.Parameters[IframeUrl] = request.Message.IFrameUrl;
        return await SubmitCommand(hub, request, ct, command);
    }

    private async Task<IMessageDelivery> SubmitCommand(IMessageHub hub, IMessageDelivery request, CancellationToken ct, KernelCommand command)
    {
        var kernel = await hub.ServiceProvider.GetRequiredService<Task<CompositeKernel>>();
        var ret = await kernel.SendAsync(command, ct);
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


}
