using System.Collections.Immutable;
using System.Reactive;
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

    /// <summary>
    /// The idle-disconnect's shared state — set by <see cref="DisposeOnTimeout"/>, read by the
    /// submission forwarder. Holds the hub only WEAKLY (see <see cref="DisposeOnTimeout"/>).
    /// </summary>
    private IdleState? idle;

    /// <summary>
    /// The idle window after which a kernel-hosting hub disposes itself (15 minutes by default) and
    /// the clock it runs on. A host (or a test that needs to observe the reclamation inside its
    /// budget) can override either by registering a <see cref="KernelHubOptions"/> singleton.
    /// 🚨 NOT a memory tuning knob — see <see cref="KernelHubOptions.IdleDisconnectTimeout"/>.
    /// </summary>
    private readonly KernelHubOptions options =
        serviceProvider.GetService<KernelHubOptions>() ?? new KernelHubOptions();

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
                // NOTE: deliberately NO InitializeActivityLifecycle here. The
                // kernel hub IS the executor — it activates in order to RUN the
                // script, so its own activity is legitimately Running the instant
                // it comes up. A first-emission "Running ⇒ Failed(interrupted)"
                // recovery would kill every freshly-started script. That wake-up
                // pattern is only safe when the owner hub is DISTINCT from the
                // executor (e.g. NodeType compile, where the owner re-requests
                // from its own state). See ActivityControlPlane.md.
            })
            // 🚨 The OBSERVABLE overload, deliberately (#1868). StartActivityControlPlane reaches
            // hub construction — WatchControlPlane → SubscribeWithReEstablish → AcquireStream →
            // Workspace.GetStream → SynchronizationStream..ctor, whose constructor ALWAYS calls
            // GetHostedHub(sync/{clientId}, HostedHubCreation.Always). Run as a SYNCHRONOUS buildup
            // action it did that from inside MessageHubConfiguration.Build, so a disposal racing
            // this hub's creation raced a TREE of constructions rather than one frame. The
            // observable overload runs on the InitializeHubRequest turn, after Build returns and
            // still before the Initialize gate opens, so nothing observable to a message changes.
            .WithInitialization(hub => Observable.Defer(() =>
            {
                StartActivityControlPlane(hub);
                return Observable.Return(Unit.Default);
            }))
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
        //
        // 🚨 IDLE MEANS "NO MESSAGE *AND* NOTHING EXECUTING" (MeshWeaver#4422). The timer is
        // re-armed by messages DELIVERED to this hub, and a running script delivers none: it
        // executes on the hosted executor, its mesh reads answer elsewhere, and its log lines
        // leave as outgoing writes. So a script still working 15 min after the last inbound
        // message had this hub — and its executor with it — disposed mid-run, silently: no
        // SubmitCodeResponse, no terminal status (an approved OperationRequest died that way on
        // memex, 2026-09-15). While a forwarded submission is in flight the callback re-arms
        // instead of disposing; the finished-activity reclamation (#1324/#1435) is unchanged,
        // because a finished run has nothing in flight and the window restarts when it ends.
        var state = new IdleState(new WeakReference<IMessageHub>(hub), options.IdleDisconnectTimeout, options.TimeProvider);
        idle = state;
        // Dispose the timer WITH the hub for the normal (activated → disposed) path so
        // the queue drops it promptly rather than waiting on a GC.
        hub.RegisterForDisposal(state);
        hub.Register<object>(d =>
        {
            state.Rearm();
            return d;
        });
    }

    /// <summary>
    /// The idle-disconnect: the hub (WEAKLY — the timer queue is a GC root), the window, its one-shot
    /// timer, and the submissions forwarded to the executor that have not answered yet. The timer
    /// holds this object and this object holds the timer — a cycle the GC collects; nothing here
    /// roots the hub.
    ///
    /// <para>🚨 <b>ONE atomic snapshot, never a counter beside a flag</b> (the #4423 review). The
    /// timer's decision — "nothing is working, so close and dispose" — and a submission's — "claim a
    /// slot unless closed" — are each ONE compare-and-swap on the same immutable
    /// <see cref="Snapshot"/>, so the two are totally ordered: a claim lands before the close (and
    /// the close does not happen) or after it (and the claim is refused; the hub is going). There is
    /// no count to go negative, and no window between a decrement and a clamp for a concurrent
    /// claim to fall into: a submission's claim is an OBJECT in a set, removed by that claim alone,
    /// and removing it twice is a no-op.</para>
    /// </summary>
    internal sealed class IdleState : IDisposable
    {
        private readonly WeakReference<IMessageHub> hub;
        private readonly TimeSpan window;
        private readonly ITimer timer;
        private Snapshot current = new(false, ImmutableHashSet<Claim>.Empty);

        /// <summary>Starts the window on <paramref name="clock"/> — <see cref="TimeProvider.System"/> in every host.</summary>
        public IdleState(WeakReference<IMessageHub> hub, TimeSpan window, TimeProvider clock)
        {
            this.hub = hub;
            this.window = window;
            // A STATIC callback over this state: the hub is reachable from the timer only weakly.
            timer = clock.CreateTimer(static s => ((IdleState)s!).Elapsed(), this, window, Timeout.InfiniteTimeSpan);
        }

        /// <summary>Submissions forwarded to the executor that have not answered yet.</summary>
        public int InFlight => Volatile.Read(ref current).InFlight.Count;

        /// <summary>The window elapsed with nothing working, or the hub was disposed: no claim is accepted any more.</summary>
        public bool Closed => Volatile.Read(ref current).Closed;

        /// <summary>
        /// Runs one submission as IN FLIGHT: the claim is taken on subscribe, BEFORE
        /// <paramref name="dispatch"/> sets anything up, so a window that elapses during the setup
        /// sees a submission being set up rather than an idle hub. <paramref name="dispatch"/> runs
        /// INSIDE the tracked observable, so a synchronous throw from the setup or from the post
        /// reaches <c>Finally</c> like a response, an error or a teardown does — every path releases
        /// the claim, and none can pin the hub for ever. A hub the timer already closed refuses the
        /// claim and never runs <paramref name="dispatch"/>.
        /// </summary>
        public IObservable<T> Track<T>(Func<Claim, IObservable<T>> dispatch) =>
            Observable.Defer(() =>
            {
                var claim = new Claim();
                if (!ImmutableInterlocked.Update(ref current, s => s.Closed ? s : s with { InFlight = s.InFlight.Add(claim) }))
                    return Observable.Throw<T>(new ObjectDisposedException(nameof(KernelContainer),
                        "The kernel host was reclaimed after being idle; submit the code again."));
                return Observable.Defer(() => dispatch(claim)).Finally(() => Release(claim));
            });

        /// <summary>A submission answered, failed or was torn down — the idle window starts again from now.</summary>
        private void Release(Claim claim)
        {
            ImmutableInterlocked.Update(ref current, s => s.InFlight.Contains(claim) ? s with { InFlight = s.InFlight.Remove(claim) } : s);
            Rearm();
        }

        /// <summary>Restart the idle window.</summary>
        public void Rearm()
        {
            if (Volatile.Read(ref current).Closed)
                return;
            try { timer.Change(window, Timeout.InfiniteTimeSpan); }
            catch (ObjectDisposedException) { /* disposed with the hub between the read above and here */ }
        }

        /// <summary>
        /// The window elapsed. A submission still executing on a LIVE executor (or still being set
        /// up) keeps the hub and re-arms the window; a dead executor cannot answer, so its claim no
        /// longer means "working", and a hub whose claims are all dead goes.
        /// </summary>
        internal void Elapsed()
        {
            if (ImmutableInterlocked.Update(ref current, s => s.Closed || s.InFlight.Any(c => c.Working) ? s : s with { Closed = true }))
            {
                if (hub.TryGetTarget(out var h))
                    h.Dispose();
                return;
            }
            Rearm();
        }

        /// <summary>The hub is going: refuse further claims and drop the timer from the queue.</summary>
        public void Dispose()
        {
            ImmutableInterlocked.Update(ref current, s => s.Closed ? s : s with { Closed = true });
            timer.Dispose();
        }

        private sealed record Snapshot(bool Closed, ImmutableHashSet<Claim> InFlight);

        /// <summary>One forwarded submission's slot, bound to the executor it was sent to once that exists.</summary>
        internal sealed class Claim
        {
            private WeakReference<IMessageHub>? executor;

            /// <summary>The submission was sent to <paramref name="hosted"/> — only a live one can still answer.</summary>
            public void Bind(IMessageHub hosted) => Volatile.Write(ref executor, new WeakReference<IMessageHub>(hosted));

            /// <summary>Still being set up (no executor yet), or sent to an executor that is alive.</summary>
            internal bool Working =>
                Volatile.Read(ref executor) is not { } bound || (bound.TryGetTarget(out var e) && !e.IsDisposing);
        }
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
        // In flight from BEFORE the executor is set up until it answers — the idle-disconnect must
        // not dispose this hub under a running script (MeshWeaver#4422). The executor setup AND the
        // post run inside the tracked observable: Observe posts before it returns and rethrows a
        // synchronous post failure, so outside it a throw would skip Finally and leak the claim.
        var submission = idle is { } state
            ? state.Track(claim => Dispatch(hub, request.Message, claim))
            : Observable.Defer(() => Dispatch(hub, request.Message, null));
        submission
            .Take(1)
            .Subscribe(
                resp => hub.Post(resp.Message, o => o.ResponseFor(request)),
                ex => hub.Post(
                    new SubmitCodeResponse(request.Message.Id, false) { Error = ex.Message },
                    o => o.ResponseFor(request)));

        return request.Processed();
    }

    /// <summary>Sets up (or reuses) the executor, binds the claim to it, and posts the submission.</summary>
    private IObservable<IMessageDelivery<SubmitCodeResponse>> Dispatch(
        IMessageHub hub, SubmitCodeRequest submission, IdleState.Claim? claim)
    {
        var executor = GetOrCreateExecutor(hub);
        claim?.Bind(executor);
        return hub.Observe<SubmitCodeResponse>(submission, o => o.WithTarget(executor.Address));
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
