using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reflection;
using System.Text.Json;
using MeshWeaver.Domain;
using MeshWeaver.Reflection;
using MeshWeaver.ServiceProvider;
using MeshWeaver.ShortGuid;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Messaging;

/// <summary>
/// The concrete <see cref="IMessageHub"/>: a single-threaded actor that processes
/// <see cref="IMessageDelivery"/> messages serially through a registered rule chain. Owns its
/// hosted child hubs, correlates request/response via AsyncSubject-backed callbacks, and runs a
/// reactive initialization and a phased reactive disposal (Quiescing → DisposeHostedHubs → ShutDown).
/// Sealed; constructed by the framework, not directly by application code.
/// </summary>
public sealed class MessageHub : IMessageHub
{
    /// <summary>This hub's address (its routing/partition key), taken from <see cref="Configuration"/>.</summary>
    public Address Address => Configuration.Address;


    /// <summary>
    /// Schedules <paramref name="action"/> onto the hub's action block by posting an execution
    /// request, so it runs serially with message handling; its async leaf runs off the action block.
    /// Faults are routed to <paramref name="exceptionCallback"/>.
    /// </summary>
    /// <param name="action">The work to run, receiving the hub's cancellation token.</param>
    /// <param name="exceptionCallback">Invoked with any exception thrown by <paramref name="action"/>.</param>
    public void InvokeAsync(Func<CancellationToken, Task> action, Func<Exception, Task> exceptionCallback) =>
        Post(new ExecutionRequest(action, exceptionCallback));

    /// <summary>The DI service provider scoped to this hub.</summary>
    public IServiceProvider ServiceProvider { get; }


    // Per-message response subjects. Observe(...) creates and stores; HandleCallbacks
    // pushes the response onto the matching subject. AsyncSubject emits the last value
    // on subscribe, so subscribers added before AND after the response arrives both see it.
    // Metadata (request type / target / age) is captured at registration so the dispose
    // Quiescing phase can name *which* callbacks are still pending when it times out.
    private readonly Dictionary<string, PendingCallback> responseSubjects = new();

    private sealed record PendingCallback(
        System.Reactive.Subjects.AsyncSubject<IMessageDelivery> Subject,
        string RequestType,
        Address? Target,
        long RegisteredAtTicks);

    /// <summary>
    /// Cross-process dispose trace. Off by default — set <c>MESHWEAVER_DISPOSE_TRACE=1</c>
    /// to enable. When enabled, every phase boundary (Quiescing entry/exit,
    /// DisposeHostedHubs entry/exit, ShutDown entry/exit) is enqueued onto a
    /// single bounded <see cref="System.Threading.Channels.Channel{T}"/> drained
    /// by one writer task — the previous implementation took a global lock
    /// per call and serialized hub teardown under load (~0.7% of test
    /// thread-time). Drops the trace line silently when the channel is full
    /// so a stalled writer can never delay dispose. <c>tail -f</c> the file
    /// to spot a stalled phase.
    /// </summary>
    private static readonly bool DisposeTraceEnabled =
        Environment.GetEnvironmentVariable("MESHWEAVER_DISPOSE_TRACE") is "1" or "true" or "True";
    private static readonly string DisposeTraceLogPath =
        Path.Combine(Path.GetTempPath(), "meshweaver-dispose-trace.log");

    /// <summary>
    /// Bounded async queue + single-writer drain task. Bounded depth means a
    /// misbehaving disk (slow append, locked file) puts back-pressure on the
    /// channel rather than serializing hub teardown via lock contention.
    /// Drop-write on full so the trace stays best-effort.
    /// </summary>
    private static readonly System.Threading.Channels.Channel<string>? DisposeTraceChannel =
        DisposeTraceEnabled
            ? System.Threading.Channels.Channel.CreateBounded<string>(
                new System.Threading.Channels.BoundedChannelOptions(4096)
                {
                    FullMode = System.Threading.Channels.BoundedChannelFullMode.DropWrite,
                    SingleReader = true
                })
            : null;

    static MessageHub()
    {
        if (DisposeTraceChannel is null) return;
        var reader = DisposeTraceChannel.Reader;
        _ = Task.Run(async () =>
        {
            try
            {
                while (await reader.WaitToReadAsync())
                {
                    while (reader.TryRead(out var line))
                    {
                        try { File.AppendAllText(DisposeTraceLogPath, line + Environment.NewLine); }
                        catch
                        {
                            // Tracing must never throw out of the writer; drop the line.
                        }
                    }
                }
            }
            catch
            {
                // Outer guard — channel completion is the only normal exit path.
            }
        });
    }

    private static void DisposeTrace(Address address, string phase, long elapsedMs, string? extra = null)
    {
        if (DisposeTraceChannel is null) return;
        var line = extra is null
            ? $"{DateTime.UtcNow:HH:mm:ss.fff} {address} {phase} elapsed={elapsedMs}ms"
            : $"{DateTime.UtcNow:HH:mm:ss.fff} {address} {phase} elapsed={elapsedMs}ms {extra}";
        // Non-blocking: drops on full so a stuck writer never delays dispose.
        DisposeTraceChannel.Writer.TryWrite(line);
    }

    private readonly ILogger logger;
    /// <summary>The immutable configuration this hub was built from.</summary>
    public MessageHubConfiguration Configuration { get; }

    /// <summary>
    /// Fallback-hub NACK policy (<see cref="UnhandledMessageNack"/>), resolved once
    /// at construction — null on regular hubs, set on hubs standing in for a node
    /// whose NodeType produced no usable configuration. See FinishDelivery.
    /// </summary>
    private readonly UnhandledMessageNack? unhandledNack;
    private readonly HostedHubsCollection hostedHubs;
    private readonly AccessService accessService;

    /// <summary>Monotonic counter, incremented once per message processed; used for ordering and disposal sequencing.</summary>
    public long Version { get; private set; }

    /// <summary>
    /// Disposal-health diagnostic: how many <see cref="ShutdownRequest"/> turns this hub
    /// has handled. A healthy disposal handles exactly the three phase requests
    /// (Quiescing → DisposeHostedHubs → ShutDown). A value in the thousands is the
    /// signature of the version-match repost STORM removed from <c>HandleShutdownCore</c>
    /// (see the regression test <c>Dispose_UnderContinuousLoad_DoesNotStormShutdownRequests</c>).
    /// </summary>
    public int ShutdownTurnsHandled => shutdownTurnsHandled;
    private int shutdownTurnsHandled;

    /// <summary>
    /// Sets the initial version for the hub. Only callable during initialization
    /// before any messages are processed.
    /// </summary>
    public void SetInitialVersion(long version)
    {
        Version = version;
    }

    /// <summary>The hub's current lifecycle phase; advances through start, quiescing, and the disposal phases.</summary>
    public MessageHubRunLevel RunLevel { get; private set; }

    /// <summary>
    /// Non-null once a BuildupAction faulted during init. The hub stays <see cref="MessageHubRunLevel.Started"/>
    /// (the init gate is open, so it still REACTS to messages and can be torn down) but is in a FAILED state:
    /// every non-lifecycle request is refused with a typed <see cref="DeliveryFailure"/>
    /// (<see cref="ErrorType.Failed"/>) carrying this error. This is the "status failed" marker — mirrors
    /// <c>DataContext.InitializationError</c> (MeshWeaver.Data), lifted to the hub level. Set by
    /// <see cref="EnterInitializationFailedState"/>.
    /// </summary>
    public Exception? InitializationError { get; private set; }

    /// <summary>
    /// Upper bound on how long a hub's BuildupActions may run before init is declared FAILED rather
    /// than wedging forever behind a closed gate (see <see cref="HandleInitialize"/>). Generous on
    /// purpose — every legitimate init, including a NodeType compile, completes well inside it; only a
    /// genuine hang trips it. A hub can tighten it via <c>Configuration.StartupTimeout</c>.
    /// </summary>
    private static readonly TimeSpan DefaultInitializationTimeout = TimeSpan.FromSeconds(120);

    private readonly IMessageService messageService;
    /// <summary>
    /// Parent hub address captured at construction. Used in disposal logging so we
    /// don't re-resolve from <see cref="MessageHubConfiguration.ParentHub"/> on a
    /// scope that may already be disposed.
    /// </summary>
    private readonly Address? parentAddress;
    /// <summary>The hub's type registry, mapping message type names to CLR types for (de)serialization and routing.</summary>
    public ITypeRegistry TypeRegistry { get; }
    /// <summary>
    /// Transitions the hub to <see cref="MessageHubRunLevel.Started"/> (idempotent) and completes the
    /// <see cref="Started"/> task. Called by the framework once initialization gates are open.
    /// </summary>
    public void Start()
    {
        if (RunLevel < MessageHubRunLevel.Started)
        {
            RunLevel = MessageHubRunLevel.Started;
            hasStarted.TrySetResult();
        }
    }

    /// <summary>
    /// Faults the <see cref="Started"/> task with <paramref name="error"/> so dependents observing
    /// startup (e.g. data-source initialization) also fault. Called when a stream errors during init.
    /// <para>
    /// Teardown-disposal is classified as CANCELLATION, not failure: when the cause chain
    /// contains an <see cref="ObjectDisposedException"/> (the shape <c>CancelCallbacks</c>
    /// pushes into pending <c>Observe</c> subjects at hub disposal — "Hub … was disposed
    /// before the response arrived"), startup didn't fail, it will simply never happen.
    /// A sync hub's <see cref="Started"/> task has NO awaiter at teardown, so faulting it
    /// armed a <c>TaskScheduler.UnobservedTaskException</c> that detonated at the next GC —
    /// xUnit v3 escalates that to a "Catastrophic failure" poisoning the next test class
    /// (the #228 capture). A canceled task never raises UnobservedTaskException, and a live
    /// awaiter still gets a graceful, typed <see cref="TaskCanceledException"/>
    /// (<c>DataContext.OpenInitializationGate</c> handles <c>IsCanceled</c> explicitly).
    /// Real startup errors keep faulting <see cref="Started"/> so dependents observe them.
    /// Pinned by <c>FailStartupTeardownClassificationTest</c> and
    /// <c>TeardownPendingSubscribeGracefulTest</c>.
    /// </para>
    /// </summary>
    /// <param name="error">The exception that caused startup to fail.</param>
    public void FailStartup(Exception error)
    {
        if (IsTeardownDisposal(error))
            hasStarted.TrySetCanceled();
        else
            hasStarted.TrySetException(error);
    }

    /// <summary>
    /// True when <paramref name="error"/> (or any exception in its cause chain) is an
    /// <see cref="ObjectDisposedException"/> — the benign teardown shape. Walks the chain
    /// because the disposal error arrives both bare (the SubscribeRequest observe path) and
    /// wrapped (e.g. <c>InvalidOperationException</c> → ODE from the DataChangeRequest
    /// observe path in <c>JsonSynchronizationStream</c>); mirrors
    /// <c>SynchronizationStream.IsObjectDisposed</c>.
    /// </summary>
    private static bool IsTeardownDisposal(Exception? error)
    {
        for (var e = error; e != null; e = e.InnerException)
            if (e is ObjectDisposedException)
                return true;
        return false;
    }

    /// <summary>
    /// Cancels the currently-executing handler's cancellation token and rolls a fresh one for
    /// subsequent messages, aborting a long-running handler (e.g. streaming) without disposing the hub.
    /// </summary>
    public void CancelCurrentExecution()
    {
        messageService.CancelExecution();
    }

    /// <summary>
    /// The hub's message-storm circuit-breaker (when backed by the default
    /// <see cref="MessageService"/>; <c>null</c> for an alternative message service).
    /// Exposed so the framework's tests can observe its trip signal deterministically.
    /// </summary>
    public MessageStormBreaker? StormBreaker =>
        messageService is MessageService ms ? ms.StormBreaker : null;

    /// <summary>
    /// Starts message processing and posts the initialization request.
    /// Called from Build() after SyncBuildupActions complete.
    /// </summary>
    internal void StartMessageProcessing()
    {
        messageService.Start();
        InstallStaleCallbackScanner();
        if (!Configuration.DeferredInitialization)
            Post(new InitializeHubRequest());
    }

    /// <summary>Periodic interval for the stale-callback scanner.</summary>
    internal static readonly TimeSpan StaleCallbackScanInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A pending callback older than this is logged at Warning. Calibrated to
    /// catch hangs that the user perceives (10–30 s is "the UI is stuck") while
    /// staying above legitimate slow request paths (cold-start hub activation,
    /// first-token agent latency, large query fan-out). Adjust via
    /// <c>MESHWEAVER_STALE_CALLBACK_MS</c> env var if a deployment has higher
    /// expected latencies.
    /// </summary>
    internal static readonly TimeSpan StaleCallbackThreshold =
        long.TryParse(Environment.GetEnvironmentVariable("MESHWEAVER_STALE_CALLBACK_MS"), out var ms)
            && ms > 0
            ? TimeSpan.FromMilliseconds(ms)
            : TimeSpan.FromSeconds(30);

    private IDisposable? staleCallbackScannerSub;

    /// <summary>
    /// Always-on per-hub scanner. Every <see cref="StaleCallbackScanInterval"/>
    /// snapshots <see cref="SnapshotPendingCallbacks"/>, filters entries older
    /// than <see cref="StaleCallbackThreshold"/>, and logs them at Warning so
    /// hangs are observable while in flight (no need to wait for the dispose
    /// quiesce timeout to surface "we were stuck on X").
    ///
    /// <para>Cost: one timer tick per hub every 5 s + one dictionary scan;
    /// negligible. Disposed explicitly at the start of the Quiescing phase
    /// (and as a fallback via the <c>disposables</c> composite).</para>
    /// </summary>
    private void InstallStaleCallbackScanner()
    {
        var thresholdMs = (long)StaleCallbackThreshold.TotalMilliseconds;
        // 🚨 WEAK self-reference. The scanner is a DIAGNOSTIC — it must NEVER keep its
        // hub alive. Observable.Interval lives on the global Rx scheduler's TimerQueue
        // (a GC strong-root); a closure capturing `this` STRONGLY pins the hub via
        // TimerQueue → Rx PeriodicTimer → DisplayClass → MessageHub for as long as the
        // timer runs. That is fine for a hub that gets DISPOSED (the dispose path kills
        // the timer), but an ABANDONED hub — created, RunLevel=1, never disposed
        // (e.g. a sync/ SynchronizationStream hub orphaned at mesh teardown) — never
        // disposes its scanner, so the timer pins it forever and it accumulates across
        // meshes. That is the MeshHub_IsCollected leak (ClrMD: TimerQueue → Rx
        // PeriodicTimer → DisplayClass44 → MessageHub[sync/…, RunLevel=1]). With a weak
        // ref the abandoned hub is collectable; the scanner observes it dead on the next
        // tick and self-disposes. A LIVE hub is held by its real owners (parent
        // hosted-hubs / DI), so the weak ref always resolves while the hub matters.
        var weakSelf = new WeakReference<MessageHub>(this);
        var sub = new System.Reactive.Disposables.SingleAssignmentDisposable();
        sub.Disposable = Observable
            .Interval(StaleCallbackScanInterval)
            .Subscribe(_ =>
            {
                if (!weakSelf.TryGetTarget(out var self))
                {
                    // Hub was collected (abandoned + GC'd). Stop the timer so the whole
                    // scanner graph becomes unreachable. `sub` is captured directly, so
                    // this needs no reference back to the (now-gone) hub.
                    sub.Dispose();
                    return;
                }
                self.ScanStaleCallbacks(thresholdMs);
            });
        staleCallbackScannerSub = sub;
        // Also register in the disposables composite so a NORMAL teardown kills the
        // timer promptly (rather than waiting for the next tick after GC). Double-dispose
        // with the explicit Quiescing-phase dispose is harmless (Rx is idempotent).
        disposables.Add(sub);
    }

    /// <summary>Single scan tick — extracted so the timer closure captures only a
    /// <see cref="WeakReference{T}"/> to the hub, never <c>this</c> (see
    /// <see cref="InstallStaleCallbackScanner"/>).</summary>
    private void ScanStaleCallbacks(long thresholdMs)
    {
        try
        {
            var pending = SnapshotPendingCallbacks();
            if (pending.Length == 0) return;
            var stale = pending.Where(p => p.AgeMs > thresholdMs).ToArray();
            if (stale.Length == 0) return;
            TryLog(LogLevel.Warning,
                "[STALE-CALLBACK] {Address}: {Count} callback(s) pending > {ThresholdMs}ms: {Detail}",
                Address, stale.Length, thresholdMs, FormatPendingCallbacks(stale));
        }
        catch (Exception ex)
        {
            TryLog(LogLevel.Debug,
                "[STALE-CALLBACK] {Address}: scan tick failed: {Error}",
                Address, ex.Message);
        }
    }

    private readonly ThreadSafeLinkedList<AsyncDelivery> rules = new();
    private readonly Lock messageHandlerRegistrationLock = new();
    private readonly Lock typeRegistryLock = new();
    /// <summary>
    /// Constructs the hub: wires DI, the type registry, the message service, JSON options, the
    /// built-in lifecycle handlers (dispose / shutdown / ping / initialize) and the configured
    /// message handlers. Message processing is started separately (by the configuration's Build,
    /// after synchronous buildup completes), not from this constructor.
    /// </summary>
    /// <param name="serviceProvider">The DI service provider scoped to this hub.</param>
    /// <param name="hostedHubs">The collection that owns this hub's hosted child hubs.</param>
    /// <param name="configuration">The configuration describing address, handlers, buildup/dispose actions and timeouts.</param>
    /// <param name="parentHub">The parent hub, or <c>null</c> for a root hub; used for routing and inherited JSON options.</param>
    public MessageHub(
        IServiceProvider serviceProvider,
        HostedHubsCollection hostedHubs,
        MessageHubConfiguration configuration,
        IMessageHub? parentHub
    )
    {
        serviceProvider.Buildup(this);
        serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(GetType());
        logger = serviceProvider.GetRequiredService<ILogger<MessageHub>>();

        logger.LogDebug("Starting MessageHub construction for address {Address} with parent {Parent}", configuration.Address, parentHub?.Address);

        TypeRegistry = serviceProvider.GetRequiredService<ITypeRegistry>();
        InitializeTypes(this);

        this.hostedHubs = hostedHubs;
        ServiceProvider = serviceProvider;
        Configuration = configuration;
        unhandledNack = configuration.Get<UnhandledMessageNack>();
        parentAddress = parentHub?.Address;
        accessService = serviceProvider.GetRequiredService<AccessService>();


        messageService = new MessageService(configuration.Address,
            serviceProvider.GetRequiredService<ILogger<MessageService>>(), this, parentHub);

        foreach (var disposeAction in configuration.DisposeActions)
            RegisterForDisposal(disposeAction);

        JsonSerializerOptions = this.CreateJsonSerializationOptions(parentHub);

        TypeRegistry.WithType(typeof(PingRequest), nameof(PingRequest));
        TypeRegistry.WithType(typeof(PingResponse), nameof(PingResponse));
        Register<DisposeRequest>(HandleDispose);
        // Disposal is fully SYNCHRONOUS + reactive — the handler returns immediately, kicks off
        // the Quiescing poll / hosted-hub drain as Rx subscriptions (Observable.Interval/Timer,
        // off the action block), and completes via the `disposalCompleted` ReplaySubject. No
        // async leaf, no DeliveryObservable.Run, no FromAsync on the disposal path.
        Register<ShutdownRequest>((request, _) => Observable.Return(HandleShutdownCore(request)));
        Register<PingRequest>(HandlePingRequest);
        // HandleInitialize already returns IObservable (buildup composed via Observable.Concat,
        // no await) — register it directly now that the rule chain is reactive.
        Register<InitializeHubRequest>((request, ct) => HandleInitialize(request));
        lock (messageHandlerRegistrationLock)
        {
            foreach (var messageHandler in configuration.MessageHandlers)
                Register(
                    messageHandler.MessageType,
                    (d, c) => messageHandler.AsyncDelivery.Invoke(this, d, c)
                );
        }
        Register(ExecuteRequest);
        Register(HandleCallbacks);

        // Note: messageService.Start() is called from MessageHubConfiguration.Build()
        // AFTER SyncBuildupActions complete, to ensure services like Workspace/DataContext
        // are fully configured before any messages arrive
    }

    private IMessageDelivery HandlePingRequest(IMessageDelivery<PingRequest> request)
    {
        Post(new PingResponse(), o => o.ResponseFor(request));
        return request.Processed();
    }

    /// <summary>
    /// Reactive hub initialization: composes the configured buildup observables in order
    /// (<see cref="Observable.Concat{TSource}(System.Collections.Generic.IEnumerable{IObservable{TSource}})"/>)
    /// and opens the Initialize gate when the composed sequence completes — no <c>await</c>/<c>for</c>-await.
    /// Each action advances on its first emission (or <c>DefaultIfEmpty</c>) via <c>Take(1)</c>, matching the
    /// previous FirstAsync-per-action semantics; the gate opens exactly once after every action has signalled.
    /// </summary>
    /// <remarks>
    /// <para><b>Robustness — a faulting BuildupAction must NOT wedge the hub.</b> A throw in init used to
    /// propagate out of the <c>Concat</c> so the <c>Select</c> that calls <see cref="OpenGate"/> never ran:
    /// the Initialize gate stayed closed forever, so EVERY later message deferred until the 30s
    /// deferral-timeout — which the user experiences as an unrecoverable hang (the atioz AgenticPension
    /// agent-select wedge, 2026-06-16). The <c>.Catch</c> below mirrors
    /// <c>DataContext</c> (MeshWeaver.Data)'s per-context guard, lifted to the hub level so EVERY
    /// BuildupAction is covered: on a fault the hub enters a FAILED state (see
    /// <see cref="EnterInitializationFailedState"/>) that answers every request with a typed
    /// <see cref="DeliveryFailure"/> carrying the init error — FAST, not a 30s wedge — and ALWAYS opens the
    /// gate so those rejections actually flow. The error is now observable end-to-end: callers get a
    /// <c>DeliveryFailure</c> and the GUI's area binding renders it instead of spinning forever. See
    /// <c>Doc/Architecture/HubInitializationFailure.md</c>.</para>
    /// Bridged to the Task-based rule chain at the <c>Register</c> edge.
    /// </remarks>
    private IObservable<IMessageDelivery> HandleInitialize(IMessageDelivery<InitializeHubRequest> request)
    {
        logger.LogDebug("Message hub {address} initializing via InitializeHubRequest", Address);

        var actions = Configuration.BuildupActions;
        logger.LogDebug("Message hub {address} has {count} BuildupActions to run", Address, actions.Count);

        return Observable
            .Concat(actions.Select(a => a(this).DefaultIfEmpty(Unit.Default).Take(1)))
            .ToList()
            // 🚫 Liveness bound. A BuildupAction that HANGS — never emits and never completes (a
            // dependency that never initialises, a stuck NodeType compile, a subscribe that never
            // fires) — leaves the Concat incomplete, so the gate never opens and EVERY message defers
            // to the 30s deferral-timeout: the hub wedges forever. A throw is caught below; a hang
            // raises no exception, so convert "never completes within the budget" into a
            // TimeoutException the SAME .Catch handles. Generous default (every legit init, incl. a
            // NodeType compile, finishes well inside it); a hub may tighten it via
            // Configuration.StartupTimeout.
            .Timeout(Configuration.StartupTimeout ?? DefaultInitializationTimeout)
            .Select(_ =>
            {
                logger.LogDebug("Message hub {address} BuildupActions complete, opening Initialize gate", Address);

                // Open the Initialize gate - this will set RunLevel to Started if all other gates are also open
                OpenGate(MessageHubConfiguration.InitializeGateName);

                return request.Processed();
            })
            .Catch((Exception ex) =>
            {
                // Init failed — a BuildupAction faulted (threw) or HUNG (TimeoutException from the bound
                // above). Do NOT leave the gate closed (→ the 30s-per-message deferral wedge): enter a
                // FAILED state that surfaces a clear DeliveryFailure for every later request, then
                // ALWAYS open the gate so those rejections (and disposal) can flow.
                var reason = ex is TimeoutException
                    ? $"a BuildupAction did not complete within "
                      + $"{(Configuration.StartupTimeout ?? DefaultInitializationTimeout).TotalSeconds:F0}s "
                      + "(a hung dependency or stuck compile)"
                    : $"a BuildupAction faulted ({ex.GetType().Name}: {ex.Message})";
                logger.LogError(ex,
                    "Hub {Address} initialization failed — {Reason}. Hub is now in FAILED state.", Address, reason);
                EnterInitializationFailedState(new InvalidOperationException(reason, ex));
                OpenGate(MessageHubConfiguration.InitializeGateName);
                return Observable.Return(request.Failed($"Hub '{Address}' initialization failed — {reason}"));
            });
    }

    /// <summary>
    /// Puts the hub in a FAILED state after an initialization fault. Registers a front-of-chain rule that
    /// answers every subsequent request with a <see cref="DeliveryFailure"/> carrying the init error, so
    /// callers — and the GUI's area binding — get a clear, FAST error instead of the 30s deferral-timeout
    /// wedge a closed init gate produces. Lifecycle/control messages pass through unchanged so the hub can
    /// still be torn down and the failure can't ping-pong (the same bypass set
    /// <see cref="MessageService"/> applies at the gate). Mirrors
    /// <c>DataContext</c> (MeshWeaver.Data)'s per-context guard, lifted to the hub level.
    /// </summary>
    private void EnterInitializationFailedState(Exception initException)
    {
        // Status = failed. RunLevel stays Started (the gate opens below) so the hub still reacts to and
        // refuses messages; InitializationError is the queryable "failed" marker.
        InitializationError = initException;
        var errorMessage = $"Hub '{Address}' initialization failed: {initException.Message}";
        Register(delivery =>
        {
            // Let lifecycle/control traffic through: disposal must still work, and a DeliveryFailure must
            // never beget another DeliveryFailure (storm). Everything else is rejected with the init error.
            if (delivery.Message is DeliveryFailure or ShutdownRequest or DisposeRequest
                or InitializeHubRequest or HeartBeatEvent)
                return delivery;

            logger.LogWarning("Hub {Address} is in FAILED state. Rejecting {MessageType} from {Sender}: {Error}",
                Address, delivery.Message.GetType().Name, delivery.Sender, errorMessage);
            Post(new DeliveryFailure(delivery) { ErrorType = ErrorType.Failed, Message = errorMessage },
                o => o.ResponseFor(delivery));
            return delivery.Processed();
        });
    }

    #region Message Types
    private void InitializeTypes(object instance)
    {
        foreach (
            var registry in instance
                .GetType()
                .GetAllInterfaces()
                .Select(i => GetTypeAndHandler(i, instance))
                .Where(x => x != null)
        )
        {
            if (registry!.Action != null)
                Register(registry.Action, d => registry.Type.IsInstanceOfType(d.Message));


            WithTypeAndRelatedTypesFor(registry.Type);
        }
    }
    private void WithTypeAndRelatedTypesFor(Type? typeToRegister)
    {
        if (typeToRegister == null) return;

        lock (typeRegistryLock)
        {
            logger.LogTrace("Registering type {TypeName} and related types in hub {Address}", typeToRegister.Name, Address);

            TypeRegistry.WithType(typeToRegister);

            var types = typeToRegister
                .GetAllInterfaces()
                .Where(x => x.IsGenericType && x.GetGenericTypeDefinition() == typeof(IRequest<>))
                .SelectMany(x => x.GetGenericArguments());

            foreach (var type in types)
            {
                TypeRegistry.WithType(type);
            }

            if (typeToRegister.IsGenericType)
            {
                foreach (var genericType in typeToRegister.GetGenericArguments())
                    TypeRegistry.WithType(genericType);
            }

            logger.LogTrace("Completed type registration for {TypeName} in hub {Address}", typeToRegister.Name, Address);
        }
    }

    private TypeAndHandler? GetTypeAndHandler(Type type, object instance)
    {
        if (
            !type.IsGenericType
            || !MessageHubPluginExtensions.HandlerTypes.Contains(type.GetGenericTypeDefinition())
        )
            return null;
        var genericArgs = type.GetGenericArguments();

        var cancellationToken = new CancellationTokenSource().Token; // todo: think how to handle this
        if (type.GetGenericTypeDefinition() == typeof(IMessageHandler<>))
            return new(
                genericArgs.First(),
                CreateDelivery(genericArgs.First(), type, instance, Expression.Constant(cancellationToken))
            );
        if (type.GetGenericTypeDefinition() == typeof(IMessageHandlerAsync<>))
            return new(
                genericArgs.First(),
                CreateDelivery(
                    genericArgs.First(),
                    type,
                    instance,
                    Expression.Constant(cancellationToken)
                )
            );

        return null;
    }
    private AsyncDelivery CreateDelivery(
        Type messageType,
        Type interfaceType,
        object instance,
        Expression? cancellationToken
    )
    {
        var prm = Expression.Parameter(typeof(IMessageDelivery));
        var cancellationTokenPrm = Expression.Parameter(typeof(CancellationToken));

        var expressions = new List<Expression>
        {
            Expression.Convert(prm, typeof(IMessageDelivery<>).MakeGenericType(messageType))
        };
        if (cancellationToken != null)
            expressions.Add(cancellationToken);
        var handlerCall = Expression.Call(
            Expression.Constant(instance, interfaceType),
            interfaceType.GetMethods(BindingFlags.Instance | BindingFlags.Public).First(),
            expressions
        );

        // Sync IMessageHandler<> returns IMessageDelivery → wrap in Observable.Return;
        // async IMessageHandlerAsync<> already returns IObservable<IMessageDelivery>.
        if (interfaceType.GetGenericTypeDefinition() == typeof(IMessageHandler<>))
            handlerCall = Expression.Call(
                null,
                MessageHubPluginExtensions.ObservableReturnMethod,
                handlerCall
            );

        var lambda = Expression
            .Lambda<Func<IMessageDelivery, CancellationToken, IObservable<IMessageDelivery>>>(
                handlerCall,
                prm,
                cancellationTokenPrm
            )
            .Compile();
        return (d, c) => lambda(d, c);
    }

    private record TypeAndHandler(Type Type, AsyncDelivery? Action);



    #endregion



    private IObservable<IMessageDelivery> HandleMessageAsync(
        IMessageDelivery delivery,
        AsyncDelivery[] ruleChain,
        CancellationToken cancellationToken
    )
    {
        // Reactive fold over the rule chain: each rule maps the running delivery
        // to an IObservable that emits the transformed delivery, fed to the next
        // rule via SelectMany. Same sequential semantics as the previous
        // await-loop — sync rules (Observable.Return) collapse synchronously on
        // subscribe (on the action-block thread), genuinely-async rules complete
        // later via the pool. The chain is BUILT here (one SelectMany per rule);
        // it runs when the actor-loop edge subscribes (.ToTask).
        //
        // 🚨 ruleChain is a SNAPSHOT taken under ThreadSafeLinkedList's read lock — NOT a live
        // walk of LinkedListNode.Next. A raw .Next walk races a concurrent rules.Remove(node)
        // (a handler disposable firing during hub teardown / rapid sync-hub churn): LinkedList
        // invalidates the removed node's owning-list reference before its next pointer, so a
        // racing get_Next() dereferences list.head and throws NRE → the delivery fails → sync
        // streams see it as [SYNC_STREAM] OnError and their subscribers time out (a different
        // sync-hub test flakes each bulk run). Iterating the snapshot is immune to that race.
        if (ruleChain.Length > 500)
            throw new InvalidOperationException($"HandleMessageAsync rule count exceeded 500 in hub {Address} for {delivery.Message.GetType().Name}");
        IObservable<IMessageDelivery> result = Observable.Return(delivery);
        foreach (var rule in ruleChain)
            result = result.SelectMany(d => rule.Invoke(d, cancellationToken));
        return result;
    }

    /// <summary>
    /// Opens the named initialization gate on the message service, releasing messages deferred behind it.
    /// </summary>
    /// <param name="name">The name of the gate to open.</param>
    /// <returns><c>true</c> if the gate existed and was opened; <c>false</c> if it was already open or not found.</returns>
    public bool OpenGate(string name)
    {
        return messageService.OpenGate(name);
    }


    /// <summary>
    /// Threshold above which per-message dispatch latency is reported at
    /// <see cref="LogLevel.Information"/> so it surfaces in Grafana/Loki without
    /// LogLevel.Trace flooding. Tuned so chat / layout / routing hops only log
    /// when something is genuinely slow.
    /// </summary>
    private static readonly long SlowDispatchTicks = (long)(TimeSpan.TicksPerMillisecond * 500);

    // Reactive end-to-end: IObservable, no async/await, no Task in the signature.
    // Runs INLINE on the turn thread (Defer → factory on Subscribe); a synchronous
    // rule chain completes inline, so the turn never leaves the thread. AccessContext
    // is set on entry and restored on terminate — in Select (success) BEFORE
    // FinishDelivery and in Catch (error) — the reactive equivalent of the old
    // try/finally restore.
    IObservable<IMessageDelivery> IMessageHub.HandleMessageAsync(
        IMessageDelivery delivery,
        CancellationToken cancellationToken
    ) => Observable.Defer(() =>
    {
        ++Version;
        var dispatchStartTicks = Stopwatch.GetTimestamp();

        var traceEnabled = logger.IsEnabled(LogLevel.Trace);
        string? messageTypeName = traceEnabled ? delivery.Message.GetType().Name : null;
        if (traceEnabled)
            logger.LogTrace("MESSAGE_FLOW: HUB_HANDLE_START | {MessageType} | Hub: {Address} | MessageId: {MessageId} | Version: {Version}",
                messageTypeName, Address, delivery.Id, Version);

        if (IsDisposing && delivery.Message is ShutdownRequest shutdownReq)
            logger.LogDebug("Processing ShutdownRequest in {Address} : RunLevel={RunLevel}, Version={RequestVersion}, Expected={ExpectedVersion}",
                Address, shutdownReq.RunLevel, shutdownReq.Version, Version - 1);

        // 🚨 Systematic AccessContext propagation — stamp the SENDER's identity for
        // the duration of handling. Only USER identities propagate to AsyncLocal;
        // hub-shaped principals MUST NOT leak. See Doc/Architecture/AccessContextPropagation.md.
        var prevContext = accessService.Context;
        if (delivery.AccessContext is not null
            && !AccessService.LooksLikeHubPrincipal(delivery.AccessContext.ObjectId))
            accessService.SetContext(delivery.AccessContext);

        // Snapshot the rule chain ONCE under the list's read lock (see HandleMessageAsync) so a
        // concurrent rules.Remove during teardown can't NRE the iteration.
        var ruleChain = rules.Snapshot();
        if (traceEnabled)
            logger.LogTrace(ruleChain.Length > 0
                    ? "MESSAGE_FLOW: HUB_PROCESSING_RULES | {MessageType} | Hub: {Address} | MessageId: {MessageId}"
                    : "MESSAGE_FLOW: HUB_NO_RULES | {MessageType} | Hub: {Address} | MessageId: {MessageId}",
                messageTypeName, Address, delivery.Id);

        var chain = ruleChain.Length > 0
            ? HandleMessageAsync(delivery, ruleChain, cancellationToken)
            : Observable.Return(delivery);

        return chain
            .Select(handled =>
            {
                accessService.SetContext(prevContext);
                var result = FinishDelivery(handled);
                if (traceEnabled)
                    logger.LogTrace("MESSAGE_FLOW: HUB_HANDLE_END | {MessageType} | Hub: {Address} | MessageId: {MessageId} | Result: {State}",
                        messageTypeName, Address, delivery.Id, result.State);
                var elapsedTicks = Stopwatch.GetTimestamp() - dispatchStartTicks;
                if (elapsedTicks > SlowDispatchTicks)
                {
                    var elapsedMs = elapsedTicks * 1000.0 / Stopwatch.Frequency;
                    logger.LogInformation(
                        "MESSAGE_FLOW: SLOW_DISPATCH | {MessageType} | Hub: {Address} | MessageId: {MessageId} | Elapsed: {ElapsedMs:F0}ms | Sender: {Sender} | Target: {Target}",
                        messageTypeName ?? delivery.Message.GetType().Name,
                        Address, delivery.Id, elapsedMs, delivery.Sender, delivery.Target);
                }
                return result;
            })
            .Catch((Exception ex) =>
            {
                accessService.SetContext(prevContext);
                return Observable.Throw<IMessageDelivery>(ex);
            });
    });

    private IMessageDelivery FinishDelivery(IMessageDelivery delivery)
    {
        // Per-message hot path. Skip the GetType().Name + boxing when Debug is off.
        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebug("FinishDelivery called for {MessageType} (ID: {MessageId}) with state {State} in {Address}",
                delivery.Message.GetType().Name, delivery.Id, delivery.State, Address);

        if (delivery.State == MessageDeliveryState.Submitted)
        {
            // Fallback-hub contract (UnhandledMessageNack policy): this hub stands in
            // for a node whose NodeType couldn't produce a real configuration. Anything
            // its (default/overlay) config didn't handle — including RawJson deliveries
            // whose type the broken assembly would have registered — is answered with a
            // typed DeliveryFailure naming the broken NodeType, never silently Ignored.
            // Guard: never NACK a NACK (DeliveryFailure ping-pong).
            var nackPolicy = unhandledNack;
            if (nackPolicy is not null)
            {
                if (delivery.Message is DeliveryFailure)
                    return delivery.Ignored();

                logger.LogWarning(
                    "Unhandled {MessageType} (ID: {MessageId}) in fallback hub {Address} - answering {ErrorType} NACK: {Reason}",
                    delivery.Message.GetType().Name, delivery.Id, Address, nackPolicy.ErrorType, nackPolicy.Reason);
                try
                {
                    Post(new DeliveryFailure(delivery)
                    {
                        ErrorType = nackPolicy.ErrorType,
                        NodeTypePath = nackPolicy.NodeTypePath,
                        Message = nackPolicy.Reason
                    }, o => o.ResponseFor(delivery));
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to post fallback NACK for {MessageType} (ID: {MessageId}) in {Address}",
                        delivery.Message.GetType().Name, delivery.Id, Address);
                }
                return delivery.Failed(nackPolicy.Reason);
            }

            // Check if this is a request that expects a response
            var messageType = delivery.Message.GetType();
            var isRequest = messageType.GetInterfaces()
                .Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequest<>));

            logger.LogDebug("Message {MessageType} (ID: {MessageId}) is request: {IsRequest} in {Address}",
                messageType.Name, delivery.Id, isRequest, Address);

            if (isRequest)
            {
                // Send DeliveryFailure response for unhandled requests
                var failure = DeliveryFailure.FromException(delivery,
                    new InvalidOperationException($"No handler found for message type {messageType.Name}"));
                failure = failure with { ErrorType = ErrorType.NotFound };

                logger.LogWarning("No handler found for request {MessageType} (ID: {MessageId}) in {Address} - sending DeliveryFailure response",
                    messageType.Name, delivery.Id, Address);

                try
                {
                    Post(failure, o => o.ResponseFor(delivery));
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to post DeliveryFailure message for unhandled request {MessageType} (ID: {MessageId}) in {Address}",
                        messageType.Name, delivery.Id, Address);
                }

                return delivery.Failed($"No handler found for {messageType.Name}");
            }

            return delivery.Ignored();
        }
        return delivery;
    }

    private readonly TaskCompletionSource hasStarted = new();
    /// <summary>
    /// Completes when the hub has finished initialization; faults via <see cref="FailStartup"/> if startup failed.
    /// </summary>
    public Task Started => hasStarted.Task;








    /// <summary>
    /// Sync factory that returns an <see cref="IObservable{IMessageDelivery}"/> for the
    /// response to <paramref name="delivery"/>. The observable emits exactly one item
    /// when the response arrives (or <c>OnError</c> for <see cref="DeliveryFailureException"/> /
    /// <see cref="TimeoutException"/>). No Task, no <c>TaskCompletionSource</c>, no
    /// <c>async</c>: just an <see cref="System.Reactive.Subjects.AsyncSubject{T}"/> whose
    /// emission is triggered when <see cref="HandleCallbacks"/> matches the response.
    /// </summary>
    public IObservable<IMessageDelivery> Observe(IMessageDelivery delivery)
    {
        var requestType = delivery.Message?.GetType().Name ?? "<null>";
        return RestoreUserContextOnEmission(
            ObserveById(delivery.Id, requestType, delivery.Target),
            delivery.AccessContext);
    }

    /// <summary>
    /// Posts <paramref name="r"/> with a pre-generated message id and returns the
    /// observable for its response. Registering the subject BEFORE posting avoids the
    /// race where a synchronously-handled response arrives before the subscription is
    /// in place.
    /// </summary>
    public IObservable<IMessageDelivery> Observe(object r, Func<PostOptions, PostOptions> options)
    {
        if (r is IMessageDelivery existing)
            return Observe(existing);

        // Capture the caller's AccessContext at observe-time. The response delivery
        // arrives on the hub action block where AsyncLocal is the receiving hub's
        // identity (impersonated). Without re-seeding here, every Subscribe callback
        // would post under the wrong identity. See AsynchronousCalls.md.
        var capturedCtx = accessService.Context;
        var messageId = Guid.NewGuid().AsString();
        // Resolve the target up-front so the pending-callback diagnostic can name
        // which hub we're waiting on. We only run `options(...)` once — Post below
        // gets the same composed PostOptions via WithMessageId chaining.
        var probeOptions = options(new PostOptions(Address));
        var requestType = r?.GetType().Name ?? "<null>";
        var subject = GetOrAddResponseSubject(messageId, requestType, probeOptions.Target);
        Post(r, opts => options(opts).WithMessageId(messageId));
        return RestoreUserContextOnEmission(
            WrapWithCancelOnDispose(
                ApplyTimeout(subject, requestType, probeOptions.Target, messageId),
                messageId, subject),
            capturedCtx);
    }

    private IObservable<IMessageDelivery> ObserveById(string messageId,
        string requestType = "<unknown>",
        Address? target = null)
    {
        var subject = GetOrAddResponseSubject(messageId, requestType, target);
        return WrapWithCancelOnDispose(
            ApplyTimeout(subject, requestType, target, messageId),
            messageId, subject);
    }

    /// <summary>
    /// Wraps a response observable so disposing the downstream subscription removes
    /// the pending callback entry from <see cref="responseSubjects"/>. Without this,
    /// a Subscribe disposed before the response arrives leaves the entry in the
    /// dictionary until <see cref="MessageHubConfiguration.RequestTimeout"/> expires
    /// (~30s). Test bases' quiescing-budget leak check (~0.5s) flags this as a leaked
    /// callback even though the application-level subscription is gone.
    /// </summary>
    private IObservable<IMessageDelivery> WrapWithCancelOnDispose(
        IObservable<IMessageDelivery> source,
        string messageId,
        System.Reactive.Subjects.AsyncSubject<IMessageDelivery> subject)
    {
        return System.Reactive.Linq.Observable.Create<IMessageDelivery>(observer =>
        {
            var sub = source.Subscribe(observer);
            return new System.Reactive.Disposables.CompositeDisposable(
                sub,
                System.Reactive.Disposables.Disposable.Create(() =>
                {
                    lock (responseSubjects)
                    {
                        if (responseSubjects.TryGetValue(messageId, out var entry)
                            && ReferenceEquals(entry.Subject, subject))
                        {
                            responseSubjects.Remove(messageId);
                        }
                    }
                }));
        });
    }

    /// <summary>
    /// Wraps a response observable so each emission re-seeds <see cref="AccessService.Context"/>
    /// on the dispatching thread before downstream Subscribe callbacks run. Without this,
    /// the response arrives on the hub action block (identity = receiving-hub address as
    /// hub-impersonation), and any post made from the Subscribe callback inherits the wrong
    /// identity — surfaces as <c>Access denied: user '&lt;cell-hub-path&gt;' lacks ...</c>.
    /// </summary>
    private IObservable<IMessageDelivery> RestoreUserContextOnEmission(
        IObservable<IMessageDelivery> source, AccessContext? capturedCtx)
    {
        if (capturedCtx is null)
            return source;
        // Restore-on-Finally pattern (2026-05-22): set Context during the
        // subscription so emission-side code (Subscribe callbacks, downstream
        // posts) runs under the captured identity; restore the prior value
        // when the observable completes/errors/disposes. Without this, the
        // captured identity leaked into the caller's AsyncLocal — symptom:
        // McpUpdate tests showed user1's identity used even after
        // LoginWithToken switched to user2, because the earlier user1 call
        // had set Context=user1 on the test thread and never cleared it.
        return Observable.Defer<IMessageDelivery>(() =>
        {
            var prev = accessService.Context;
            accessService.SetContext(capturedCtx);
            return source
                .Do(_ => accessService.SetContext(capturedCtx))
                .Finally(() => accessService.SetContext(prev));
        });
    }

    private IObservable<IMessageDelivery> ApplyTimeout(
        IObservable<IMessageDelivery> source,
        string requestType,
        Address? target,
        string messageId)
        => source.Timeout(Configuration.RequestTimeout,
            System.Reactive.Linq.Observable.Defer<IMessageDelivery>(() =>
                System.Reactive.Linq.Observable.Throw<IMessageDelivery>(
                    new TimeoutException(BuildTimeoutMessage(requestType, target, messageId)))));

    /// <summary>
    /// Names the request that timed out. The previous form said only "No response
    /// received in hub X within 30s" — diagnostics had to walk the call stack to
    /// guess which Observe call timed out. Now the message includes the request
    /// type, target address (the hub we expected to respond), and message id (so
    /// you can grep MESSAGE_FLOW logs for the exact delivery).
    /// </summary>
    private string BuildTimeoutMessage(string requestType, Address? target, string messageId) =>
        $"No response received in hub {Address} within {Configuration.RequestTimeout} " +
        $"for request {requestType} (id={messageId}) → target {target?.ToString() ?? "<unset>"}. " +
        $"The request may have been undeliverable or the target hub was not found.";

    private System.Reactive.Subjects.AsyncSubject<IMessageDelivery> GetOrAddResponseSubject(
        string messageId,
        string requestType = "<unknown>",
        Address? target = null)
    {
        lock (responseSubjects)
        {
            if (RunLevel >= MessageHubRunLevel.ShutDown)
            {
                var disposed = new System.Reactive.Subjects.AsyncSubject<IMessageDelivery>();
                disposed.OnError(new ObjectDisposedException(nameof(MessageHub),
                    $"Hub {Address} is shutting down — cannot register new response subject for {messageId}."));
                return disposed;
            }
            if (!responseSubjects.TryGetValue(messageId, out var entry))
            {
                entry = new PendingCallback(
                    new System.Reactive.Subjects.AsyncSubject<IMessageDelivery>(),
                    requestType,
                    target,
                    Stopwatch.GetTimestamp());
                responseSubjects[messageId] = entry;
                logger.LogDebug("Adding response subject for {Id} (type={Type}, target={Target})",
                    messageId, requestType, target);
            }
            return entry.Subject;
        }
    }


    private IObservable<IMessageDelivery> ExecuteRequest(
        IMessageDelivery delivery,
        CancellationToken cancellationToken
    )
    {
        if (delivery.Message is not ExecutionRequest er)
            return Observable.Return(delivery);
        // Genuinely-async leaf (the caller-supplied Action) → delegate to the
        // pool and replay the result; everything around it stays synchronous.
        return DeliveryObservable.Run(async _ =>
        {
            await er.Action.Invoke(cancellationToken);
            return delivery.Processed();
        });
    }

    /// <summary>
    /// True when <paramref name="delivery"/> is a reply correlated (via
    /// <see cref="PostOptions.RequestId"/>) to a request THIS hub issued and is still
    /// awaiting — i.e. there is a live <see cref="responseSubjects"/> entry for it.
    /// <para>
    /// Used by the init-gate deferral (<c>MessageService</c>) to NEVER defer a reply the
    /// hub is waiting on behind its own initialization gate. Deferring it deadlocks the
    /// hub against its own awaited response — e.g. a data loader that reads a cross-hub
    /// node during <c>DataContextInit</c>: the reply routes back on-target while the gate
    /// is still closed, gets queued, and the gate can't open until the load (waiting on
    /// that reply) completes. This generalises the existing <c>DeliveryFailure</c> bypass
    /// to the SUCCESS reply, which suffers the identical deadlock.
    /// </para>
    /// </summary>
    internal bool IsAwaitedResponse(IMessageDelivery delivery)
    {
        if (!delivery.Properties.TryGetValue(PostOptions.RequestId, out var requestId)
            || requestId?.ToString() is not { Length: > 0 } requestIdString)
            return false;
        lock (responseSubjects)
            return responseSubjects.ContainsKey(requestIdString);
    }

    private IObservable<IMessageDelivery> HandleCallbacks(
        IMessageDelivery delivery,
        CancellationToken cancellationToken
    )
    {
        // Per-response hot path. Cache type name + gate by IsEnabled to skip
        // GetType().Name recomputes and params boxing when logger is off.
        var traceEnabled = logger.IsEnabled(LogLevel.Trace);
        var debugEnabled = logger.IsEnabled(LogLevel.Debug);
        string? messageTypeName = (traceEnabled || debugEnabled) ? delivery.Message.GetType().Name : null;
        if (traceEnabled)
            logger.LogTrace("MESSAGE_FLOW: HUB_HANDLE_CALLBACKS | {MessageType} | Hub: {Address} | MessageId: {MessageId}",
                messageTypeName, Address, delivery.Id);

        if (
            !delivery.Properties.TryGetValue(PostOptions.RequestId, out var requestId)
            || requestId.ToString() is not { } requestIdString
        )
        {
            if (traceEnabled)
                logger.LogTrace("MESSAGE_FLOW: HUB_NO_CALLBACKS | {MessageType} | Hub: {Address} | MessageId: {MessageId}",
                    messageTypeName, Address, delivery.Id);
            return Observable.Return(delivery);
        }

        System.Reactive.Subjects.AsyncSubject<IMessageDelivery> subject;
        lock (responseSubjects)
        {
            if (!responseSubjects.Remove(requestIdString, out var entry))
            {
                if (debugEnabled)
                    logger.LogDebug("No subject found for response message {MessageType} (ID: {MessageId}) - treating as processed",
                        messageTypeName, delivery.Id);
                return Observable.Return(delivery.Processed());
            }
            subject = entry.Subject;
        }

        if (debugEnabled)
            logger.LogDebug("Dispatching response to subject | {MessageType} | Hub: {Address} | MessageId: {MessageId}",
                messageTypeName, Address, delivery.Id);

        if (delivery.Message is DeliveryFailure failure)
        {
            subject.OnError(new DeliveryFailureException(failure));
        }
        else
        {
            subject.OnNext(delivery);
            subject.OnCompleted();
        }

        if (traceEnabled)
            logger.LogTrace("MESSAGE_FLOW: HUB_CALLBACKS_COMPLETE | {MessageType} | Hub: {Address} | MessageId: {MessageId}",
                messageTypeName, Address, delivery.Id);
        // Stamp "a live Observe callback consumed this" so later rules can tell an AWAITED
        // response apart from an un-awaited one. The rule chain keeps running after this rule
        // (HandleCallbacks runs first), and the portal's DeliveryFailure→modal handler must NOT
        // re-surface a failure the call site's OnError already handled (e.g. StartThread's
        // user-partition fallback) — that double-report was the raw "Access denied … lacks
        // Thread permission" modal popping despite the fallback succeeding.
        return Observable.Return(delivery.Processed().SetProperty(PostOptions.CallbackDispatched, true));
    }

    Address IMessageHub.Address => Address;

    /// <summary>
    /// Posts a message into the mesh via the message service for routing/handling, applying optional
    /// delivery options. The side effect is the dispatch itself.
    /// </summary>
    /// <typeparam name="TMessage">The message payload type.</typeparam>
    /// <param name="message">The message payload to send.</param>
    /// <param name="configure">Optional configuration of the delivery (target, sender, response-correlation, message id).</param>
    /// <returns>The created delivery wrapping <paramref name="message"/>, or <c>null</c> if it was not posted.</returns>
    public IMessageDelivery<TMessage>? Post<TMessage>(
        TMessage message,
        Func<PostOptions, PostOptions>? configure = null
    )
    {
        var options = new PostOptions(Address);
        if (configure != null)
            options = configure(options);

        // Per-message hot path. typeof(TMessage).Name is JIT-folded so it's free,
        // but params object[] boxing of options.Target / Sender / result.Id is not.
        var traceEnabled = logger.IsEnabled(LogLevel.Trace);
        if (traceEnabled)
            logger.LogTrace("MESSAGE_FLOW: HUB_POST | {MessageType} | Hub: {Address} | Target: {Target} | Sender: {Sender}",
                typeof(TMessage).Name, Address, options.Target, options.Sender);

        // Log only important messages during disposal
        if (IsDisposing && (message is ShutdownRequest || logger.IsEnabled(LogLevel.Debug)))
        {
            logger.LogDebug("Posting {MessageType} during disposal from {Sender} to {Target} in hub {Address}",
                typeof(TMessage).Name, options.Sender, options.Target, Address);
        }

        var result = (IMessageDelivery<TMessage>?)messageService.Post(message, options);
        if (traceEnabled)
            logger.LogTrace("MESSAGE_FLOW: HUB_POST_RESULT | {MessageType} | Hub: {Address} | MessageId: {MessageId} | Target: {Target}",
                typeof(TMessage).Name, Address, result?.Id, options.Target);
        return result;
    }

    /// <summary>
    /// Inbound entry point: marks <paramref name="delivery"/> as Submitted and routes it onto the
    /// hub's action block for processing. Called by the routing layer, not application code.
    /// </summary>
    /// <param name="delivery">The routed delivery to process on this hub.</param>
    /// <returns>The delivery in its post-routing state.</returns>
    public IMessageDelivery DeliverMessage(IMessageDelivery delivery)
    {
        // Per-inbound hot path. Cache type name + gate by IsEnabled.
        var traceEnabled = logger.IsEnabled(LogLevel.Trace);
        string? messageTypeName = traceEnabled ? delivery.Message.GetType().Name : null;
        if (traceEnabled)
            logger.LogTrace("MESSAGE_FLOW: HUB_DELIVER_MESSAGE | {MessageType} | Hub: {Address} | MessageId: {MessageId}",
                messageTypeName, Address, delivery.Id);
        MessageTrace.Write($"hub={Address} msg={delivery.Message?.GetType().Name} id={delivery.Id} HUB.DeliverMessage ENTER state={delivery.State}");

        var ret = delivery.ChangeState(MessageDeliveryState.Submitted);
        var result = messageService.RouteMessageAsync(ret, default);

        if (traceEnabled)
            logger.LogTrace("MESSAGE_FLOW: HUB_DELIVER_MESSAGE_RESULT | {MessageType} | Hub: {Address} | MessageId: {MessageId} | State: {State}",
                messageTypeName, Address, delivery.Id, result.State);
        MessageTrace.Write($"hub={Address} msg={delivery.Message?.GetType().Name} id={delivery.Id} HUB.DeliverMessage EXIT state={result.State}");
        return result;
    }

    /// <summary>
    /// Resolves (and, depending on <paramref name="create"/>, creates) the hosted child hub at
    /// <paramref name="address"/>, applying <paramref name="config"/> to its configuration when created.
    /// </summary>
    /// <param name="address">The address of the hosted hub.</param>
    /// <param name="config">Transform applied to the hosted hub's configuration when it is created.</param>
    /// <param name="create">Whether to create the hub if it does not yet exist.</param>
    /// <returns>The hosted hub, or <c>null</c> if it does not exist and <paramref name="create"/> is <see cref="HostedHubCreation.Never"/>.</returns>
    public IMessageHub? GetHostedHub(
        Address address,
        Func<MessageHubConfiguration, MessageHubConfiguration> config,
        HostedHubCreation create
    )
    {
        var messageHub = hostedHubs.GetHub(address, config, create);
        return messageHub;
    }

    /// <summary>
    /// Couples a synchronous cleanup to the hub's lifetime by adding it to the hub's composite
    /// disposable; disposed during the ShutDown phase. A registrant added after disposal has begun
    /// is disposed immediately, so late registrations never leak.
    /// </summary>
    /// <param name="disposable">The resource to dispose when the hub shuts down.</param>
    /// <returns>This hub, for chaining.</returns>
    public IMessageHub RegisterForDisposal(IDisposable disposable)
    {
        // Normal subscription logic: hold the IDisposable in the hub's
        // CompositeDisposable. If disposal has already started the composite is
        // disposed and Add disposes the registrant immediately — late registrants
        // never leak. No bag, no imperative drain.
        disposables.Add(disposable);
        return this;
    }

    /// <summary>
    /// Couples a synchronous cleanup callback (receiving this hub) to the hub's lifetime; runs during
    /// the ShutDown phase. Implemented by wrapping the callback in a disposable.
    /// </summary>
    /// <param name="disposeAction">The cleanup to run at shutdown, receiving this hub.</param>
    /// <returns>This hub, for chaining.</returns>
    public IMessageHub RegisterForDisposal(Action<IMessageHub> disposeAction)
        => RegisterForDisposal(System.Reactive.Disposables.Disposable.Create(() => disposeAction(this)));

    /// <summary>
    /// Registers a reactive cleanup returning <see cref="IObservable{T}"/> (Unit) for I/O-performing
    /// teardown. Held in an immutable list, composed into one chain at dispose and subscribed so its
    /// async leaves run on the mesh IO pool.
    /// </summary>
    /// <param name="disposeAction">The reactive cleanup, receiving this hub and returning a Unit observable.</param>
    /// <returns>This hub, for chaining.</returns>
    public IMessageHub RegisterForDisposal(Func<IMessageHub, IObservable<Unit>> disposeAction)
    {
        // Reactive dispose actions are kept in an immutable list (NOT a ConcurrentBag)
        // and composed into one observable chain at dispose. Each returns
        // IObservable<Unit> precisely so it can be chained here and its async leaves
        // run on the mesh IO pool — see DisposeImpl.
        lock (reactiveDisposeLock)
            reactiveDisposeActions = reactiveDisposeActions.Add(disposeAction);
        return this;
    }

    /// <summary>The JSON serialization options used for messages on this hub (built from the parent hub's options).</summary>
    public JsonSerializerOptions JsonSerializerOptions { get; }

    /// <summary>
    /// <c>true</c> from the moment <see cref="Dispose"/> begins. The reactive, Task-free
    /// "is this hub shutting down?" probe; observe <see cref="DisposalCompleted"/> for completion.
    /// </summary>
    public bool IsDisposing => disposalStarted;
    private volatile bool disposalStarted;

    /// <inheritdoc />
    // Native reactive view of the completion subject — NOT bridged from a Task. Fires Unit +
    // completes when disposal finishes (or OnError on a disposal fault); a subscriber attaching
    // AFTER completion still observes the terminal notification immediately (ReplaySubject(1)).
    public IObservable<Unit> DisposalCompleted => disposalCompleted.AsObservable();

    /// <summary>
    /// Set when the Quiescing-phase drain budget (<see cref="QuiesceTimeout"/>)
    /// expires with callbacks still pending. Tests inspect this on the root mesh
    /// (and recursively on hosted hubs via <see cref="GetDisposalDiagnostics"/>)
    /// to fail loud rather than silently swallow leaked Observe subscriptions.
    /// </summary>
    public bool QuiescingTimedOut { get; private set; }
    /// <summary>One-line summary of the pending callbacks at the moment Quiescing
    /// timed out — empty if it didn't fire. Used by the test-base error message.</summary>
    public string? QuiescingTimeoutDetail { get; private set; }

    // Reactive disposal-completion source of truth. ReplaySubject(1): completed exactly once
    // (guarded by `disposalSignalled` CAS) via SignalDisposalCompleted / SignalDisposalFaulted.
    private readonly ReplaySubject<Unit> disposalCompleted = new(1);
    private int disposalSignalled;
    // Disposal-phase Rx subscriptions, held so the scheduler keeps them rooted; each self-
    // completes (Take(1)/Timeout/TakeUntil) and is also disposed in the ShutDown finally.
    private IDisposable? watchdogSubscription;
    private IDisposable? quiescingSubscription;
    private IDisposable? hostedHubsDisposalSubscription;
    private static readonly TimeSpan DisposalWatchdogTimeout = TimeSpan.FromSeconds(8);
    private readonly Stopwatch disposalStopwatch = new();

    private bool DisposalSignalled => Volatile.Read(ref disposalSignalled) != 0;

    /// <summary>Completes <see cref="disposalCompleted"/> exactly once (idempotent CAS).
    /// Reactive replacement for the old <c>disposingTaskCompletionSource.TrySetResult()</c>.</summary>
    private void SignalDisposalCompleted()
    {
        if (Interlocked.CompareExchange(ref disposalSignalled, 1, 0) != 0)
            return;
        disposalCompleted.OnNext(Unit.Default);
        disposalCompleted.OnCompleted();
    }

    /// <summary>Faults <see cref="disposalCompleted"/> exactly once (idempotent CAS).
    /// Reactive replacement for the old <c>disposingTaskCompletionSource.TrySetException(e)</c>.</summary>
    private void SignalDisposalFaulted(Exception error)
    {
        if (Interlocked.CompareExchange(ref disposalSignalled, 1, 0) != 0)
            return;
        disposalCompleted.OnError(error);
    }

    private readonly Lock locker = new();

    /// <summary>
    /// Begins the hub's reactive, phased teardown (idempotent). Cancels any in-flight handler, posts
    /// the Quiescing shutdown request that drives the Quiescing → DisposeHostedHubs → ShutDown state
    /// machine, and arms a watchdog that force-completes disposal if that path ever wedges. Returns
    /// immediately; observe <see cref="DisposalCompleted"/> for completion.
    /// </summary>
    public void Dispose()
    {
        var totalStopwatch = Stopwatch.StartNew();
        lock (locker)
        {
            if (IsDisposing)
            {
                logger.LogDebug("Dispose() called multiple times for hub {address} (elapsed: {elapsed}ms)", Address, totalStopwatch.ElapsedMilliseconds);
                return;
            }
            logger.LogDebug("STARTING DISPOSAL of hub {address}, current Version={Version}, hosted hubs count: {hostedHubsCount}",
                Address, Version, hostedHubs.Hubs.Count());

            disposalStopwatch.Start();
            disposalStarted = true;

        }

        // Close hosted-hub CREATION immediately — not only when the DisposeHostedHubs
        // phase disposes the collection. A hub created in the Quiescing window races
        // DisposeHubsReactive's snapshot and leaks as a zombie whose timers later
        // detonate on the disposed container (post-dispose ObjectDisposedException
        // stragglers). Existing hubs still resolve for the drain.
        hostedHubs.CloseCreation();

        // Log all hosted hubs that will be disposed
        var hostedHubAddresses = hostedHubs.Hubs.Select(h => h.Address.ToString()).ToArray();
        if (hostedHubAddresses.Length > 0)
        {
            logger.LogDebug("Hub {address} has {count} hosted hubs to dispose: [{hubAddresses}]",
                Address, hostedHubAddresses.Length, string.Join(", ", hostedHubAddresses));
        }
        else
        {
            logger.LogDebug("Hub {address} has no hosted hubs to dispose", Address);
        }

        // Cancel any in-progress message handlers (e.g. stuck initialization) to free the
        // execution block so that the ShutdownRequest can be processed immediately.
        // ShutdownRequest uses CancellationToken.None so it won't be affected.
        messageService.CancelExecution();

        logger.LogDebug("POSTING initial ShutdownRequest for hub {Address} with Version={Version} (disposal preparation took {elapsed}ms)",
            Address, Version, totalStopwatch.ElapsedMilliseconds);
        DisposeTrace(Address, "DISPOSE_INVOKED", totalStopwatch.ElapsedMilliseconds,
            $"hostedHubsCount={hostedHubs.Hubs.Count()}");
        Post(new ShutdownRequest(MessageHubRunLevel.Quiescing, Version));

        // Safety net: if the reactive shutdown path ever wedges, force-complete disposal after
        // a timeout so callers (tests, grain deactivation) don't hang forever.
        //
        // 🚨 Reactive, NOT a Task.Delay. `Observable.Timer` schedules on the DefaultScheduler
        // (OFF the action block), and `TakeUntil(disposalCompleted)` cancels the timer the
        // instant disposal finishes — so a normal fast disposal releases the TimerQueue entry
        // immediately. (The original uncancelled `Task.Delay(25s)` rooted the ENTIRE hub graph
        // — cache, data sources, action block, subscriptions — for 25 s after EVERY dispose,
        // even a fast one: TimerQueue → TimerQueueTimer → DelayPromise → state machine → hub.)
        watchdogSubscription = Observable
            .Timer(DisposalWatchdogTimeout)
            .TakeUntil(disposalCompleted)
            .Subscribe(
                _ =>
                {
                    if (DisposalSignalled)
                        return;
                    logger.LogError(
                        "DISPOSAL DEADLOCK DETECTED: Hub {Address} did not complete shutdown within {Timeout}. " +
                        "RunLevel={RunLevel}. Forcing out-of-band teardown so children/subscriptions cannot leak.",
                        Address, DisposalWatchdogTimeout, RunLevel);
                    ForceTeardownAfterWatchdog();
                },
                // disposalCompleted faulting (SignalDisposalFaulted) propagates through TakeUntil;
                // the watchdog is no longer needed — swallow so it isn't an unobserved error.
                _ => { });
    }

    /// <summary>
    /// Last-resort teardown when the phased shutdown state machine never ran: the posted
    /// ShutdownRequest is starved behind a message flood (or a handler wedged the action
    /// block), so Quiescing → DisposeHostedHubs → ShutDown can never advance. Runs the SAME
    /// teardown those phases would have run — hosted hubs, pending callbacks, dispose
    /// actions/subscriptions, message service — from the watchdog thread. Every step is
    /// idempotent, so a rare race with a slow-but-alive phased disposal is harmless.
    ///
    /// 🚨 The predecessor only SIGNALLED completion here (unblocking the caller) and leaked
    /// every child: a dead Blazor circuit's portal hub kept 7k sync-stream hubs alive,
    /// heartbeating and fanning out DataChangedEvents at ~1.2 cores FOREVER — no
    /// UnsubscribeRequest ever reached the owner nodes because the client streams were never
    /// disposed (the 2026-07-01 zombie portal-hub storm; the memory-climbing wedge class).
    /// </summary>
    private void ForceTeardownAfterWatchdog()
    {
        // Past-Started guards (heartbeat self-dispose, hosted-hub creation refusal) key off
        // RunLevel — flip it first so periodic emitters stop feeding the storm.
        lock (locker)
        {
            RunLevel = MessageHubRunLevel.ShutDown;
        }
        try { hostedHubs.Dispose(); }
        catch (Exception e)
        {
            TryLog(LogLevel.Warning, "[FORCE-TEARDOWN] {Address}: hostedHubs.Dispose faulted: {Type}: {Message}",
                Address, e.GetType().Name, e.Message);
        }
        try { CancelCallbacks(); }
        catch (Exception e)
        {
            TryLog(LogLevel.Warning, "[FORCE-TEARDOWN] {Address}: CancelCallbacks faulted: {Type}: {Message}",
                Address, e.GetType().Name, e.Message);
        }
        try { DisposeImpl(); }
        catch (Exception e)
        {
            TryLog(LogLevel.Warning, "[FORCE-TEARDOWN] {Address}: DisposeImpl faulted: {Type}: {Message}",
                Address, e.GetType().Name, e.Message);
        }
        // Stops intake AND the drain pump — the starved queue can no longer burn CPU.
        try { messageService.Dispose(); }
        catch (Exception e)
        {
            TryLog(LogLevel.Warning, "[FORCE-TEARDOWN] {Address}: messageService.Dispose faulted: {Type}: {Message}",
                Address, e.GetType().Name, e.Message);
        }
        // Dead BEFORE signalling — callers awaiting DisposalCompleted must observe the
        // terminal state, never a mid-teardown snapshot.
        lock (locker)
        {
            RunLevel = MessageHubRunLevel.Dead;
        }
        SignalDisposalCompleted();
        disposalStopwatch.Stop();
        quiescingSubscription?.Dispose();
        hostedHubsDisposalSubscription?.Dispose();
        TryLog(LogLevel.Warning, "[FORCE-TEARDOWN] {Address}: out-of-band teardown complete after {Elapsed}ms",
            Address, disposalStopwatch.ElapsedMilliseconds);
    }

    private void DisposeImpl()
    {
        // 1. Fire the REACTIVE dispose actions. Each returns IObservable<Unit>; we
        //    compose them into one chain (Merge) and subscribe — fire-and-forget. Their
        //    genuinely-async leaves (e.g. a final storage flush, a remote unsubscribe)
        //    run on the mesh IO pool, which outlives this hub, so the work completes in
        //    the background. The hub never awaits — nothing on this path is a Task. The
        //    chain is NOT held in `disposables`, so the synchronous teardown below can't
        //    cancel an in-flight pool leaf.
        ImmutableList<Func<IMessageHub, IObservable<Unit>>> reactive;
        lock (reactiveDisposeLock)
        {
            reactive = reactiveDisposeActions;
            reactiveDisposeActions = ImmutableList<Func<IMessageHub, IObservable<Unit>>>.Empty;
        }
        if (!reactive.IsEmpty)
        {
            var legs = reactive.Select(action =>
            {
                try
                {
                    return action(this).Catch<Unit, Exception>(ex =>
                    {
                        TryLog(LogLevel.Warning, "[DISPOSE-ACTION] {Address}: dispose action faulted: {Type}: {Message}",
                            Address, ex.GetType().Name, ex.Message);
                        return Observable.Return(Unit.Default);
                    });
                }
                catch (Exception ex)
                {
                    TryLog(LogLevel.Warning, "[DISPOSE-ACTION] {Address}: dispose action threw synchronously: {Type}: {Message}",
                        Address, ex.GetType().Name, ex.Message);
                    return Observable.Return(Unit.Default);
                }
            });
            // Each leg is Catch-wrapped above, so a fault here is unexpected — log it
            // rather than swallowing (an empty onError hides plumbing bugs in Merge).
            Observable.Merge(legs).Subscribe(
                _ => { },
                ex => TryLog(LogLevel.Warning,
                    "[DISPOSE-ACTION] {Address}: dispose-action merge faulted: {Type}: {Message}",
                    Address, ex.GetType().Name, ex.Message));
        }

        // 2. Synchronous teardown of every registered subscription / Action cleanup —
        //    normal Rx subscription logic, no bag.
        disposables.Dispose();
    }

    /// <summary>
    /// Multi-line snapshot of the hub's disposal state. Reports own RunLevel + disposal
    /// status, the message service's per-buffer counts (so a backlog from a handler that keeps
    /// re-posting shows up as a non-zero queue), and a one-line entry per hosted hub
    /// recursively. Test base classes call this when a dispose timeout fires; the returned
    /// string is meant to land in xUnit test output so the failure says *why* dispose hung
    /// rather than just "operation was canceled".
    /// </summary>
    public string GetDisposalDiagnostics()
    {
        var sb = new System.Text.StringBuilder();
        AppendDiagnostics(sb, depth: 0);
        return sb.ToString();
    }

    /// <summary>
    /// Hard cap on hosted-hub tree recursion. Real hierarchies are at most a few
    /// levels deep; anything beyond this is almost certainly a cycle (e.g. hub A
    /// hosts hub B whose configuration re-creates A). Without this guard the
    /// recursion would stack-overflow the test host on Linux, where the default
    /// stack is smaller than Windows and the unwind silently kills the process
    /// (no error, no trx update — just SIGTERM at the wall-clock cap).
    /// </summary>
    private const int MaxHostedHubRecursionDepth = 32;

    /// <summary>
    /// <c>true</c> if this hub or any hosted hub (recursively) hit the Quiescing-phase timeout — i.e.
    /// had response callbacks still pending when the dispose drain budget elapsed. Tests treat this as
    /// a dispose failure (a leaked subscription that never received its reply).
    /// </summary>
    /// <returns><c>true</c> if any hub in the tree timed out during Quiescing.</returns>
    public bool AnyHubQuiescingTimedOut() => AnyHubQuiescingTimedOut(depth: 0);

    private bool AnyHubQuiescingTimedOut(int depth)
    {
        if (QuiescingTimedOut) return true;
        if (depth >= MaxHostedHubRecursionDepth) return false;
        foreach (var child in hostedHubs.Hubs)
            if (child is MessageHub childMh && childMh.AnyHubQuiescingTimedOut(depth + 1)) return true;
        return false;
    }

    /// <summary>
    /// Builds a concise, indented summary of the hubs (and their pending callbacks) that hit the
    /// Quiescing timeout, for a dispose-failure message. Empty when none timed out.
    /// </summary>
    /// <returns>The multi-line summary, or an empty string when no hub timed out.</returns>
    public string GetQuiescingTimeoutSummary()
    {
        var sb = new System.Text.StringBuilder();
        AppendQuiescingTimeoutSummary(sb, depth: 0);
        return sb.ToString();
    }

    private void AppendQuiescingTimeoutSummary(System.Text.StringBuilder sb, int depth)
    {
        if (depth >= MaxHostedHubRecursionDepth)
        {
            sb.Append(new string(' ', depth * 2))
              .AppendLine("(recursion depth limit reached — possible hosted-hub cycle)");
            return;
        }
        if (QuiescingTimedOut)
        {
            sb.Append(new string(' ', depth * 2))
              .Append("Hub ").Append(Address).Append(": ")
              .AppendLine(QuiescingTimeoutDetail ?? "(no detail captured)");
        }
        foreach (var child in hostedHubs.Hubs)
            if (child is MessageHub childMh)
                childMh.AppendQuiescingTimeoutSummary(sb, depth + 1);
    }

    private void AppendDiagnostics(System.Text.StringBuilder sb, int depth)
    {
        if (depth >= MaxHostedHubRecursionDepth)
        {
            sb.Append(new string(' ', depth * 2))
              .AppendLine("(recursion depth limit reached — possible hosted-hub cycle)");
            return;
        }
        var indent = new string(' ', depth * 2);
        var snapshot = (messageService is MessageService ms)
            ? ms.GetQueueSnapshot()
            : (Buffer: -1, Deferred: -1, Execution: -1, OpenGates: -1, DeliveryCompleted: false,
               CurrentMessage: (string?)null, CurrentMessageElapsedMs: 0L);
        sb.Append(indent)
          .Append("Hub ").Append(Address)
          .Append(" RunLevel=").Append(RunLevel)
          .Append(" Disposal=")
          .Append(!disposalStarted ? "<not started>"
              : DisposalSignalled ? "Completed" : "Pending")
          .Append(" Queue(buffer=").Append(snapshot.Buffer)
          .Append(",deferred=").Append(snapshot.Deferred)
          .Append(",exec=").Append(snapshot.Execution)
          .Append(",openGates=").Append(snapshot.OpenGates)
          .Append(",deliveryActionCompleted=").Append(snapshot.DeliveryCompleted)
          .Append(')');
        if (snapshot.CurrentMessage != null)
        {
            sb.Append(" Executing(")
              .Append(snapshot.CurrentMessage)
              .Append(", ")
              .Append(snapshot.CurrentMessageElapsedMs)
              .Append("ms)");
        }
        // Pending callbacks: same data as the [QUIESCE-START] / [QUIESCE-TIMEOUT] log
        // lines, but folded into the test-base [DISPOSE] snapshot so a 30 s test-base
        // dispose timeout names *what* was outstanding even when structured logs
        // aren't visible to the test author.
        var pending = SnapshotPendingCallbacks();
        if (pending.Length > 0)
            sb.Append(" PendingCallbacks=").Append(pending.Length)
              .Append('[').Append(FormatPendingCallbacks(pending)).Append(']');
        sb.AppendLine();

        var hosted = hostedHubs.Hubs.ToArray();
        if (hosted.Length == 0)
            return;
        sb.Append(indent).Append("HostedHubs (").Append(hosted.Length).Append("):").AppendLine();
        foreach (var child in hosted)
        {
            if (child is MessageHub childMh)
                childMh.AppendDiagnostics(sb, depth + 1);
            else
                sb.Append(indent).Append("  Hub ").Append(child.Address).AppendLine();
        }
    }
    // Fully SYNCHRONOUS. The phase handlers kick off their waits as Rx subscriptions
    // (Observable.Interval / Timer / hostedHubs.DisposalCompleted) and return immediately —
    // nothing on this path awaits, and the action block is never blocked. Completion is
    // signalled through the `disposalCompleted` ReplaySubject.
    private IMessageDelivery HandleShutdownCore(
        IMessageDelivery<ShutdownRequest> request
    )
    {
        var phaseStopwatch = Stopwatch.StartNew();
        shutdownTurnsHandled++;
        logger.LogDebug("STARTING HandleShutdown for hub {Address}, RunLevel={RunLevel}, RequestVersion={RequestVersion}, total disposal time so far: {totalElapsed}ms",
            Address, request.Message.RunLevel, request.Message.Version, disposalStopwatch.ElapsedMilliseconds);

        // Registered cleanups (subscriptions, Action lambdas, pool-bridged disposables)
        // are disposed synchronously later, in the ShutDown phase (DisposeImpl →
        // disposables.Dispose). There is no async dispose-action drain phase.

        // NO version-match gate here. We used to require request.Version == Version - 1
        // (i.e. "this ShutdownRequest is the immediately-next message since it was
        // posted, nothing handled in between") and, on mismatch, REPOST the request with
        // a corrected version. On a busy hub that was a livelock: ++Version runs for
        // EVERY message (HandleMessageAsync), so any concurrent traffic between a repost
        // and its re-handle bumps Version past the one-step window — the gate never
        // converges and instead self-sustains a repost STORM (2,820 ShutdownRequest
        // reposts on a single `consumer/1` hub under the 2-core security tests; 140k
        // ShutdownRequest turns suite-wide, saturating TaskScheduler.Default and timing
        // the project out under the 2-core CI runner). The gate also added NOTHING:
        // duplicates are already handled by the per-phase RunLevel idempotency guards
        // below (Ignored, not reposted), and the three phases are causally chained
        // (Quiescing → DisposeHostedHubs → ShutDown, each posted from the previous
        // phase's completion) and FIFO-ordered, so order is guaranteed without it.
        switch (request.Message.RunLevel)
        {
            case MessageHubRunLevel.Quiescing:
                lock (locker)
                {
                    if (RunLevel >= MessageHubRunLevel.Quiescing)
                    {
                        TryLog(LogLevel.Debug, "[DISPOSE-TRACE] {address}: Quiescing already processed (RunLevel={runLevel}), ignoring",
                            Address, RunLevel);
                        return request.Ignored();
                    }
                    RunLevel = MessageHubRunLevel.Quiescing;
                }

                // Stop the always-on stale-callback scanner so it doesn't fire
                // during the quiesce wait (its warnings would be redundant with
                // [QUIESCE-START]/[QUIESCE-TIMEOUT]). Idempotent dispose.
                staleCallbackScannerSub?.Dispose();
                staleCallbackScannerSub = null;

                var initialPendingSnapshot = SnapshotPendingCallbacks();
                TryLog(LogLevel.Information,
                    "[QUIESCE-START] {Address}: {Count} pending callbacks at dispose entry: {Pending}",
                    Address, initialPendingSnapshot.Length, FormatPendingCallbacks(initialPendingSnapshot));
                DisposeTrace(Address, "QUIESCE_START", disposalStopwatch.ElapsedMilliseconds,
                    $"pending={initialPendingSnapshot.Length}");

                // CRITICAL: do the wait OFF the action block. The action block processes
                // messages serially (MaxDegreeOfParallelism = 1) — a blocking wait on the
                // action-block thread would stop dequeuing the very response messages we're
                // waiting to drain (a self-deadlock → guaranteed QuiesceTimeout).
                //
                // Reactive poll: `Observable.Interval` ticks on the DefaultScheduler (off the
                // action block); each tick checks whether `responseSubjects` has drained. The
                // leading StartWith(-1) probes ONCE inline (so an already-drained hub advances
                // without a scheduler hop). `Amb` races the drain signal against a single
                // `Observable.Timer(QuiesceTimeout)` deadline and takes whichever fires first —
                // drained → true, budget exceeded → false. (Note: a between-emissions
                // `.Timeout` would NOT work here — the interval emits every QuiescePollInterval,
                // so the inter-emission gap never reaches QuiesceTimeout; the deadline must be a
                // separate total-duration timer.) The result funnels into OnQuiesceComplete,
                // which posts DisposeHostedHubs. No Task.Delay, no await — the handler returns
                // immediately and the action block stays free.
                var quiesceSw = Stopwatch.StartNew();
                var drained = Observable
                    .Interval(QuiescePollInterval)
                    .StartWith(-1L)
                    .Select(_ => { lock (responseSubjects) return responseSubjects.Count == 0; })
                    .Where(empty => empty)
                    .Take(1)
                    .Select(_ => true);
                var quiesceDeadline = Observable.Timer(QuiesceTimeout).Select(_ => false);
                quiescingSubscription = drained
                    .Amb(quiesceDeadline)
                    .Take(1)
                    .Subscribe(
                        drainedOk => OnQuiesceComplete(drainedOk, quiesceSw, initialPendingSnapshot),
                        _ => OnQuiesceComplete(drainedOk: false, quiesceSw, initialPendingSnapshot));
                break;
            case MessageHubRunLevel.DisposeHostedHubs:
                var disposeHostedHubsStopwatch = Stopwatch.StartNew();
                lock (locker)
                {
                    if (RunLevel == MessageHubRunLevel.DisposeHostedHubs)
                    {
                        TryLog(LogLevel.Warning,
                            "DisposeHostedHubs already processed for hub {Address}, ignoring (phase time: {elapsed}ms)",
                            Address, phaseStopwatch.ElapsedMilliseconds);
                        return request.Ignored();
                    }

                    RunLevel = MessageHubRunLevel.DisposeHostedHubs;
                }

                DisposeTrace(Address, "HOSTED_DISPOSE_START", disposalStopwatch.ElapsedMilliseconds,
                    $"hostedHubsCount={hostedHubs.Hubs.Count()}");
                // Dispose the hosted hubs (each disposes synchronously) and OBSERVE their
                // collective completion via the collection's `DisposalCompleted` observable —
                // no `await hostedHubs.Disposal`, no Task.Run. Each child hub completes its own
                // reactive disposal and the collection completes once all have. On completion
                // OR error (the collection caps the wait internally), advance to ShutDown.
                hostedHubs.Dispose();
                var hostedSw = disposeHostedHubsStopwatch;
                hostedHubsDisposalSubscription = hostedHubs.DisposalCompleted
                    .Take(1)
                    .Subscribe(
                        _ => { },
                        ex =>
                        {
                            DisposeTrace(Address, "HOSTED_DISPOSE_ERROR", hostedSw.ElapsedMilliseconds,
                                $"{ex.GetType().Name}: {ex.Message}");
                            PostShutDownPhase(hostedSw);
                        },
                        () =>
                        {
                            DisposeTrace(Address, "HOSTED_DISPOSE_OK", hostedSw.ElapsedMilliseconds);
                            PostShutDownPhase(hostedSw);
                        });
                break;
            case MessageHubRunLevel.ShutDown:
                var shutdownStopwatch = Stopwatch.StartNew();
                try
                {
                    lock (locker)
                    {
                        if (RunLevel == MessageHubRunLevel.ShutDown)
                        {
                            logger.LogDebug("[DISPOSE-TRACE] {address}: ShutDown already processed, ignoring", Address);
                            return request.Ignored();
                        }

                        logger.LogDebug("[DISPOSE-TRACE] {address}: STARTING ShutDown phase", Address);
                        RunLevel = MessageHubRunLevel.ShutDown;
                    }

                    CancelCallbacks();
                    DisposeImpl();

                    logger.LogDebug("[DISPOSE-TRACE] {address}: messageService.Dispose() (sync)...", Address);
                    messageService.Dispose();
                    logger.LogDebug("[DISPOSE-TRACE] {address}: messageService.Dispose() done in {elapsed}ms",
                        Address, shutdownStopwatch.ElapsedMilliseconds);

                    // Signal completion through the reactive source (idempotent CAS — no
                    // InvalidOperationException to guard, unlike TaskCompletionSource).
                    SignalDisposalCompleted();
                    logger.LogDebug("[DISPOSE-TRACE] {address}: Disposal COMPLETED in {elapsed}ms total",
                        Address, disposalStopwatch.ElapsedMilliseconds);
                }
                catch (Exception e)
                {
                    logger.LogError("Error during shutdown of hub {address} after {elapsed}ms (total disposal time: {totalElapsed}ms): {exception}",
                        Address, shutdownStopwatch.ElapsedMilliseconds, disposalStopwatch.ElapsedMilliseconds, e);
                    SignalDisposalFaulted(e);
                }
                finally
                {
                    RunLevel = MessageHubRunLevel.Dead;
                    disposalStopwatch.Stop();
                    // Tidy the disposal-phase subscriptions (each has already self-completed:
                    // the watchdog cancelled via TakeUntil when SignalDisposalCompleted fired,
                    // the quiescing/dispose-action/hosted-hub subscriptions completed before
                    // posting onward).
                    watchdogSubscription?.Dispose();
                    quiescingSubscription?.Dispose();
                    hostedHubsDisposalSubscription?.Dispose();
                    //await ((IAsyncDisposable)ServiceProvider).DisposeAsync();
                    // Use parentAddress captured at construction — Configuration.ParentHub
                    // re-resolves from ParentServiceProvider, which is often disposed by the
                    // time we get here, throwing ObjectDisposedException that pollutes test
                    // logs. Never call DI from a disposal path.
                    logger.LogDebug("Finished shutdown of hub {address} with parent {parent} - final phase took {elapsed}ms, total disposal time: {totalElapsed}ms",
                        Address, parentAddress, phaseStopwatch.ElapsedMilliseconds, disposalStopwatch.ElapsedMilliseconds);
                }

                break;
        }

        return request.Processed();
    }

    /// <summary>
    /// Terminal step of the reactive Quiescing poll (see the Quiescing branch of
    /// <see cref="HandleShutdownCore"/>). Runs on the poll's scheduler thread when the
    /// response subjects drain (<paramref name="drainedOk"/> = true) or the
    /// <see cref="QuiesceTimeout"/> elapses (false). Logs the outcome, marks
    /// <see cref="QuiescingTimedOut"/> on timeout, then advances the state machine by posting
    /// the DisposeHostedHubs phase. Never throws — a wedged state machine is worse than a hang.
    /// </summary>
    private void OnQuiesceComplete(
        bool drainedOk,
        Stopwatch quiesceSw,
        (string MessageId, string RequestType, Address? Target, long AgeMs)[] initialPendingSnapshot)
    {
        try
        {
            if (drainedOk)
            {
                TryLog(LogLevel.Information,
                    "[QUIESCE-OK] {Address}: drained {Count} callback(s) in {Elapsed}ms",
                    Address, initialPendingSnapshot.Length, quiesceSw.ElapsedMilliseconds);
                DisposeTrace(Address, "QUIESCE_OK", quiesceSw.ElapsedMilliseconds,
                    $"drained={initialPendingSnapshot.Length}");
            }
            else
            {
                var stuck = SnapshotPendingCallbacks();
                var detail = FormatPendingCallbacks(stuck);
                TryLog(LogLevel.Warning,
                    "[QUIESCE-TIMEOUT] {Address}: {Count} callback(s) still pending after {Timeout}s — forcibly cancelling. Pending: {Pending}",
                    Address, stuck.Length, QuiesceTimeout.TotalSeconds, detail);
                DisposeTrace(Address, "QUIESCE_TIMEOUT", quiesceSw.ElapsedMilliseconds,
                    $"pending={stuck.Length}|{detail}");
                // Sticky flag — tests recursively inspect this and treat any hub with
                // QuiescingTimedOut=true as a dispose failure. Forces visibility on leaked
                // Observe subscriptions instead of silently extending dispose budgets.
                QuiescingTimedOut = true;
                QuiescingTimeoutDetail = $"{stuck.Length} pending callback(s) after {QuiesceTimeout.TotalSeconds:F2}s: {detail}";
                try { CancelCallbacks(); }
                catch (Exception cancelEx)
                {
                    TryLog(LogLevel.Warning, "[QUIESCE-TIMEOUT] {Address}: CancelCallbacks threw {Type}: {Message}",
                        Address, cancelEx.GetType().Name, cancelEx.Message);
                }
            }
        }
        catch (Exception quiesceEx)
        {
            // Never let this branch throw — that would wedge the dispose state machine at
            // Quiescing forever (worse than the original hang). Log best-effort and advance.
            TryLog(LogLevel.Error,
                "[QUIESCE-ERROR] {Address}: unexpected exception {Type}: {Message}; proceeding to DisposeHostedHubs anyway.",
                Address, quiesceEx.GetType().Name, quiesceEx.Message);
        }
        finally
        {
            // Advance to DisposeHostedHubs. Registered subscriptions are disposed
            // synchronously later, in the ShutDown phase (DisposeImpl →
            // disposables.Dispose) — there is no async dispose-action drain to await.
            Post(new ShutdownRequest(MessageHubRunLevel.DisposeHostedHubs, Version));
        }
    }

    /// <summary>
    /// Posts the ShutDown phase once the hosted hubs have drained. Wrapped so that a failed
    /// Post (hub in an unexpected state) still force-faults disposal rather than wedging the
    /// state machine — subscribers to <see cref="DisposalCompleted"/> never hang.
    /// </summary>
    private void PostShutDownPhase(Stopwatch sw)
    {
        try
        {
            TryLog(LogLevel.Debug, "[DISPOSE-TRACE] {address}: POSTING ShutDown request, Version={version}",
                Address, Version);
            Post(new ShutdownRequest(MessageHubRunLevel.ShutDown, Version));
            DisposeTrace(Address, "POSTED_SHUTDOWN", sw.ElapsedMilliseconds);
        }
        catch (Exception postEx)
        {
            DisposeTrace(Address, "POSTED_SHUTDOWN_FAILED", sw.ElapsedMilliseconds,
                $"{postEx.GetType().Name}: {postEx.Message}");
            SignalDisposalFaulted(postEx);
        }
    }

    /// <summary>
    /// Best-effort logger call that swallows any exception. The dispose pipeline
    /// runs while DI scope / logger may be partially torn down (e.g. tests dispose
    /// the service provider before the hub's disposal task completes); a logger
    /// call that throws during dispose would otherwise wedge the state machine.
    /// Field <see cref="logger"/> is non-nullable but the underlying logger
    /// factory may already be disposed — the null-conditional + catch is what
    /// keeps the state machine progressing in that case.
    /// </summary>
    private void TryLog(LogLevel level, string message, params object?[] args)
    {
        try
        {
            logger?.Log(level, message, args);
        }
        catch
        {
            // Swallow — diagnostic logging must never throw out of the dispose path.
        }
    }

    private void CancelCallbacks()
    {
        // Push ObjectDisposedException to all pending response subjects so anyone
        // currently subscribed gets onError instead of waiting forever.
        PendingCallback[] pending;
        lock (responseSubjects)
        {
            pending = responseSubjects.Values.ToArray();
            responseSubjects.Clear();
        }
        logger.LogDebug("Cancelling {SubjectCount} pending response subjects during disposal for hub {Address}",
            pending.Length, Address);

        foreach (var entry in pending)
        {
            try
            {
                entry.Subject.OnError(new ObjectDisposedException(nameof(MessageHub),
                    $"Hub {Address} was disposed before the response arrived (request type {entry.RequestType}, target {entry.Target})."));
            }
            catch
            {
                // Subject may already be terminated — ignore.
            }
        }
    }

    /// <summary>
    /// Snapshot of currently-pending response callbacks. Used by the Quiescing dispose
    /// phase and by <see cref="GetDisposalDiagnostics"/> so a hung dispose names *what*
    /// the hub was waiting on, not just that it was waiting.
    /// </summary>
    private (string MessageId, string RequestType, Address? Target, long AgeMs)[] SnapshotPendingCallbacks()
    {
        lock (responseSubjects)
        {
            var nowTicks = Stopwatch.GetTimestamp();
            return responseSubjects
                .Select(kv => (
                    kv.Key,
                    kv.Value.RequestType,
                    kv.Value.Target,
                    (long)((nowTicks - kv.Value.RegisteredAtTicks) * 1000.0 / Stopwatch.Frequency)))
                .ToArray();
        }
    }

    private const int PendingCallbackLogCap = 20;

    private static string FormatPendingCallbacks(
        (string MessageId, string RequestType, Address? Target, long AgeMs)[] pending)
    {
        if (pending.Length == 0)
            return "<none>";
        // Cap individual-callback enumeration — a stuck hub with 995 outstanding
        // DataChangeRequests was emitting a single ~100KB log line that broke
        // downstream TRX parsers (`xmlSAX2Characters: huge text node`). The first
        // few + a per-(RequestType,Target) tally is enough to diagnose; anything
        // beyond is noise that drowns the rest of the log.
        if (pending.Length <= PendingCallbackLogCap)
        {
            return string.Join(", ", pending.Select(p =>
                $"{p.MessageId}={p.RequestType}@{p.Target}({p.AgeMs}ms)"));
        }
        var head = string.Join(", ", pending.Take(PendingCallbackLogCap).Select(p =>
            $"{p.MessageId}={p.RequestType}@{p.Target}({p.AgeMs}ms)"));
        var rest = pending.Skip(PendingCallbackLogCap)
            .GroupBy(p => $"{p.RequestType}@{p.Target}")
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key}×{g.Count()}");
        return $"{head}, …+{pending.Length - PendingCallbackLogCap} more [{string.Join(", ", rest)}]";
    }

    /// <summary>
    /// Per-hub Quiescing-phase budget. Configured via
    /// <see cref="MessageHubConfiguration.WithQuiesceTimeout"/>; defaults to 2 s.
    /// Tests with deliberately abandoned <c>Observe(...)</c> subscriptions should
    /// drop this to ~100-500 ms.
    /// </summary>
    private TimeSpan QuiesceTimeout => Configuration.QuiesceTimeout;
    private static readonly TimeSpan QuiescePollInterval = TimeSpan.FromMilliseconds(50);


    // Every SYNCHRONOUS registered cleanup — subscriptions, Action<IMessageHub>
    // lambdas, the pool-bridged IDisposable a routing service hands back — lives here.
    // Disposed SYNCHRONOUSLY in the ShutDown phase (DisposeImpl). This is "normal
    // subscription logic": no ConcurrentBag of callbacks, no imperative drain. A
    // CompositeDisposable also disposes any registrant added after it is itself
    // disposed, so late registrations during teardown can't leak.
    private readonly System.Reactive.Disposables.CompositeDisposable disposables = new();

    // REACTIVE dispose actions (Func returning IObservable<Unit>) — composed into one
    // chain and subscribed at dispose so their async leaves run on the mesh IO pool
    // (see DisposeImpl). Immutable list, NOT a ConcurrentBag; mutated under a lock.
    private ImmutableList<Func<IMessageHub, IObservable<Unit>>> reactiveDisposeActions =
        ImmutableList<Func<IMessageHub, IObservable<Unit>>>.Empty;
    private readonly object reactiveDisposeLock = new();



    private readonly ConcurrentDictionary<(string Conext, Type Type), object?> properties = new();

    /// <summary>
    /// Stores a value in the per-hub property bag, keyed by (<paramref name="context"/>, typeof(T)).
    /// Caches instance state on the hub without a static dictionary; the entry lives with the hub.
    /// </summary>
    /// <typeparam name="T">The value type, part of the bag key.</typeparam>
    /// <param name="obj">The value to store.</param>
    /// <param name="context">An optional discriminator allowing multiple entries of the same type.</param>
    public void Set<T>(T obj, string context = "")
    {
        properties[(context, typeof(T))] = obj;
    }

    /// <summary>
    /// Reads the value previously stored via <see cref="Set{T}"/> for (<paramref name="context"/>, typeof(T)).
    /// </summary>
    /// <typeparam name="T">The value type, part of the bag key.</typeparam>
    /// <param name="context">The discriminator used when the value was stored.</param>
    /// <returns>The stored value, or <c>default</c> when no entry exists.</returns>
    public T Get<T>(string context = "")
    {
        properties.TryGetValue((context, typeof(T)), out var ret);
        return (T)ret!;
    }


    private IMessageDelivery HandleDispose(IMessageDelivery<DisposeRequest> request)
    {
        // 🚨 The root mesh hub (Address.Type == "mesh") is an irreplaceable,
        // process-lifetime DI singleton — built ONCE via AddSingleton(BuildHub) and
        // never rebuilt. DisposeRequest is a [SystemMessage] with NO permission gate,
        // so ANY sender (including an unauthenticated external / RawJson client routed
        // to mesh/<id>) could otherwise tear the whole mesh down: once disposed, every
        // node operation times out at 60 s forever until the process restarts — the
        // atioz mesh-wide outage on 2026-06-10. The host owns this hub's lifecycle and
        // disposes it via a DIRECT Dispose() call (MeshTeardownExtensions.TeardownAsync
        // on host shutdown), NEVER through the message bus — so refusing the message
        // path here cannot block a legitimate shutdown. Per-node / portal / client hubs
        // stay message-disposable (recycle, circuit teardown).
        if (Address.Type == AddressExtensions.MeshType)
        {
            logger.LogWarning(
                "Refused a message-routed DisposeRequest targeting the root mesh hub {Address} (sender={Sender}). " +
                "The mesh hub's lifecycle is owned by host teardown, not the message bus.",
                Address, request.Sender);
            return request.Ignored();
        }
        Dispose();
        return request.Processed();
    }


    #region Registry
    /// <summary>Registers a synchronous handler for messages of type <typeparamref name="TMessage"/> (no filter).</summary>
    /// <typeparam name="TMessage">The message type the handler processes.</typeparam>
    /// <param name="action">The synchronous delivery handler.</param>
    /// <returns>A disposable that unregisters the handler.</returns>
    public IDisposable Register<TMessage>(SyncDelivery<TMessage> action) =>
        Register(action, _ => true);

    /// <summary>Registers an asynchronous (observable-returning) handler for messages of type <typeparamref name="TMessage"/> (no filter).</summary>
    /// <typeparam name="TMessage">The message type the handler processes.</typeparam>
    /// <param name="action">The reactive delivery handler.</param>
    /// <returns>A disposable that unregisters the handler.</returns>
    public IDisposable Register<TMessage>(AsyncDelivery<TMessage> action) =>
        Register(action, _ => true);

    /// <summary>Registers a synchronous handler for messages of type <typeparamref name="TMessage"/> that pass <paramref name="filter"/>.</summary>
    /// <typeparam name="TMessage">The message type the handler processes.</typeparam>
    /// <param name="action">The synchronous delivery handler.</param>
    /// <param name="filter">Predicate selecting which deliveries the handler receives.</param>
    /// <returns>A disposable that unregisters the handler.</returns>
    public IDisposable Register<TMessage>(
        SyncDelivery<TMessage> action,
        DeliveryFilter<TMessage> filter
    )
    {
        return Register((d, _) => Observable.Return(action(d)), filter);
    }

    /// <summary>
    /// Registers an asynchronous handler that is INHERITED — appended to the END of the rule chain (so
    /// it runs after the hub's own rules), passing through deliveries that don't match
    /// <typeparamref name="TMessage"/> or the optional <paramref name="filter"/>.
    /// </summary>
    /// <typeparam name="TMessage">The message type the handler processes.</typeparam>
    /// <param name="action">The reactive delivery handler.</param>
    /// <param name="filter">Optional predicate selecting which deliveries the handler receives; <c>null</c> matches all of the type.</param>
    /// <returns>A disposable that unregisters the handler.</returns>
    public IDisposable RegisterInherited<TMessage>(
        AsyncDelivery<TMessage> action,
        DeliveryFilter<TMessage>? filter = null
    )
    {
        var node = new LinkedListNode<AsyncDelivery>(
            (d, c) =>
                d is IMessageDelivery<TMessage> md && (filter?.Invoke(md) ?? true)
                    ? action(md, c)
                    : Observable.Return(d)
        );
        rules.AddLast(node);
        return new AnonymousDisposable(() => rules.Remove(node));
    }

    /// <summary>
    /// Registers a non-generic synchronous handler that receives every delivery (it inspects the
    /// message type itself).
    /// </summary>
    /// <param name="delivery">The synchronous delivery handler.</param>
    /// <returns>A disposable that unregisters the handler.</returns>
    public IDisposable Register(SyncDelivery delivery) =>
        Register((d, _) => Observable.Return(delivery(d)));

    /// <summary>
    /// Registers a non-generic asynchronous handler at the FRONT of the rule chain; it receives every
    /// delivery and returns an observable of the transformed delivery.
    /// </summary>
    /// <param name="delivery">The reactive delivery handler.</param>
    /// <returns>A disposable that unregisters the handler.</returns>
    public IDisposable Register(AsyncDelivery delivery)
    {
        var node = new LinkedListNode<AsyncDelivery>(delivery);
        rules.AddFirst(node);
        return new AnonymousDisposable(() => rules.Remove(node));
    }

    /// <summary>
    /// Synchronous overload of the inherited registration: registers an end-of-chain handler for
    /// messages of type <typeparamref name="TMessage"/> matching the optional <paramref name="filter"/>.
    /// </summary>
    /// <typeparam name="TMessage">The message type the handler processes.</typeparam>
    /// <param name="action">The synchronous delivery handler.</param>
    /// <param name="filter">Optional predicate selecting which deliveries the handler receives; <c>null</c> matches all of the type.</param>
    /// <returns>A disposable that unregisters the handler.</returns>
    public IDisposable RegisterInherited<TMessage>(
        SyncDelivery<TMessage> action,
        DeliveryFilter<TMessage>? filter = null
    ) => RegisterInherited((d, _) => Observable.Return(action(d)), filter);

    /// <summary>
    /// Registers an asynchronous handler for messages of type <typeparamref name="TMessage"/> that
    /// target this hub's address and pass <paramref name="filter"/>. Also registers the message type
    /// (and related types) in the type registry.
    /// </summary>
    /// <typeparam name="TMessage">The message type the handler processes.</typeparam>
    /// <param name="action">The reactive delivery handler.</param>
    /// <param name="filter">Predicate selecting which deliveries the handler receives.</param>
    /// <returns>A disposable that unregisters the handler.</returns>
    public IDisposable Register<TMessage>(
        AsyncDelivery<TMessage> action,
        DeliveryFilter<TMessage> filter
    )
    {
        WithTypeAndRelatedTypesFor(typeof(TMessage));
        return Register(
            (d, c) => action((IMessageDelivery<TMessage>)d, c),
            d =>
            {
                // Compare without Host since Host tracks routing path
                var targetWithoutHost = d.Target is not null ? d.Target with { Host = null } : null;
                return (targetWithoutHost == null || Address.Equals(targetWithoutHost)) && d is IMessageDelivery<TMessage> md && filter(md);
            }
        );
    }

    /// <summary>
    /// Registers a non-generic asynchronous handler for messages assignable to <paramref name="tMessage"/>,
    /// registering that type in the type registry.
    /// </summary>
    /// <param name="tMessage">The message type (by runtime <see cref="Type"/>) the handler processes.</param>
    /// <param name="action">The reactive delivery handler.</param>
    /// <returns>A disposable that unregisters the handler.</returns>
    public IDisposable Register(Type tMessage, AsyncDelivery action)
    {
        WithTypeAndRelatedTypesFor(tMessage);
        return Register(action, d => tMessage.IsInstanceOfType(d.Message));
    }

    /// <summary>
    /// Registers a non-generic asynchronous handler at the FRONT of the rule chain, invoked only for
    /// deliveries that pass <paramref name="filter"/> (non-matching deliveries pass through unchanged).
    /// </summary>
    /// <param name="action">The reactive delivery handler.</param>
    /// <param name="filter">Predicate selecting which deliveries the handler receives.</param>
    /// <returns>A disposable that unregisters the handler.</returns>
    public IDisposable Register(AsyncDelivery action, DeliveryFilter filter)
    {
        IObservable<IMessageDelivery> Rule
            (IMessageDelivery delivery, CancellationToken cancellationToken)
            => WrapFilter(delivery, action, filter, cancellationToken);
        var node = new LinkedListNode<AsyncDelivery>(Rule);
        rules.AddFirst(node);
        return new AnonymousDisposable(() =>
        {
            rules.Remove(node);
        });
    }

    private IObservable<IMessageDelivery> WrapFilter(
        IMessageDelivery delivery,
        AsyncDelivery action,
        DeliveryFilter filter,
        CancellationToken cancellationToken
    )
    {
        if (filter(delivery))
            return action(delivery, cancellationToken);
        return Observable.Return(delivery);
    }

    /// <summary>
    /// Registers a non-generic synchronous handler for messages assignable to <paramref name="tMessage"/> (no filter).
    /// </summary>
    /// <param name="tMessage">The message type (by runtime <see cref="Type"/>) the handler processes.</param>
    /// <param name="action">The synchronous delivery handler.</param>
    /// <returns>A disposable that unregisters the handler.</returns>
    public IDisposable Register(Type tMessage, SyncDelivery action) =>
        Register(tMessage, action, _ => true);

    /// <summary>
    /// Registers a non-generic synchronous handler for messages assignable to <paramref name="tMessage"/>
    /// that also pass <paramref name="filter"/>.
    /// </summary>
    /// <param name="tMessage">The message type (by runtime <see cref="Type"/>) the handler processes.</param>
    /// <param name="action">The synchronous delivery handler.</param>
    /// <param name="filter">Predicate selecting which deliveries the handler receives.</param>
    /// <returns>A disposable that unregisters the handler.</returns>
    public IDisposable Register(Type tMessage, SyncDelivery action, DeliveryFilter filter) =>
        Register(
            tMessage,
            (d, _) =>
            {
                d = action(d);
                return Observable.Return(d);
            },
            filter
        );


    /// <summary>
    /// Registers a non-generic asynchronous handler for messages assignable to <paramref name="tMessage"/>
    /// that also pass <paramref name="filter"/>, registering the type (and related types) in the type registry.
    /// </summary>
    /// <param name="tMessage">The message type (by runtime <see cref="Type"/>) the handler processes.</param>
    /// <param name="action">The reactive delivery handler.</param>
    /// <param name="filter">Predicate selecting which deliveries the handler receives.</param>
    /// <returns>A disposable that unregisters the handler.</returns>
    public IDisposable Register(Type tMessage, AsyncDelivery action, DeliveryFilter filter)
    {
        WithTypeAndRelatedTypesFor(tMessage);
        return Register(
            (d, c) => action(d, c),
            d => tMessage.IsInstanceOfType(d.Message) && filter(d)
        );
    }
    #endregion
}
