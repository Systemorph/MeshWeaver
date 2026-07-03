using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Kernel;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Kernel.Hub;

/// <summary>
/// Public-facing kernel hub. Lives on the Activity hub (or any host hub that
/// surfaces a kernel) and acts as a thin <em>forwarder</em>: when a
/// <see cref="SubmitCodeRequest"/> arrives, it is shipped to a hosted child hub
/// (the <see cref="KernelExecutor"/>) for actual script execution. The forwarder
/// itself does no Roslyn work and never blocks its action block on a script —
/// so the host hub stays free to (a) accept further requests and (b) process
/// the <c>DataChangeRequest</c>s the script emits via <c>Log.LogInformation</c>
/// in real time.
///
/// <para>The executor's address is an internal implementation detail. External
/// clients only ever address this hub; this hub forwards inward and routes
/// responses back out so subscribers see the kernel as a single addressable
/// surface.</para>
///
/// <para>Progress, stdout, return values, and errors flow through the host's
/// <c>ActivityLog</c> content. There is no separate event-envelope channel —
/// subscribers use the canonical
/// <c>workspace.GetRemoteStream&lt;MeshNode, MeshNodeReference&gt;</c> pattern.</para>
/// </summary>
public class KernelContainer(IServiceProvider serviceProvider)
{
    private readonly ILogger<KernelContainer> logger = serviceProvider.GetRequiredService<ILogger<KernelContainer>>();
    private IMessageHub? executorHub;
    private readonly object executorHubLock = new();

    private static readonly TimeSpan DisconnectTimeout = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Hub configuration for the standalone kernel-hub (full mesh-types + routes).
    /// Currently a thin alias of <see cref="ConfigureSubHub"/>; the standalone
    /// kernel address (<c>kernel/*</c>) was retired in favour of hosting the
    /// kernel inside the Activity MeshNode hub.
    /// </summary>
    public MessageHubConfiguration ConfigureHub(MessageHubConfiguration config)
        => Configure(config.AddMeshTypes());

    /// <summary>
    /// Hub configuration for kernel sub-hubs (e.g. the Activity hub, or hosted
    /// kernel hubs created directly by Blazor views). Lightweight: no AddMeshTypes /
    /// no routing — those are owned by the parent.
    /// </summary>
    public MessageHubConfiguration ConfigureSubHub(MessageHubConfiguration config) => Configure(config);

    private MessageHubConfiguration Configure(MessageHubConfiguration config)
    {
        // SubmitCodeRequest stays as the run trigger (today still posted directly
        // by CodeNodeType.HandleExecuteScript). CancelScriptRequest is INTERNAL —
        // not exposed to external clients. The canonical cancel API is
        // patching ActivityLog.RequestedStatus = Cancelled on the activity's own
        // content; the hub-content watcher below translates that to
        // CancelScriptRequest dispatched to the executor. See
        // Doc/Architecture/ActivityControlPlane.md.
        config.TypeRegistry.WithType(typeof(SubmitCodeRequest), nameof(SubmitCodeRequest));
        config.TypeRegistry.WithType(typeof(SubmitCodeResponse), nameof(SubmitCodeResponse));
        config.TypeRegistry.WithType(typeof(CancelScriptRequest), nameof(CancelScriptRequest));

        return config
            .AddLayout(layout =>
                // 🚨 The kernel's view domain is ONLY its dynamic script areas (the
                // submission-id areas its area dictionary owns) — NEVER areas owned by a
                // named renderer (Progress/Overview/Thumbnail on an Activity hub, …).
                // Every renderer matching an area first disposes and REMOVES the area's
                // existing content (RenderObservable → DisposeExistingAreas), so a
                // `_ => true` catch-all destroyed every named area on every hub hosting
                // the kernel: no Activity layout area ever rendered, and the code cell's
                // output pane (LayoutAreaControl(activityPath, Progress)) spun forever
                // for every viewer, runner included (2026-07-03 RCA; pinned by
                // CodeCellOutputCaptureTest). `layout` here is the definition built by
                // the lambdas applied BEFORE this one — node-type views register ahead
                // of AddKernelSubHubHandlers, so their named areas are all visible.
                layout.WithView(ctx => !layout.HasNamedRenderer(ctx.Area),
                    (host, ctx) => GetAreaStream(host.Hub.ServiceProvider)
                        .Select(a =>
                        {
                            var valueOrDefault = a.Value!.GetValueOrDefault(ctx.Area);
                            if (valueOrDefault is null)
                                return null;
                            var uiControlService = host.Hub.ServiceProvider.GetRequiredService<IUiControlService>();
                            return uiControlService.Convert(valueOrDefault);
                        })
                        // 🚨 Emit IMMEDIATELY — this catch-all matches EVERY area on the
                        // hub, and LayoutDefinition.Render composes the area's renderers
                        // SEQUENTIALLY (SelectMany): a matching renderer that stays silent
                        // dams the whole chain, so NO area on the hub ever reaches the
                        // store. On Activity hubs (which host the kernel AND named areas
                        // like Progress/Overview) that meant every layout-area
                        // subscription hung forever — the code cell's output pane spun
                        // eternally for every viewer, runner included (2026-07-03 RCA;
                        // pinned by CodeCellOutputCaptureTest). A null renders as a
                        // PASS-THROUGH (RenderArea: view == null ⇒ store unchanged), so
                        // the kernel view has "no opinion" until its own area dictionary
                        // actually owns the requested area; the script's control still
                        // flows the moment it lands in the dictionary.
                        .StartWith((UiControl?)null)
                )
            )
            .WithServices(services => services.AddScoped(CreateAreaStream))
            .WithInitialization(hub =>
            {
                DisposeOnTimeout(hub);
                StartActivityControlPlane(hub);
                // NOTE: deliberately NO InitializeActivityLifecycle here. The
                // kernel hub IS the executor — it activates in order to RUN the
                // script, so its own activity is legitimately Running the instant
                // it comes up. A first-emission "Running ⇒ Failed(interrupted)"
                // recovery would kill every freshly-started script. That wake-up
                // pattern is only safe when the owner hub is DISTINCT from the
                // executor (e.g. NodeType compile, where the owner re-requests
                // from its own state). See ActivityControlPlane.md.
            })
            .WithHandler<SubmitCodeRequest>(ForwardSubmitCodeRequest)
            .WithHandler<CancelScriptRequest>(ForwardCancelRequest);
    }

    /// <summary>
    /// Subscribe to this hub's own <see cref="MeshNodeReference"/> stream and
    /// translate <see cref="ActivityLog.RequestedStatus"/> patches into
    /// kernel-internal cancellations. The canonical cancel API is
    /// <c>workspace.UpdateMeshNode(curr =&gt; curr with { Content = ((ActivityLog)curr.Content!) with { RequestedStatus = Cancelled } })</c>;
    /// no external CancelScriptRequest needed. See
    /// <c>Doc/Architecture/ActivityControlPlane.md</c>.
    /// </summary>
    private void StartActivityControlPlane(IMessageHub hub)
    {
        // Reuses the shared WatchControlPlane helper from MeshWeaver.Mesh.Contract
        // so every NodeType that adopts the Activity Control Plane wires the
        // same Status / RequestedStatus loop. The kernel-specific bit is the
        // handler: a Cancelled request gets translated into CancelScriptRequest
        // dispatched to the executor sub-hub.
        var subscription = hub.WatchControlPlane(
            requested =>
            {
                if (requested == ActivityStatus.Cancelled)
                {
                    IMessageHub? executor;
                    lock (executorHubLock) { executor = executorHub; }
                    if (executor is not null && !executor.IsDisposing)
                        hub.Post(new CancelScriptRequest(), o => o.WithTarget(executor.Address));
                }
            },
            logger);
        hub.RegisterForDisposal(subscription);
    }

    private void DisposeOnTimeout(IMessageHub hub)
    {
        // 🚨 One-shot timer (period = InfiniteTimeSpan), reset on every message for
        // the idle-disconnect. A PERIODIC timer kept re-firing hub.Dispose() on an
        // already-disposed hub.
        //
        // 🚨 The callback must NOT capture `hub` strongly. The process-wide TimerQueue
        // is a GC strong-handle root: TimerQueue → Timer → callback closure → hub. For
        // a hub that completes activation we RegisterForDisposal the timer (disposed →
        // removed from the queue → no pin). But a kernel hub abandoned mid-activation
        // (RunLevel=1, Starting) is never disposed, so RegisterForDisposal never runs
        // and a `_ => hub.Dispose()` closure would pin the abandoned hub forever
        // (ClrMD chain: StrongHandle → Timer → KernelContainer.<>c__DisplayClass → hub
        // [RunLevel=1] — the MeshHub_IsCollected leak). A WeakReference + static callback
        // lets the GC reclaim an unreferenced hub: if it's already collectable the
        // disconnect is moot; if it's still alive the weak ref resolves and disposes it.
        var weakHub = new WeakReference<IMessageHub>(hub);
        var timer = new Timer(static state =>
        {
            if (((WeakReference<IMessageHub>)state!).TryGetTarget(out var h))
                h.Dispose();
        }, weakHub, DisconnectTimeout, Timeout.InfiniteTimeSpan);
        // Dispose the timer WITH the hub for the normal (activated → disposed) path so
        // the queue drops it promptly rather than waiting on a GC.
        hub.RegisterForDisposal((IDisposable)timer);
        hub.Register<object>(d =>
        {
            timer.Change(DisconnectTimeout, Timeout.InfiniteTimeSpan);
            return d;
        });
    }

    ISynchronizationStream<ImmutableDictionary<string, object>> GetAreaStream(IServiceProvider sp)
        => sp.GetRequiredService<ISynchronizationStream<ImmutableDictionary<string, object>>>();

    private ISynchronizationStream<ImmutableDictionary<string, object>> CreateAreaStream(IServiceProvider sp)
    {
        var hub = sp.GetRequiredService<IMessageHub>();
        return new SynchronizationStream<ImmutableDictionary<string, object>>(
            new(Guid.NewGuid().ToString("N"), hub.Address),
            hub,
            new AggregateWorkspaceReference(),
            new ReduceManager<ImmutableDictionary<string, object>>(hub),
            // Pure in-memory initial value — use the synchronous IObservable WithInitialization
            // overload (Observable.Return), not the Task.FromResult bridge.
            x => x.WithInitialization(_ => Observable.Return(ImmutableDictionary<string, object>.Empty))
        );
    }

    /// <summary>
    /// Lazily materialises the hosted executor child hub. Single instance per
    /// container so REPL state shared across submissions persists.
    ///
    /// <para>The executor's address is <c>kernelExec/{parentId}</c> — a plain
    /// address with no <c>Host</c> property. Hosted hubs are stored in the
    /// parent's <c>HostedHubsCollection</c> keyed by
    /// <see cref="AddressComparer"/> (Type+Id only), so a plain address is
    /// sufficient and matches the existing pattern. The parentId disambiguates
    /// across multiple activity hubs in the same process. The executor is a
    /// transient hub, NOT a persisted MeshNode — routing never goes through the
    /// MeshCatalog path.</para>
    /// </summary>
    private IMessageHub GetOrCreateExecutor(IMessageHub publicHub)
    {
        lock (executorHubLock)
        {
            if (executorHub is not null && !executorHub.IsDisposing) return executorHub;
            // Use the parent address as the executor's id so concurrent activity
            // hubs in the same process get distinct executor addresses
            // (HostedHubsCollection scopes by parent, but the child id still
            // needs to be unique to avoid clashes if a single parent ever spawns
            // more than one).
            var execAddress = new Address("kernelExec", publicHub.Address.Path);
            var executor = new KernelExecutor(publicHub);
            executorHub = publicHub.GetHostedHub(execAddress, executor.Configure, HostedHubCreation.Always)
                ?? throw new InvalidOperationException($"Failed to create kernel executor hub at {execAddress}");
            return executorHub;
        }
    }

    /// <summary>
    /// Forward <see cref="SubmitCodeRequest"/> to the hosted executor and bridge
    /// the response back to the original requester. Returns immediately so the
    /// public hub's action block stays free.
    /// </summary>
    private IMessageDelivery ForwardSubmitCodeRequest(IMessageHub hub, IMessageDelivery<SubmitCodeRequest> request)
    {
        var executor = GetOrCreateExecutor(hub);

        hub.Observe<SubmitCodeResponse>(request.Message, o => o.WithTarget(executor.Address))
            .Take(1)
            .Subscribe(
                resp => hub.Post(resp.Message, o => o.ResponseFor(request)),
                ex => hub.Post(
                    new SubmitCodeResponse(request.Message.Id, false) { Error = ex.Message },
                    o => o.ResponseFor(request)));

        return request.Processed();
    }

    /// <summary>
    /// Forward <see cref="CancelScriptRequest"/> to the executor (if it has been
    /// materialised — no point spawning the executor just to cancel nothing).
    /// Fire-and-forget: the executor's cancellation flips the script's
    /// <see cref="CancellationToken"/> and the script's own response (Failed)
    /// flows back through the SubmitCodeRequest forwarder above.
    /// </summary>
    private IMessageDelivery ForwardCancelRequest(IMessageHub hub, IMessageDelivery<CancelScriptRequest> request)
    {
        IMessageHub? executor;
        lock (executorHubLock) { executor = executorHub; }
        if (executor is not null && !executor.IsDisposing)
            hub.Post(request.Message, o => o.WithTarget(executor.Address));
        return request.Processed();
    }
}
