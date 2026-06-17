using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Domain;
using MeshWeaver.Messaging.Serialization;
using MeshWeaver.ServiceProvider;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Messaging;

public record MessageHubConfiguration
{
    public const string InitializeGateName = "Initialize";

    public Address Address { get; }
    protected readonly IServiceProvider? ParentServiceProvider;
    public MessageHubConfiguration(IServiceProvider? parentServiceProvider, Address address)
    {
        Address = address;
        ParentServiceProvider = parentServiceProvider;
        TypeRegistry = new TypeRegistry(ParentServiceProvider?.GetService<ITypeRegistry>()).WithType(address.GetType());
        PostPipeline = [UserServicePostPipeline];
        DeliveryPipeline = [UserServiceDeliveryPipeline];
    }

    /// <summary>
    /// Named initialization gates that are created during hub initialization and can be opened by name.
    /// The key is the gate name, the value is the predicate that determines which messages are allowed during initialization.
    /// All other messages are deferred until the gate is opened.
    /// The Initialize gate doesn't allow any additional messages - it's just a marker for when BuildupActions complete.
    /// </summary>
    internal ImmutableDictionary<string, Predicate<IMessageDelivery>> InitializationGates { get; init; } = ImmutableDictionary<string, Predicate<IMessageDelivery>>.Empty
        .Add(InitializeGateName, d => d.Message is InitializeHubRequest); // Initialize gate doesn't allow any messages - just marks completion of BuildupActions

    /// <summary>
    /// Adds a named initialization gate that will be created during hub initialization.
    /// This ensures the gate is in place before any messages are processed.
    /// Only messages matching the predicate will be allowed through during initialization.
    /// All other messages will be deferred until the gate is opened via OpenGate().
    /// </summary>
    /// <param name="name">Unique name for this initialization gate</param>
    /// <param name="allowDuringInit">Predicate that determines which messages are allowed during initialization (e.g. InitializeHubRequest, SetCurrentRequest)</param>
    /// <returns>Updated configuration</returns>
    public MessageHubConfiguration WithInitializationGate(string name, Predicate<IMessageDelivery>? allowDuringInit = null)
        => this with { InitializationGates = InitializationGates.SetItem(name, allowDuringInit ?? (_ => false)) };

    // Cache the resolved parent hub. Without this, every access went through an Autofac
    // `GetService<IMessageHub>()` resolve — and DataExtensions.RouteStreamMessage reads
    // ParentHub on EVERY routed stream message, so under a stream-message storm the
    // per-message DI resolve dominated CPU (the 155%-CPU routing hot frame in the
    // 2026-06-11 thread-execution wedge: RouteStreamMessage → get_ParentHub → GetService).
    // The parent hub never changes for a given configuration, so resolve once and reuse.
    // (Proven safe via the AccessContext-canary cache-revert diagnostic: the canary fails
    // identically with or without this cache — the cache is not the cause.)
    private IMessageHub? _parentHub;

    /// <summary>
    /// The parent hub, resolved once via DI on the parent scope and cached thereafter.
    /// Do NOT call during disposal before it has been resolved — the parent scope may already
    /// be disposed, and an ObjectDisposedException here pollutes test output. If you need
    /// the parent hub at disposal time, capture it at construction (e.g.
    /// <see cref="MessageService.ParentHub"/>).
    /// </summary>
    public IMessageHub? ParentHub => _parentHub ??= ParentServiceProvider?.GetService<IMessageHub>();

    /// <summary>
    /// TaskScheduler used by this hub's message-dispatch ActionBlocks. Each hub is
    /// an actor and gets its own scheduler so async continuations don't serialise
    /// through some other hub's single thread.
    ///
    /// <para>
    /// <b>Default behaviour:</b> when unset, the hub uses <see cref="TaskScheduler.Default"/>
    /// (the thread pool). The Orleans grain glue (<c>MessageHubGrain</c>) sets this
    /// explicitly to the grain's scheduler for the root grain hub so Orleans can
    /// attribute work to the grain (keep-alive, statistics, RequestContext flow).
    /// Hosted hubs created via <c>GetHostedHub(...)</c> default to <c>TaskScheduler.Default</c> —
    /// they are sibling actors, not extensions of the parent.
    /// </para>
    ///
    /// <para>See <c>Doc/Architecture/OrleansTaskScheduler.md</c> for the threading model.</para>
    /// </summary>
    public TaskScheduler? TaskScheduler { get; init; }

    /// <summary>
    /// Sets the <see cref="TaskScheduler"/> this hub's ActionBlocks run on.
    /// Use the grain's scheduler ONLY for the root grain hub; every other hub
    /// (hosted hubs, per-node hubs, _Exec, kernel hubs) should use a separate
    /// scheduler — usually leave it unset so the default <see cref="System.Threading.Tasks.TaskScheduler.Default"/>
    /// applies. See <c>Doc/Architecture/OrleansTaskScheduler.md</c>.
    /// </summary>
    public MessageHubConfiguration WithTaskScheduler(TaskScheduler scheduler)
        => this with { TaskScheduler = scheduler };

    /// <summary>
    /// Declares UNDER WHICH IDENTITY this hub posts — the never-null AccessContext
    /// invariant made an explicit, per-hub configuration decision rather than a
    /// per-callsite concern (<c>feedback_access_context_always_set</c>). See
    /// <see cref="PostingIdentity"/> for the three-source contract.
    ///
    /// <para>Default <see cref="PostingIdentity.User"/>: the hub posts as the user and
    /// is UNHAPPY (logs an error + fails the delivery) when it posts a non-exempt
    /// application message with no ambient user context. Set
    /// <see cref="PostingIdentity.System"/> for framework infrastructure (routing,
    /// persistence) whose own posts run as System automatically.</para>
    /// </summary>
    public PostingIdentity PostingIdentity { get; init; } = PostingIdentity.User;

    /// <summary>
    /// Declares this hub's posting identity. See <see cref="PostingIdentity"/>.
    /// </summary>
    public MessageHubConfiguration WithPostingIdentity(PostingIdentity identity)
        => this with { PostingIdentity = identity };

    internal Func<IServiceCollection, IServiceCollection> Services { get; init; } = x => x;

    public IServiceProvider ServiceProvider { get; set; } = null!;
    private readonly Lock serviceProviderLock = new();

    // Synchronous dispose actions seeded onto the hub at construction. Disposal at the
    // hub level is purely synchronous (see MessageHub.DisposeImpl → disposables.Dispose)
    // — nothing here is a Task or an IObservable. Anything genuinely async must be
    // bridged onto the mesh IO pool by the layer that owns it.
    internal ImmutableList<Action<IMessageHub>> DisposeActions { get; init; } = [];

    internal ImmutableList<MessageHandlerItem> MessageHandlers { get; init; } = ImmutableList<MessageHandlerItem>.Empty;

    // Observable buildup actions: each is a factory returning IObservable<Unit>. The hub composes them
    // reactively (Observable.Concat) when it handles InitializeHubRequest and opens the Initialize gate on
    // completion — the init path is observable end-to-end, no await. See MessageHub.HandleInitialize.
    protected internal ImmutableList<Func<IMessageHub, IObservable<Unit>>> BuildupActions { get; init; } = ImmutableList<Func<IMessageHub, IObservable<Unit>>>.Empty;
    protected internal ImmutableList<Action<IMessageHub>> SyncBuildupActions { get; init; } = [];

    internal IMessageHub HubInstance { get; set; } = null!;

    public MessageHubConfiguration RegisterForDisposal(Action<IMessageHub> disposeAction)
        => this with { DisposeActions = DisposeActions.Add(disposeAction) };





    public MessageHubConfiguration WithServices(Func<IServiceCollection, IServiceCollection> configuration)
    {
        return this with { Services = x => configuration(Services(x)) };
    }

    public MessageHubConfiguration WithHostedHub(Address address,
        Func<MessageHubConfiguration, MessageHubConfiguration> configuration)
        =>
            this.WithRoutes(f => f.RouteAddress(address.Type, (a, d) =>
        {
            if (!address.Equals(a))
                return d;
            var hub = f.Hub.GetHostedHub(a, configuration).DeliverMessage(d);

            if (hub is null)
                throw new ArgumentException($"Could not find hub with address {a}");
            return d.Forwarded();
        }));


    public ITypeRegistry TypeRegistry { get; }
    protected virtual ServiceCollection ConfigureServices(IMessageHub? parent)
    {
        var services = new ServiceCollection();
        services.Replace(ServiceDescriptor.Singleton<IMessageHub>(sp => new MessageHub(sp, sp.GetRequiredService<HostedHubsCollection>(), this, parent)));
        services.Replace(ServiceDescriptor.Singleton<HostedHubsCollection, HostedHubsCollection>(sp => new(sp, Address)));
        services.Replace(ServiceDescriptor.Singleton(typeof(ITypeRegistry), _ => TypeRegistry));
        services.Replace(ServiceDescriptor.Singleton(sp => new ParentMessageHub(sp.GetRequiredService<IMessageHub>())));
        // Check if AccessService is registered in the parent service provider
        if (ParentServiceProvider?.GetService<AccessService>() == null)
        {
            services.AddSingleton<AccessService>();
        }
        Services.Invoke(services);
        return services;
    }



    private record ParentMessageHub(IMessageHub Value);


    public MessageHubConfiguration WithHandler<TMessage>(Func<IMessageHub, IMessageDelivery<TMessage>, IMessageDelivery> delivery, Func<IMessageHub, IMessageDelivery, bool>? filter = null)
    {
        TypeRegistry.GetOrAddType(typeof(TMessage));
        return this with
        {
            MessageHandlers = MessageHandlers.Add(
                new(typeof(TMessage),
                    (h, m, c) =>
                        m is IMessageDelivery<TMessage> mdTyped &&
                        (filter ?? DefaultFilter).Invoke(h, m)
                            // Synchronous handler: run inline on the hub's ActionBlock
                            // thread (preserves TaskScheduler.Current — the actor-model
                            // invariant WithTaskScheduler relies on) and emit via
                            // Observable.Return. No pool hop.
                            ? Observable.Return(delivery.Invoke(h, mdTyped))
                            : Observable.Return(m)))
        };
    }
    public MessageHubConfiguration WithHandler<TMessage>(Func<IMessageHub, IMessageDelivery<TMessage>, CancellationToken, Task<IMessageDelivery>> delivery, Func<IMessageHub, IMessageDelivery, bool>? filter = null)
    {
        TypeRegistry.GetOrAddType(typeof(TMessage));
        return this with
        {
            MessageHandlers = MessageHandlers.Add(
                new(typeof(TMessage),
                    (h, m, c) =>
                        m is IMessageDelivery<TMessage> mdTyped &&
                        (filter ?? DefaultFilter).Invoke(h, m)
                            // Invoke inline so the handler's synchronous prefix runs on
                            // the hub's ActionBlock scheduler (actor-model invariant);
                            // bridge the resulting Task to the rule chain. An
                            // already-completed Task emits inline; genuine async resumes
                            // on its own continuation.
                            ? delivery.Invoke(h, mdTyped, c).ToObservable()
                            : Observable.Return(m)))
        };
    }

    private static bool DefaultFilter(IMessageHub hub, IMessageDelivery delivery)
    {
        if (delivery.Target == null)
            return true;
        // Compare without Host since Host tracks routing path, not destination
        var targetWithoutHost = delivery.Target with { Host = null };
        return targetWithoutHost.Equals(hub.Address);
    }

    /// <summary>
    /// Idempotent: re-adding the same delegate (method group / cached lambda)
    /// is a no-op. Without this, a configurator composed through multiple layers
    /// (e.g. NodeType <c>HubConfiguration</c> + <c>DefaultNodeHubConfiguration</c>
    /// + module extensions) silently stacks duplicate init runs — each watcher /
    /// subscription that runs in the init then fires N×, dispatching N rounds
    /// per state change. Symptom: Resubmit test saw Thread.Messages accumulate
    /// the same response id 3× because <c>ThreadSubmissionServer.InstallServerWatcher</c>
    /// was running on N stacked subscriptions.
    /// </summary>
    public MessageHubConfiguration WithInitialization(Action<IMessageHub> action) => this with
    {
        SyncBuildupActions = SyncBuildupActions.Contains(action)
            ? SyncBuildupActions
            : SyncBuildupActions.Add(action)
    };

    /// <summary>
    /// Reactive init overload — caller returns an <see cref="IObservable{Unit}"/> the hub
    /// will Subscribe to during init. The Initialize gate opens after the observable
    /// emits its first value or completes. Wrap a Task-returning method via
    /// <c>Observable.FromAsync(() =&gt; method())</c> for the typical "load initial data
    /// before processing messages" shape. Hub-reachable code returns
    /// <see cref="IObservable{T}"/>, never <see cref="Task{T}"/>.
    /// <para>Idempotent on the caller's delegate identity — the inner action is tracked
    /// in <see cref="RegisteredObservableInits"/> so repeat <c>WithInitialization(F)</c>
    /// calls (composed configurators) collapse to one Subscribe.</para>
    /// </summary>
    public MessageHubConfiguration WithInitialization(Func<IMessageHub, IObservable<Unit>> action)
    {
        if (RegisteredObservableInits.Contains(action))
            return this;
        // Store the observable directly — no Task bridge. HandleInitialize composes the BuildupActions
        // reactively and opens the gate on completion.
        return this with
        {
            RegisteredObservableInits = RegisteredObservableInits.Add(action),
            BuildupActions = BuildupActions.Add(action),
        };
    }

    /// <summary>Identity-tracking set for the observable <c>WithInitialization</c> overload.</summary>
    internal ImmutableHashSet<Func<IMessageHub, IObservable<Unit>>> RegisteredObservableInits { get; init; } =
        ImmutableHashSet<Func<IMessageHub, IObservable<Unit>>>.Empty;

    protected void CreateServiceProvider(IMessageHub? parent)
    {
        lock (serviceProviderLock)
        {
            if (ServiceProvider != null!)
                return; // Already created

            ServiceProvider = ConfigureServices(parent)
                .SetupModules(ParentServiceProvider);
        }
    }

    public virtual IMessageHub Build<TAddress>(IServiceProvider serviceProvider, TAddress address)
    {
        // TODO V10: Check whether this address is already built in hosted hubs collection, if not build. (18.01.2024, Roland Buergi)
        var parentHub = ParentServiceProvider?.GetService<ParentMessageHub>()?.Value;
        CreateServiceProvider(parentHub);

        var parentHubs = ParentServiceProvider?.GetService<HostedHubsCollection>();

        HubInstance = ServiceProvider.GetRequiredService<IMessageHub>();
        parentHubs?.Add(HubInstance);

        // Execute synchronous initialization actions BEFORE starting message processing
        // This ensures services like Workspace/DataContext are fully configured before any messages arrive
        foreach (var initAction in SyncBuildupActions)
            initAction(HubInstance);

        // Start message processing after SyncBuildupActions complete
        ((MessageHub)HubInstance).StartMessageProcessing();

        return HubInstance;
    }



    internal ImmutableDictionary<(Type, string?), object> Properties { get; init; } = ImmutableDictionary<(Type, string?), object>.Empty;
    internal ImmutableList<Func<SyncPipelineConfig, SyncPipelineConfig>> PostPipeline { get; set; }

    public MessageHubConfiguration AddPostPipeline(Func<SyncPipelineConfig, SyncPipelineConfig> pipeline) => this with { PostPipeline = PostPipeline.Add(pipeline) };
    private SyncPipelineConfig UserServicePostPipeline(SyncPipelineConfig syncPipeline)
    {
        var userService = syncPipeline.Hub.ServiceProvider.GetService<AccessService>();
        var logger = syncPipeline.Hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("MeshWeaver.AccessContext");
        // The hub's declared posting identity (feedback_access_context_always_set):
        //   User (default) → post as the ambient user; UNHAPPY (error + fail delivery)
        //     when no user context is set for a non-exempt message.
        //   System (routing, persistence) → the hub's own otherwise-unattributed posts
        //     run as system-security automatically.
        // 🚨 Read from the LIVE hub Configuration, NOT this instance's field: PostPipeline
        // captured the UserServicePostPipeline method-group delegate against the
        // configuration instance at constructor time, BEFORE any `with { PostingIdentity }`
        // copy. syncPipeline.Hub.Configuration is the final, fully-`with`'d configuration,
        // so it carries the declared value.
        var postingIdentity = syncPipeline.Hub.Configuration.PostingIdentity;
        return syncPipeline.AddPipeline((d, next) =>
        {
            // If AccessContext was pre-set by ImpersonateAsHub() / ImpersonateAsSystem()
            // / WithAccessContext() / ResponseFor() / a forwarded user delivery, don't
            // overwrite — that explicit identity wins over the hub's default mode.
            if (d.AccessContext is not null)
                return next(d);

            // Context = per-request AsyncLocal (delivery pipeline).
            // CircuitContext = per-circuit AsyncLocal (set by CircuitAccessHandler).
            var context = userService?.Context ?? userService?.CircuitContext;
            if (context is not null)
            {
                d = d.SetAccessContext(context);
            }
            else if (postingIdentity == PostingIdentity.System && !IsAccessContextExempt(d.Message))
            {
                // 🚨 SYSTEM-IDENTITY HUB (routing / persistence — the courier and the
                // store). Its own otherwise-unattributed posts run as System: stamp the
                // well-known system-security identity (granted Permission.All) so the
                // post is never null and never fail-closed. Declared once at hub startup
                // via WithPostingIdentity(PostingIdentity.System) instead of every
                // callsite remembering ImpersonateAsSystem. Exempt messages
                // ([SystemMessage]/[CanBeIgnored]/DeliveryFailure) keep their null —
                // they carry no security-relevant payload.
                d = d.SetAccessContext(new AccessContext
                {
                    ObjectId = SystemSecurityObjectId,
                    Name = SystemSecurityObjectId
                });
            }
            else if (!IsAccessContextExempt(d.Message))
            {
                // 🚨 NEVER-NULL INVARIANT (feedback_access_context_always_set):
                // AccessContext must ALWAYS be set. Three sources, no fourth —
                // infrastructure → System, user-contexts → the user,
                // threads/activities → the owner. Once those sources are wired,
                // there is no legitimate application post with a null resolved
                // context — a null here is a GAP.
                //
                // NO IDENTITY, NO DELIVERY. We do NOT throw (this runs
                // synchronously inside Post, called from countless fire-and-forget
                // callsites where a synchronous throw would be unobserved or crash
                // an unrelated path). Instead we LOG AN ERROR (naming the sending
                // hub + target so the null source is identifiable) and FAIL THE
                // DELIVERY immediately — return delivery.Failed(...) and short-
                // circuit the rest of the pipeline. ScheduleNotify still enqueues
                // the Failed delivery; NotifyAsync detects State == Failed and
                // ReportFailure posts a DeliveryFailure back to the sender, so an
                // awaiting hub.Observe(...) gets a clean OnError instead of a
                // silent null-context delivery that fails closed deep in
                // AccessControl. The error log is the tripwire — CI parses the
                // `MeshWeaver.AccessContext` channel for `[Error]` lines, so every
                // gap surfaces loudly and gets fixed at its source.
                //
                // Genuine identity-free framework traffic is EXEMPT (see
                // IsAccessContextExempt): [SystemMessage] (heartbeats, hub-lifecycle,
                // SetCurrentRequest, Save/DeleteMeshNodeRequest), [CanBeIgnored]
                // (Shutdown/Dispose/HeartBeat), and DeliveryFailure (the courier's
                // own error channel). Infrastructure that legitimately bypasses RLS
                // opts in EXPLICITLY at the callsite via ImpersonateAsSystem /
                // ImpersonateAsHub (routing's own posts, persistence, cache
                // hydration) — that sets d.AccessContext before this pipeline runs,
                // so it short-circuits above and never reaches here. The portal hub
                // stamps the circuit user via its own PortalApplication PostPipeline
                // step (the SOURCE fix), so its layout/agent/model subscribes carry
                // the user and never trip this.
                var failureReason =
                    $"AccessContext must never be null for an application post — no identity, no delivery. " +
                    $"hub={syncPipeline.Hub.Address}, message={d.Message?.GetType().Name ?? "(null)"}, " +
                    $"target={d.Target?.ToString() ?? "(null)"} was posted with no AccessContext " +
                    $"(no Context, no CircuitContext). The post lost the user identity. Wire its source: " +
                    $"user-context from the circuit/HTTP user; infrastructure via " +
                    $"AccessService.ImpersonateAsSystem / PostOptions.ImpersonateAsHub; threads/activities " +
                    $"via AccessContextScope.FromNode. See AccessContextPropagation.md / " +
                    $"feedback_access_context_always_set.";
                logger?.LogError("PostPipeline: {FailureReason}", failureReason);
                return d.Failed(failureReason);
            }
            // Per-message; gate on Debug so the 5 arg evaluations + boxing are
            // skipped when not enabled.
            if (logger?.IsEnabled(LogLevel.Debug) == true)
                logger.LogDebug(
                    "PostPipeline: hub={Hub}, message={MessageType}, user={User} (context={Context}, circuit={Circuit})",
                    syncPipeline.Hub.Address,
                    d.Message?.GetType().Name ?? "(null)",
                    d.AccessContext?.ObjectId ?? "(no-context)",
                    userService?.Context?.ObjectId ?? "(null)",
                    userService?.CircuitContext?.ObjectId ?? "(null)");
            return next(d);
        });
    }

    /// <summary>
    /// True for messages marked <see cref="SystemMessageAttribute"/> — framework
    /// infrastructure traffic (heartbeats, hub-lifecycle, subscription
    /// management) that carries no security-relevant payload, so the mesh
    /// hub's "posted with no AccessContext" warning would only be developer
    /// noise. <b>Responses</b> (GetDataResponse, DeliveryFailure) are NOT
    /// marked — they auto-inherit the request's AccessContext via
    /// <see cref="PostOptions.ResponseFor"/>, so they get proper identity
    /// without needing an exemption.
    /// </summary>
    // The well-known System identity. Must match
    // AccessService.ImpersonateAsSystem's literal and
    // MeshWeaver.Mesh.Security.WellKnownUsers.System; we don't reference that
    // constant here because Messaging.Hub sits below Mesh.Contract in the project
    // graph and adding the dep would invert it (same rationale as ImpersonateAsSystem).
    private const string SystemSecurityObjectId = "system-security";

    private static bool IsFrameworkLifecycleMessage(object? message)
    {
        if (message is null) return false;
        var type = message.GetType();
        return _systemMessageCache.GetOrAdd(type,
            static t => t.GetCustomAttributes(typeof(SystemMessageAttribute), inherit: true).Length > 0);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, bool> _systemMessageCache = new();

    /// <summary>
    /// True when a null <c>AccessContext</c> is legitimate for this message and the
    /// never-null tripwire (<see cref="UserServicePostPipeline"/>) must NOT throw.
    /// The exempt set is exactly the genuinely identity-free framework traffic:
    /// <list type="bullet">
    /// <item><b><see cref="SystemMessageAttribute"/></b> — heartbeats, hub-lifecycle,
    /// subscription management, <c>SetCurrentRequest</c>, <c>Save/DeleteMeshNodeRequest</c>
    /// (per-node-hub self-writes). Carry no security-relevant payload.</item>
    /// <item><b><see cref="CanBeIgnoredAttribute"/></b> — fire-and-forget control traffic
    /// (Shutdown / Dispose / HeartBeat) with no awaiting requester.</item>
    /// <item><b><see cref="DeliveryFailure"/></b> — the courier's OWN error channel. It
    /// inherits the request's identity via <c>PostOptions.ResponseFor</c> when the request
    /// had one; throwing here would turn a NACK into a NACK-of-a-NACK and break routing's
    /// error reporting (it is already treated as non-NACKable in
    /// <c>RoutingServiceBase</c> / <c>HierarchicalRouting</c>).</item>
    /// </list>
    /// Everything else is an application post and MUST carry an identity (user, System, or
    /// owner) — a null is a real gap to fix at the source, surfaced by the throw.
    /// </summary>
    private static bool IsAccessContextExempt(object? message)
    {
        if (message is null) return true; // nothing to attribute
        if (message is DeliveryFailure) return true;
        if (IsFrameworkLifecycleMessage(message)) return true;
        return _canBeIgnoredCache.GetOrAdd(message.GetType(),
            static t => t.GetCustomAttributes(typeof(CanBeIgnoredAttribute), inherit: true).Length > 0);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, bool> _canBeIgnoredCache = new();

    internal ImmutableList<Func<AsyncPipelineConfig, AsyncPipelineConfig>> DeliveryPipeline { get; set; }
    internal TimeSpan? StartupTimeout { get; init; } //= new(0, 0, 30); // Default 10 seconds
    // Default 60s. The previous 30s default was hitting CI consistently on
    // cross-hub forward chains (mesh hub → per-node hub → response) when the
    // per-node hub's cold-cache initialization took >30s on slow Linux runners
    // — a typical scenario for AcceMe TodoDataChangeWorkflowTest and Content
    // tests where a Node's first activation reads + validates from persistence.
    // The test client hub override (MonolithMeshTestBase.WithRequestTimeout(60s))
    // covered the test-side post but intermediate sync/mesh hubs still
    // followed the 30s default. 60s as a framework default matches that
    // ceiling; anything genuinely longer than that is a real bug.
    internal TimeSpan RequestTimeout { get; init; } = new(0, 1, 0);

    /// <summary>
    /// Quiescing-phase drain budget per hub. When <see cref="MessageHub.Dispose"/> fires,
    /// the hub waits up to this long for in-flight response callbacks (registered via
    /// <c>hub.Observe(...)</c>) to resolve naturally before forcibly cancelling them
    /// with <see cref="ObjectDisposedException"/>.
    /// <para>
    /// Default: 2 s — enough headroom for a slow CI scheduler to deliver a legitimate
    /// reply, low enough that a leaked callback fails fast.
    /// </para>
    /// <para>
    /// Tests with deliberately abandoned callbacks (e.g. <c>client.Observe(req)</c> +
    /// <c>Subscribe(...)</c> with no completion path) should call
    /// <see cref="WithQuiesceTimeout"/> on their hub configuration to drop this to
    /// ~100-500 ms — every leaked callback otherwise costs the full budget per
    /// dispose, which compounds across N test classes.
    /// </para>
    /// </summary>
    internal TimeSpan QuiesceTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Sets the Quiescing-phase drain budget for this hub. See <see cref="QuiesceTimeout"/>.
    /// </summary>
    public MessageHubConfiguration WithQuiesceTimeout(TimeSpan timeout) => this with { QuiesceTimeout = timeout };

    /// <summary>
    /// When true, the hub will not automatically post InitializeHubRequest during construction.
    /// Manual initialization is required by posting InitializeHubRequest to the hub.
    /// </summary>
    internal bool DeferredInitialization { get; init; }

    /// <summary>
    /// Sets the timeout allowed for startup
    /// </summary>
    /// <param name="timeout"></param>
    /// <returns></returns>
    public MessageHubConfiguration WithStartupTimeout(TimeSpan timeout) => this with { StartupTimeout = timeout };

    /// <summary>
    /// Sets the timeout for callbacks (AwaitResponse)
    /// </summary>
    /// <param name="timeout"></param>
    /// <returns></returns>
    public MessageHubConfiguration WithRequestTimeout(TimeSpan timeout) => this with { RequestTimeout = timeout };

    /// <summary>
    /// Enables deferred initialization. When enabled, the hub will not automatically post InitializeHubRequest
    /// during construction. Manual initialization is required by posting InitializeHubRequest to the hub.
    /// This is useful when the hub needs to be fully constructed before initialization can proceed.
    /// </summary>
    /// <param name="deferred">Whether to defer initialization (default: true)</param>
    /// <returns>Updated configuration</returns>
    public MessageHubConfiguration WithDeferredInitialization(bool deferred = true) =>
        this with { DeferredInitialization = deferred };

    public MessageHubConfiguration AddDeliveryPipeline(Func<AsyncPipelineConfig, AsyncPipelineConfig> pipeline) => this with { DeliveryPipeline = DeliveryPipeline.Add(pipeline) };
    private AsyncPipelineConfig UserServiceDeliveryPipeline(AsyncPipelineConfig asyncPipeline)
    {
        var userService = asyncPipeline.Hub.ServiceProvider.GetService<AccessService>();
        var logger = asyncPipeline.Hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("MeshWeaver.AccessContext");
        return asyncPipeline.AddPipeline((d, ct, next) =>
        {
            var accessContext = d.AccessContext;
            // Per-message; gate on Debug.
            if (logger?.IsEnabled(LogLevel.Debug) == true)
                logger.LogDebug(
                    "DeliveryPipeline: hub={Hub}, message={MessageType}, user={User}, sender={Sender}",
                    asyncPipeline.Hub.Address,
                    d.Message?.GetType().Name ?? "(null)",
                    accessContext?.ObjectId ?? "(no-context)",
                    d.Sender);
            // Only propagate USER identities to AsyncLocal. Hub-shaped principals
            // may legitimately ride delivery.AccessContext (for AccessControl)
            // but MUST NOT leak into AsyncLocal — see
            // Doc/Architecture/AccessContextPropagation.md. The MessageHub.HandleMessageAsync
            // hook applies the same guard for the rule-chain dispatch boundary.
            var shouldStamp = accessContext is not null
                && !AccessService.LooksLikeHubPrincipal(accessContext.ObjectId);
            if (!shouldStamp)
                return next.Invoke(d, ct);
            // next() emits synchronously (terminates at ScheduleExecution); Finally
            // clears the AsyncLocal stamp when that emission completes — the same
            // set/clear window the old try/finally gave us, with no await.
            userService?.SetContext(accessContext);
            return next.Invoke(d, ct).Finally(() => userService?.SetContext(null));
        });
    }

    public T? Get<T>(string? context = null) => (T?)(Properties.GetValueOrDefault((typeof(T), context)) ?? default(T));
    public MessageHubConfiguration Set<T>(T value, string? context = null) => this with { Properties = Properties.SetItem((typeof(T), context), value!) };


    public MessageHubConfiguration WithType<T>(string? name = null)
    {
        var typeName = name ?? typeof(T).FullName!;
        System.Diagnostics.Debug.WriteLine($"MessageHubConfiguration.WithType<{typeof(T).Name}>({typeName}): TypeRegistry hashCode={TypeRegistry.GetHashCode()}");
        TypeRegistry.WithType(typeof(T), typeName);
        return this;
    }
    public MessageHubConfiguration WithType(Type type, string? name = null)
    {
        TypeRegistry.WithType(type, name ?? type.FullName!);
        return this;
    }

}

public record AsyncPipelineConfig
{
    public AsyncPipelineConfig(IMessageHub Hub, AsyncDelivery asyncDelivery)
    {
        this.Hub = Hub;
        AsyncDelivery = asyncDelivery;

    }

    internal AsyncDelivery AsyncDelivery { get; init; }

    public AsyncPipelineConfig AddPipeline(
        Func<IMessageDelivery, CancellationToken, AsyncDelivery, IObservable<IMessageDelivery>> pipeline)
        => this with { AsyncDelivery = (d, ct) => pipeline.Invoke(d, ct, AsyncDelivery) };

    public IMessageHub Hub { get; init; }
}
public record SyncPipelineConfig
{
    public SyncPipelineConfig(IMessageHub Hub, SyncDelivery syncDelivery)
    {
        this.Hub = Hub;
        SyncDelivery = syncDelivery;

    }

    internal SyncDelivery SyncDelivery { get; init; }

    public SyncPipelineConfig AddPipeline(
        Func<IMessageDelivery, SyncDelivery, IMessageDelivery> pipeline)
        => this with { SyncDelivery = d => pipeline.Invoke(d, SyncDelivery) };

    public IMessageHub Hub { get; init; }
}

internal record MessageHandlerItem(Type MessageType, Func<IMessageHub, IMessageDelivery, CancellationToken, IObservable<IMessageDelivery>> AsyncDelivery);
