using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Kernel;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using MeshWeaver.NuGet;
using Microsoft.DotNet.Interactive;
using Microsoft.DotNet.Interactive.Commands;
using Microsoft.DotNet.Interactive.CSharp;
using Microsoft.DotNet.Interactive.Events;
using Microsoft.DotNet.Interactive.Formatting;
using Microsoft.DotNet.Interactive.Formatting.Csv;
using Microsoft.DotNet.Interactive.Formatting.TabularData;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Runtime.Loader;

namespace MeshWeaver.Kernel.Hub;

public class KernelContainer(IServiceProvider serviceProvider)
{

    private readonly HashSet<Address> subscriptions = new();
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
        // Note: Kernel types are registered at mesh level in KernelNodeType.AddKernel
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
            .WithServices(services => services
                .AddScoped(CreateKernelAsync)
                .AddScoped(CreateAreaStream)
            )
            .WithInitialization((hub, _) =>
            {
                DisposeOnTimeout(hub);
                // Delete the kernel session MeshNode when the hub is disposed
                hub.RegisterForDisposal((_, _) =>
                {
                    var meshService = hub.ServiceProvider.GetService<IMeshService>();
                    var nodePath = $"{hub.Address}";
                    meshService?.DeleteNode(nodePath).Subscribe(
                        _ => { },
                        ex => logger.LogWarning(ex, "Failed to delete kernel session node on dispose"));
                    return Task.CompletedTask;
                });
                return Task.CompletedTask;
            })
            .WithHandler<KernelCommandEnvelope>(HandleKernelCommandEnvelope)
            .WithHandler<SubmitCodeRequest>(HandleKernelCommand)
            .WithHandler<SubscribeKernelEventsRequest>((_, request) => HandleSubscribe(request))
            .WithHandler<UnsubscribeKernelEventsRequest>((_, request) => HandleUnsubscribe(request))
            .WithHandler<KernelEventEnvelope>((_, request) => HandleKernelEvent(request));

    }

    /// <summary>
    /// Hub configuration for kernel sub-hubs (local subhosts).
    /// Includes AddLayout for layout area subscriptions but omits AddMeshTypes/WithRoutes.
    ///
    /// <para>Registers kernel message types on this hub's own TypeRegistry. The
    /// hub may live in a different silo from the one where <c>KernelNodeType.AddKernel</c>
    /// ran (e.g. an Activity grain on the silo hosting <c>rbuergi</c> while
    /// <c>AddKernel</c> ran on the mesh hub of a different silo); type-registry
    /// inheritance does NOT span silos, so SubmitCodeResponse / KernelEventEnvelope
    /// would otherwise fail to deserialize on arrival ("type X is not registered
    /// in this hub's TypeRegistry"). Registering them locally is idempotent and
    /// safe even when inheritance does work.</para>
    /// </summary>
    public MessageHubConfiguration ConfigureSubHub(MessageHubConfiguration config)
    {
        config.TypeRegistry.WithType(typeof(SubmitCodeRequest), nameof(SubmitCodeRequest));
        config.TypeRegistry.WithType(typeof(SubmitCodeResponse), nameof(SubmitCodeResponse));
        config.TypeRegistry.WithType(typeof(KernelEventEnvelope), nameof(KernelEventEnvelope));
        config.TypeRegistry.WithType(typeof(KernelCommandEnvelope), nameof(KernelCommandEnvelope));
        config.TypeRegistry.WithType(typeof(SubscribeKernelEventsRequest), nameof(SubscribeKernelEventsRequest));
        config.TypeRegistry.WithType(typeof(UnsubscribeKernelEventsRequest), nameof(UnsubscribeKernelEventsRequest));

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

    private static readonly TimeSpan DisconnectTimeout = TimeSpan.FromMinutes(15);

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
            new ChangeItem<ImmutableDictionary<string, object>>(
                (x ?? ImmutableDictionary<string, object>.Empty).SetItem(viewId, view),
                hub.Address,
                areasStream.StreamId,
                ChangeType.Patch,
                hub.Version,
                [])
        , _ => { });
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
            ;

        ret.KernelInfo.Uri = new Uri(ret.KernelInfo.Uri.ToString().Replace("local", "mesh"));

        ret.AddAssemblyReferences([
            typeof(IMessageHub).Assembly.Location,
            typeof(Address).Assembly.Location,
            typeof(UiControl).Assembly.Location,
            typeof(DataExtensions).Assembly.Location,
            typeof(EntityStore).Assembly.Location, // MeshWeaver.Data.Contract - required for Layout types
            typeof(System.ComponentModel.DescriptionAttribute).Assembly.Location,
            typeof(System.ComponentModel.DataAnnotations.RequiredAttribute).Assembly.Location,
            typeof(System.Reactive.Linq.Observable).Assembly.Location, // System.Reactive - for reactive UI examples
            typeof(FluentIcons).Assembly.Location, // MeshWeaver.Application.Styles - for icon support
            typeof(ILogger).Assembly.Location, // Microsoft.Extensions.Logging.Abstractions - for scripts' Log global
            typeof(LoggerExtensions).Assembly.Location // Microsoft.Extensions.Logging.Abstractions - LogInformation/LogWarning extensions
        ]);

        var composite = new CompositeKernel("mesh");
        composite.KernelInfo.Uri = new(composite.KernelInfo.Uri.ToString().Replace("local", "mesh"));
        composite.Add(ret);
        await Task.WhenAll(composite.ChildKernels.OfType<CSharpKernel>()
            .Select(k => k.SetValueAsync(nameof(Mesh), hub, typeof(IMessageHub))));

        // Expose a `Log` global (ILogger). Scripts call `Log.LogInformation(...)` /
        // `Log.LogWarning(...)` etc. — the messages land on the current activity log
        // node streamed by the hub (once the ActivityLog plumbing in task #60 is
        // wired up). Until then, messages go to the standard logger infrastructure.
        // No more IProgress<string> Progress global — that was replaced by ActivityLog.
        var scriptLogger = hub.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("MeshWeaver.Kernel.Script");
        await Task.WhenAll(composite.ChildKernels.OfType<CSharpKernel>()
            .Select(k => k.SetValueAsync("Log", scriptLogger, typeof(ILogger))));

        // Add default using directives for interactive markdown
        // Note: We don't include "using static MeshWeaver.Layout.Controls;" because
        // Controls.DateTime() conflicts with System.DateTime
        var defaultUsings = @"
using System;
using System.Linq;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using Microsoft.Extensions.Logging;
using MeshWeaver.Application.Styles;
using MeshWeaver.Layout;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Messaging;
";
        await ret.SendAsync(new SubmitCode(defaultUsings));
        composite.KernelEvents.Subscribe(e => PublishEventToContext(hub, e));

        hub.RegisterForDisposal(composite);
        return composite;
    }

    private static readonly HashSet<string> _probingDirs = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _probingDirLock = new();
    private static bool _probingResolverInstalled;

    private void InstallRuntimeProbe(IEnumerable<string> dirs)
    {
        lock (_probingDirLock)
        {
            foreach (var dir in dirs) _probingDirs.Add(dir);
            if (_probingResolverInstalled) return;
            _probingResolverInstalled = true;

            AssemblyLoadContext.Default.Resolving += (ctx, name) =>
            {
                var dllName = name.Name + ".dll";
                lock (_probingDirLock)
                {
                    foreach (var d in _probingDirs)
                    {
                        var candidate = Path.Combine(d, dllName);
                        if (File.Exists(candidate))
                        {
                            try { return ctx.LoadFromAssemblyPath(candidate); }
                            catch { /* try next */ }
                        }
                    }
                }
                return null;
            };
        }
    }

    private async Task<(KernelCommand command, string? resolvedCode)> PreprocessSubmitCodeAsync(
        CompositeKernel kernel, SubmitCode submit, CancellationToken ct)
    {
        var (cleaned, refs) = NuGetDirectiveParser.Extract(submit.Code);
        if (refs.Length == 0)
            return (submit, null);

        var resolver = serviceProvider.GetService<INuGetAssemblyResolver>();
        if (resolver is null)
        {
            logger.LogWarning("INuGetAssemblyResolver not registered; #r \"nuget:...\" directives ignored.");
            return (submit, null);
        }

        try
        {
            var resolved = await resolver.ResolveAsync(refs, targetFramework: null, ct);
            var csharp = kernel.ChildKernels.OfType<CSharpKernel>().FirstOrDefault();
            csharp?.AddAssemblyReferences(resolved.AssemblyPaths);
            InstallRuntimeProbe(resolved.ProbingDirectories);
            logger.LogInformation("Resolved {Count} NuGet package(s) for interactive cell.", refs.Length);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "NuGet restore failed for interactive cell.");
            throw;
        }

        var replaced = new SubmitCode(cleaned, targetKernelName: submit.TargetKernelName);
        foreach (var (k, v) in submit.Parameters)
            replaced.Parameters[k] = v;
        return (replaced, cleaned);
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

    /// <summary>
    /// Sync handler — composes via <c>Observable.FromAsync</c> + <c>Subscribe</c>; no <c>await</c>.
    /// </summary>
    public IMessageDelivery HandleKernelCommandEnvelope(IMessageHub hub, IMessageDelivery<KernelCommandEnvelope> request)
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

        Observable.FromAsync(ct => SubmitCommandAsync(hub, ct, command))
            .Subscribe(
                _ => { },
                ex => logger.LogError(ex, "KernelCommandEnvelope dispatch failed"));

        return request.Processed();
    }

    /// <summary>
    /// Async handler — awaits the kernel work so consecutive SubmitCodeRequests on
    /// the same kernel hub are processed serially. The hub's action block blocks
    /// while the kernel runs, which is the correct serialization point for shared
    /// kernel state: submission #2 must not start compiling before submission #1
    /// has finished defining its symbols, otherwise variables from #1 are not in
    /// scope when #2 is parsed (the InteractiveMarkdownExecutionTest
    /// MultipleBlocks_ShareKernelState_ViaSharedAddress regression).
    ///
    /// <para>
    /// Awaiting <see cref="SubmitCommandAsync"/> does NOT post back through this
    /// hub — it's a pure call into Microsoft.DotNet.Interactive — so blocking
    /// the action block here can't deadlock against itself. This is one of the
    /// sanctioned async-handler shapes (long-running CPU/IO work that doesn't
    /// re-enter the hub during execution; see Doc/Architecture/AsynchronousCalls.md
    /// "Blocking Execution (AI Streaming)").
    /// </para>
    /// </summary>
    public async Task<IMessageDelivery> HandleKernelCommand(IMessageHub hub, IMessageDelivery<SubmitCodeRequest> request, CancellationToken ct)
    {
        subscriptions.Add(request.Sender);
        var submissionId = request.Message.Id;
        var command = new SubmitCode(request.Message.Code)
        {
            Parameters = { [ViewId] = submissionId }
        };
        if (!string.IsNullOrEmpty(request.Message.IFrameUrl))
            command.Parameters[IframeUrl] = request.Message.IFrameUrl;

        // If the caller passed an ActivityLogPath, swap the script's `Log` global
        // to a logger that appends to that node. Messages stream through the node's
        // MeshNodeReference for subscribers watching the run.
        ActivityLogLogger? activityLogger = null;
        if (!string.IsNullOrEmpty(request.Message.ActivityLogPath))
        {
            activityLogger = new ActivityLogLogger(hub, request.Message.ActivityLogPath!);
            var kernelInstance = await hub.ServiceProvider.GetRequiredService<Task<CompositeKernel>>();
            await Task.WhenAll(kernelInstance.ChildKernels.OfType<CSharpKernel>()
                .Select(k => k.SetValueAsync("Log", activityLogger, typeof(ILogger))));
        }

        string? error = null;
        try
        {
            await SubmitCommandAsync(hub, ct, command);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            activityLogger?.LogError(ex, "Script dispatch failed");
        }

        // Finalize the activity log: flush pending messages and flip the terminal
        // status on the node so subscribers see the run as Succeeded / Failed.
        activityLogger?.Complete(error is null ? ActivityStatus.Succeeded : ActivityStatus.Failed);

        hub.Post(new SubmitCodeResponse(submissionId, error is null) { Error = error },
            o => o.ResponseFor(request));

        return request.Processed();
    }

    // File-I/O / kernel-SDK kernel — kept as async Task internally (Microsoft.DotNet.Interactive
    // requires Task-based kernel.SendAsync). Surfaced through Observable.FromAsync at the
    // single hub-handler boundary above.
    private async Task SubmitCommandAsync(IMessageHub hub, CancellationToken ct, KernelCommand command)
    {
        var kernel = await hub.ServiceProvider.GetRequiredService<Task<CompositeKernel>>();
        if (command is SubmitCode submit)
        {
            (command, _) = await PreprocessSubmitCodeAsync(kernel, submit, ct);
        }
        await kernel.SendAsync(command, ct);
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
