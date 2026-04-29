using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reactive.Linq;
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

public sealed class MessageHub : IMessageHub
{
    public Address Address => Configuration.Address;


    public void InvokeAsync(Func<CancellationToken, Task> action, Func<Exception, Task> exceptionCallback) =>
        Post(new ExecutionRequest(action, exceptionCallback));

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
    /// Cross-process dispose trace. Every phase boundary (Quiescing entry/exit,
    /// DisposeHostedHubs entry/exit, ShutDown entry/exit) appends a single line
    /// with timestamp, hub address, phase, and elapsed-ms. Path is fixed so the
    /// developer can `tail -f` it during a test run and spot a stalled phase
    /// immediately, without waiting for the suite to finish.
    /// </summary>
    private static readonly string DisposeTraceLogPath =
        Path.Combine(Path.GetTempPath(), "meshweaver-dispose-trace.log");
    private static readonly object DisposeTraceLogLock = new();

    private static void DisposeTrace(Address address, string phase, long elapsedMs, string? extra = null)
    {
        try
        {
            var line = extra is null
                ? $"{DateTime.UtcNow:HH:mm:ss.fff} {address} {phase} elapsed={elapsedMs}ms"
                : $"{DateTime.UtcNow:HH:mm:ss.fff} {address} {phase} elapsed={elapsedMs}ms {extra}";
            lock (DisposeTraceLogLock)
                File.AppendAllText(DisposeTraceLogPath, line + Environment.NewLine);
        }
        catch
        {
            // Tracing must never throw out of dispose.
        }
    }

    private readonly ILogger logger;
    public MessageHubConfiguration Configuration { get; }
    private readonly HostedHubsCollection hostedHubs;

    public long Version { get; private set; }

    /// <summary>
    /// Sets the initial version for the hub. Only callable during initialization
    /// before any messages are processed.
    /// </summary>
    public void SetInitialVersion(long version)
    {
        Version = version;
    }

    public MessageHubRunLevel RunLevel { get; private set; }
    private readonly IMessageService messageService;
    /// <summary>
    /// Parent hub address captured at construction. Used in disposal logging so we
    /// don't re-resolve from <see cref="MessageHubConfiguration.ParentHub"/> on a
    /// scope that may already be disposed.
    /// </summary>
    private readonly Address? parentAddress;
    public ITypeRegistry TypeRegistry { get; }
    public void Start()
    {
        if (RunLevel < MessageHubRunLevel.Started)
        {
            RunLevel = MessageHubRunLevel.Started;
            hasStarted.TrySetResult();
        }
    }

    public void FailStartup(Exception error)
    {
        hasStarted.TrySetException(error);
    }

    public void CancelCurrentExecution()
    {
        messageService.CancelExecution();
    }

    /// <summary>
    /// Starts message processing and posts the initialization request.
    /// Called from Build() after SyncBuildupActions complete.
    /// </summary>
    internal void StartMessageProcessing()
    {
        messageService.Start();
        if (!Configuration.DeferredInitialization)
            Post(new InitializeHubRequest());
    }

    private readonly ThreadSafeLinkedList<AsyncDelivery> rules = new();
    private readonly Lock messageHandlerRegistrationLock = new();
    private readonly Lock typeRegistryLock = new();
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
        parentAddress = parentHub?.Address;


        messageService = new MessageService(configuration.Address,
            serviceProvider.GetRequiredService<ILogger<MessageService>>(), this, parentHub);

        foreach (var disposeAction in configuration.DisposeActions)
            asyncDisposeActions.Add(disposeAction);

        JsonSerializerOptions = this.CreateJsonSerializationOptions(parentHub);

        TypeRegistry.WithType(typeof(PingRequest), nameof(PingRequest));
        TypeRegistry.WithType(typeof(PingResponse), nameof(PingResponse));
        Register<DisposeRequest>(HandleDispose);
        Register<ShutdownRequest>(HandleShutdown);
        Register<PingRequest>(HandlePingRequest);
        Register<InitializeHubRequest>(HandleInitialize);
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

    private async Task<IMessageDelivery> HandleInitialize(IMessageDelivery<InitializeHubRequest> request, CancellationToken cancellationToken)
    {
        logger.LogDebug("Message hub {address} initializing via InitializeHubRequest", Address);

        var actions = Configuration.BuildupActions;
        logger.LogDebug("Message hub {address} has {count} BuildupActions to run", Address, actions.Count);
        for (var i = 0; i < actions.Count; i++)
        {
            logger.LogDebug("Message hub {address} starting BuildupAction {i}/{count}", Address, i + 1, actions.Count);
            await actions[i](this, cancellationToken);
            logger.LogDebug("Message hub {address} completed BuildupAction {i}/{count}", Address, i + 1, actions.Count);
        }

        logger.LogDebug("Message hub {address} BuildupActions complete, opening Initialize gate", Address);

        // Open the Initialize gate - this will set RunLevel to Started if all other gates are also open
        OpenGate(MessageHubConfiguration.InitializeGateName);

        return request.Processed();
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

        if (interfaceType.GetGenericTypeDefinition() == typeof(IMessageHandler<>))
            handlerCall = Expression.Call(
                null,
                MessageHubPluginExtensions.TaskFromResultMethod,
                handlerCall
            );

        var lambda = Expression
            .Lambda<Func<IMessageDelivery, CancellationToken, Task<IMessageDelivery>>>(
                handlerCall,
                prm,
                cancellationTokenPrm
            )
            .Compile();
        return (d, c) => lambda(d, c);
    }

    private record TypeAndHandler(Type Type, AsyncDelivery? Action);



    #endregion



    private Task<IMessageDelivery> HandleMessageAsync(
        IMessageDelivery delivery,
        LinkedListNode<AsyncDelivery> node,
        CancellationToken cancellationToken
    ) => HandleMessageAsyncImpl(delivery, node, cancellationToken, 0);

    private async Task<IMessageDelivery> HandleMessageAsyncImpl(
        IMessageDelivery delivery,
        LinkedListNode<AsyncDelivery> node,
        CancellationToken cancellationToken,
        int depth
    )
    {
        if (depth > 500)
            throw new InvalidOperationException($"HandleMessageAsync recursion depth exceeded 500 in hub {Address} for {delivery.Message.GetType().Name}");

        delivery = await node.Value.Invoke(delivery, cancellationToken);

        if (node.Next == null)
            return delivery;

        return await HandleMessageAsyncImpl(delivery, node.Next, cancellationToken, depth + 1);
    }

    public bool OpenGate(string name)
    {
        return messageService.OpenGate(name);
    }


    async Task<IMessageDelivery> IMessageHub.HandleMessageAsync(
        IMessageDelivery delivery,
        CancellationToken cancellationToken
    )
    {
        ++Version;

        logger.LogTrace("MESSAGE_FLOW: HUB_HANDLE_START | {MessageType} | Hub: {Address} | MessageId: {MessageId} | Version: {Version}",
            delivery.Message.GetType().Name, Address, delivery.Id, Version);

        // Log only important messages during disposal
        if (IsDisposing && delivery.Message is ShutdownRequest shutdownReq)
        {
            logger.LogDebug("Processing ShutdownRequest in {Address} : RunLevel={RunLevel}, Version={RequestVersion}, Expected={ExpectedVersion}",
                Address, shutdownReq.RunLevel, shutdownReq.Version, Version - 1);
        }

        if (rules.First != null)
        {
            logger.LogTrace("MESSAGE_FLOW: HUB_PROCESSING_RULES | {MessageType} | Hub: {Address} | MessageId: {MessageId} | RuleCount: {RuleCount}",
                delivery.Message.GetType().Name, Address, delivery.Id, rules.Count);
            delivery = await HandleMessageAsync(delivery, rules.First, cancellationToken);
        }
        else
        {
            logger.LogTrace("MESSAGE_FLOW: HUB_NO_RULES | {MessageType} | Hub: {Address} | MessageId: {MessageId}",
                delivery.Message.GetType().Name, Address, delivery.Id);
        }

        var result = FinishDelivery(delivery);
        logger.LogTrace("MESSAGE_FLOW: HUB_HANDLE_END | {MessageType} | Hub: {Address} | MessageId: {MessageId} | Result: {State}",
            delivery.Message.GetType().Name, Address, delivery.Id, result.State);
        return result;
    }

    private IMessageDelivery FinishDelivery(IMessageDelivery delivery)
    {
        logger.LogDebug("FinishDelivery called for {MessageType} (ID: {MessageId}) with state {State} in {Address}",
            delivery.Message.GetType().Name, delivery.Id, delivery.State, Address);

        if (delivery.State == MessageDeliveryState.Submitted)
        {
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
        => RestoreUserContextOnEmission(
            ObserveById(delivery.Id, delivery.Message?.GetType().Name ?? "<null>", delivery.Target),
            delivery.AccessContext);

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
        var accessService = ServiceProvider.GetService<AccessService>();
        var capturedCtx = accessService?.Context;
        var messageId = Guid.NewGuid().AsString();
        // Resolve the target up-front so the pending-callback diagnostic can name
        // which hub we're waiting on. We only run `options(...)` once — Post below
        // gets the same composed PostOptions via WithMessageId chaining.
        var probeOptions = options(new PostOptions(Address));
        var subject = GetOrAddResponseSubject(messageId, r?.GetType().Name ?? "<null>", probeOptions.Target);
        Post(r, opts => options(opts).WithMessageId(messageId));
        return RestoreUserContextOnEmission(ApplyTimeout(subject), capturedCtx);
    }

    private IObservable<IMessageDelivery> ObserveById(string messageId,
        string requestType = "<unknown>",
        Address? target = null)
    {
        var subject = GetOrAddResponseSubject(messageId, requestType, target);
        return ApplyTimeout(subject);
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
        return source.Do(_ =>
        {
            ServiceProvider.GetService<AccessService>()?.SetContext(capturedCtx);
        });
    }

    private IObservable<IMessageDelivery> ApplyTimeout(IObservable<IMessageDelivery> source)
        => source.Timeout(Configuration.RequestTimeout, System.Reactive.Linq.Observable.Throw<IMessageDelivery>(
            new TimeoutException(
                $"No response received in hub {Address} within {Configuration.RequestTimeout}. " +
                "The request may have been undeliverable or the target hub was not found.")));

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


    private async Task<IMessageDelivery> ExecuteRequest(
        IMessageDelivery delivery,
        CancellationToken cancellationToken
    )
    {
        if (delivery.Message is not ExecutionRequest er)
            return delivery;
        await er.Action.Invoke(cancellationToken);
        return delivery.Processed();
    }

    private Task<IMessageDelivery> HandleCallbacks(
        IMessageDelivery delivery,
        CancellationToken cancellationToken
    )
    {
        logger.LogTrace("MESSAGE_FLOW: HUB_HANDLE_CALLBACKS | {MessageType} | Hub: {Address} | MessageId: {MessageId}",
            delivery.Message.GetType().Name, Address, delivery.Id);

        if (
            !delivery.Properties.TryGetValue(PostOptions.RequestId, out var requestId)
            || requestId.ToString() is not { } requestIdString
        )
        {
            logger.LogTrace("MESSAGE_FLOW: HUB_NO_CALLBACKS | {MessageType} | Hub: {Address} | MessageId: {MessageId}",
                delivery.Message.GetType().Name, Address, delivery.Id);
            return Task.FromResult(delivery);
        }

        System.Reactive.Subjects.AsyncSubject<IMessageDelivery> subject;
        lock (responseSubjects)
        {
            if (!responseSubjects.Remove(requestIdString, out var entry))
            {
                logger.LogDebug("No subject found for response message {MessageType} (ID: {MessageId}) - treating as processed",
                    delivery.Message.GetType().Name, delivery.Id);
                return Task.FromResult(delivery.Processed());
            }
            subject = entry.Subject;
        }

        logger.LogDebug("Dispatching response to subject | {MessageType} | Hub: {Address} | MessageId: {MessageId}",
            delivery.Message.GetType().Name, Address, delivery.Id);

        if (delivery.Message is DeliveryFailure failure)
        {
            subject.OnError(new DeliveryFailureException(failure));
        }
        else
        {
            subject.OnNext(delivery);
            subject.OnCompleted();
        }

        logger.LogTrace("MESSAGE_FLOW: HUB_CALLBACKS_COMPLETE | {MessageType} | Hub: {Address} | MessageId: {MessageId}",
            delivery.Message.GetType().Name, Address, delivery.Id);
        return Task.FromResult(delivery.Processed());
    }

    Address IMessageHub.Address => Address;

    public IMessageDelivery<TMessage>? Post<TMessage>(
        TMessage message,
        Func<PostOptions, PostOptions>? configure = null
    )
    {
        var options = new PostOptions(Address);
        if (configure != null)
            options = configure(options);

        logger.LogTrace("MESSAGE_FLOW: HUB_POST | {MessageType} | Hub: {Address} | Target: {Target} | Sender: {Sender}",
            typeof(TMessage).Name, Address, options.Target, options.Sender);

        // Log only important messages during disposal
        if (IsDisposing && (message is ShutdownRequest || logger.IsEnabled(LogLevel.Debug)))
        {
            logger.LogDebug("Posting {MessageType} during disposal from {Sender} to {Target} in hub {Address}",
                typeof(TMessage).Name, options.Sender, options.Target, Address);
        }

        var result = (IMessageDelivery<TMessage>?)messageService.Post(message, options);
        logger.LogTrace("MESSAGE_FLOW: HUB_POST_RESULT | {MessageType} | Hub: {Address} | MessageId: {MessageId} | Target: {Target}",
            typeof(TMessage).Name, Address, result?.Id, options.Target);
        return result;
    }

    public IMessageDelivery DeliverMessage(IMessageDelivery delivery)
    {
        logger.LogTrace("MESSAGE_FLOW: HUB_DELIVER_MESSAGE | {MessageType} | Hub: {Address} | MessageId: {MessageId}",
            delivery.Message.GetType().Name, Address, delivery.Id);

        var ret = delivery.ChangeState(MessageDeliveryState.Submitted);
        var result = messageService.RouteMessageAsync(ret, default);

        logger.LogTrace("MESSAGE_FLOW: HUB_DELIVER_MESSAGE_RESULT | {MessageType} | Hub: {Address} | MessageId: {MessageId} | State: {State}",
            delivery.Message.GetType().Name, Address, delivery.Id, result.State);
        return result;
    }

    public IMessageHub? GetHostedHub(
        Address address,
        Func<MessageHubConfiguration, MessageHubConfiguration> config,
        HostedHubCreation create
    )
    {
        var messageHub = hostedHubs.GetHub(address, config, create);
        return messageHub;
    }

    public IMessageHub RegisterForDisposal(Action<IMessageHub> disposeAction)
    {
        disposeActions.Add(disposeAction);
        return this;
    }

    public IMessageHub RegisterForDisposal(Func<IMessageHub, CancellationToken, Task> disposeAction)
    {
        asyncDisposeActions.Add(disposeAction);
        return this;
    }

    public JsonSerializerOptions JsonSerializerOptions { get; }

    public bool IsDisposing => Disposal != null;

    public Task? Disposal { get; private set; }

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
    private readonly TaskCompletionSource disposingTaskCompletionSource = new();
    private readonly Stopwatch disposalStopwatch = new();

    private readonly Lock locker = new();

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
            Disposal = disposingTaskCompletionSource.Task;

        }

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

        // Safety net: if the normal shutdown path deadlocks for any reason,
        // force-complete the disposal task after a timeout to prevent indefinite hangs.
        // Budget breakdown: 10 s quiesce + 10 s hostedHubs.Disposal + ~2 s buffer drain
        // = ~22 s. The previous 5 s ceiling was tighter than the quiesce phase alone
        // and would short-circuit the new pipeline.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(25));
                if (!disposingTaskCompletionSource.Task.IsCompleted)
                {
                    logger.LogError(
                        "DISPOSAL DEADLOCK DETECTED: Hub {Address} did not complete shutdown within 25 seconds. " +
                        "RunLevel={RunLevel}. Force-completing disposal to prevent hang.",
                        Address, RunLevel);
                    disposingTaskCompletionSource.TrySetResult();
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in disposal safety timeout for hub {Address}", Address);
            }
        });
    }

    private void DisposeImpl()
    {

        while (disposeActions.TryTake(out var disposeAction))
            disposeAction.Invoke(this);

    }

    /// <summary>
    /// Multi-line snapshot of the hub's disposal state. Reports own RunLevel + Disposal-task
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

    public bool AnyHubQuiescingTimedOut() => AnyHubQuiescingTimedOut(depth: 0);

    private bool AnyHubQuiescingTimedOut(int depth)
    {
        if (QuiescingTimedOut) return true;
        if (depth >= MaxHostedHubRecursionDepth) return false;
        foreach (var child in hostedHubs.Hubs)
            if (child is MessageHub childMh && childMh.AnyHubQuiescingTimedOut(depth + 1)) return true;
        return false;
    }

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
          .Append(Disposal == null ? "<not started>"
              : Disposal.IsCompleted ? "Completed" : "Pending")
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
    private async Task<IMessageDelivery> HandleShutdown(
        IMessageDelivery<ShutdownRequest> request,
        CancellationToken ct
    )
    {
        var phaseStopwatch = Stopwatch.StartNew();
        logger.LogDebug("STARTING HandleShutdown for hub {Address}, RunLevel={RunLevel}, RequestVersion={RequestVersion}, total disposal time so far: {totalElapsed}ms",
            Address, request.Message.RunLevel, request.Message.Version, disposalStopwatch.ElapsedMilliseconds);


        // Process dispose actions first
        var disposeActionsStopwatch = Stopwatch.StartNew();
        var disposeActionCount = 0;
        while (asyncDisposeActions.TryTake(out var configurationDisposeAction))
        {
            var actionStopwatch = Stopwatch.StartNew();
            disposeActionCount++;
            try
            {
                logger.LogDebug("Executing dispose action {actionNumber} for hub {address}", disposeActionCount, Address);
                await configurationDisposeAction.Invoke(this, ct);
                logger.LogDebug("Completed dispose action {actionNumber} for hub {address} in {elapsed}ms",
                    disposeActionCount, Address, actionStopwatch.ElapsedMilliseconds);
            }
            catch (Exception e)
            {
                logger.LogError("Error in dispose action {actionNumber} for hub {address} after {elapsed}ms: {exception}",
                    disposeActionCount, Address, actionStopwatch.ElapsedMilliseconds, e);
                // Continue with other dispose actions
            }
        }
        if (disposeActionCount > 0)
        {
            logger.LogDebug("Completed {actionCount} dispose actions for hub {address} in {elapsed}ms",
                disposeActionCount, Address, disposeActionsStopwatch.ElapsedMilliseconds);
        }
        if (request.Message.Version != Version - 1)
        {
            logger.LogDebug("Version mismatch for hub {Address}: received {RequestVersion}, expected {ExpectedVersion}, IsDisposing={IsDisposing}",
                Address, request.Message.Version, Version - 1, IsDisposing);

            logger.LogDebug("Reposting ShutdownRequest with corrected version {NewVersion} for hub {Address}", Version, Address);
            Post(request.Message with { Version = Version });
            return request.Ignored();
        }

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

                var initialPendingSnapshot = SnapshotPendingCallbacks();
                TryLog(LogLevel.Information,
                    "[QUIESCE-START] {Address}: {Count} pending callbacks at dispose entry: {Pending}",
                    Address, initialPendingSnapshot.Length, FormatPendingCallbacks(initialPendingSnapshot));
                DisposeTrace(Address, "QUIESCE_START", disposalStopwatch.ElapsedMilliseconds,
                    $"pending={initialPendingSnapshot.Length}");

                // CRITICAL: do the wait OFF the action block. The action block processes
                // messages serially (MaxDegreeOfParallelism = 1) — if we await Task.Delay
                // on the action block thread, no other messages can be dequeued, including
                // the very response messages we're waiting for. That's a self-deadlock that
                // turns every dispose into a guaranteed QuiesceTimeout.
                //
                // Fire-and-forget Task.Run mirrors how the DisposeHostedHubs branch (below)
                // awaits hostedHubs.Disposal — the handler returns immediately, the action
                // block stays free to dispatch responses into HandleCallbacks, and the
                // continuation posts DisposeHostedHubs once we're done.
#pragma warning disable CS4014
                _ = Task.Run(async () =>
#pragma warning restore CS4014
                {
                    try
                    {
                        var quiesceSw = Stopwatch.StartNew();
                        while (quiesceSw.Elapsed < QuiesceTimeout)
                        {
                            bool empty;
                            lock (responseSubjects) empty = responseSubjects.Count == 0;
                            if (empty) break;
                            try { await Task.Delay(QuiescePollInterval); }
                            catch { break; }
                        }

                        int remainingCount;
                        lock (responseSubjects) remainingCount = responseSubjects.Count;
                        if (remainingCount == 0)
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
                            // Sticky flag — tests recursively inspect this and treat any
                            // hub with QuiescingTimedOut=true as a dispose failure. Forces
                            // visibility on leaked Observe subscriptions instead of letting
                            // them silently extend dispose budgets across the suite.
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
                        // Never let the Quiescing branch throw — that would leave the dispose
                        // state machine wedged at Quiescing forever (worse than the original
                        // hang). Log best-effort and fall through to the next phase post.
                        TryLog(LogLevel.Error,
                            "[QUIESCE-ERROR] {Address}: unexpected exception {Type}: {Message}; proceeding to DisposeHostedHubs anyway.",
                            Address, quiesceEx.GetType().Name, quiesceEx.Message);
                    }
                    finally
                    {
                        Post(new ShutdownRequest(MessageHubRunLevel.DisposeHostedHubs, Version));
                    }
                });
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
                hostedHubs.Dispose();
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                Task.Run(async () =>
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                {
                    try
                    {
                        TryLog(LogLevel.Debug, "[DISPOSE-TRACE] {address}: Awaiting hostedHubs.Disposal (IsCompleted={isCompleted})",
                            Address, hostedHubs.Disposal?.IsCompleted);
                        await hostedHubs.Disposal!;
                        TryLog(LogLevel.Debug, "[DISPOSE-TRACE] {address}: hostedHubs.Disposal completed in {elapsed}ms",
                            Address, disposeHostedHubsStopwatch.ElapsedMilliseconds);
                        DisposeTrace(Address, "HOSTED_DISPOSE_OK", disposeHostedHubsStopwatch.ElapsedMilliseconds);
                    }
                    catch (Exception e)
                    {
                        TryLog(LogLevel.Debug, "[DISPOSE-TRACE] {address}: hostedHubs.Disposal ERROR after {elapsed}ms: {error}",
                            Address, disposeHostedHubsStopwatch.ElapsedMilliseconds, e.Message);
                        DisposeTrace(Address, "HOSTED_DISPOSE_ERROR", disposeHostedHubsStopwatch.ElapsedMilliseconds,
                            $"{e.GetType().Name}: {e.Message}");
                    }
                    finally
                    {
                        // Defensive: this finally runs even if the catch's logger
                        // calls throw. The Post itself can throw if the hub is in
                        // an unexpected state — wrap so the state machine still
                        // tries to advance to ShutDown rather than wedging here.
                        try
                        {
                            TryLog(LogLevel.Debug, "[DISPOSE-TRACE] {address}: POSTING ShutDown request, Version={version}",
                                Address, Version);
                            Post(new ShutdownRequest(MessageHubRunLevel.ShutDown, Version));
                            DisposeTrace(Address, "POSTED_SHUTDOWN", disposeHostedHubsStopwatch.ElapsedMilliseconds);
                        }
                        catch (Exception postEx)
                        {
                            DisposeTrace(Address, "POSTED_SHUTDOWN_FAILED", disposeHostedHubsStopwatch.ElapsedMilliseconds,
                                $"{postEx.GetType().Name}: {postEx.Message}");
                            // Force-complete the disposal task so callers don't hang.
                            try { disposingTaskCompletionSource.TrySetException(postEx); } catch { }
                        }
                    }
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

                    logger.LogDebug("[DISPOSE-TRACE] {address}: Awaiting messageService.DisposeAsync()...", Address);
                    await messageService.DisposeAsync();
                    logger.LogDebug("[DISPOSE-TRACE] {address}: messageService.DisposeAsync() done in {elapsed}ms",
                        Address, shutdownStopwatch.ElapsedMilliseconds);

                    try
                    {
                        disposingTaskCompletionSource.TrySetResult();
                        logger.LogDebug("[DISPOSE-TRACE] {address}: Disposal COMPLETED in {elapsed}ms total",
                            Address, disposalStopwatch.ElapsedMilliseconds);
                    }
                    catch (InvalidOperationException)
                    {
                        // Task completion source was already set, ignore
                        logger.LogDebug("Disposal task completion source was already set for hub {address} (elapsed: {elapsed}ms)",
                            Address, shutdownStopwatch.ElapsedMilliseconds);
                    }
                }
                catch (Exception e)
                {
                    logger.LogError("Error during shutdown of hub {address} after {elapsed}ms (total disposal time: {totalElapsed}ms): {exception}",
                        Address, shutdownStopwatch.ElapsedMilliseconds, disposalStopwatch.ElapsedMilliseconds, e);
                    try
                    {
                        disposingTaskCompletionSource.TrySetException(e);
                    }
                    catch (InvalidOperationException)
                    {
                        // Task completion source was already set, ignore
                        logger.LogDebug("Disposal task completion source was already set for hub {address} during exception handling", Address);
                    }
                }
                finally
                {
                    RunLevel = MessageHubRunLevel.Dead;
                    disposalStopwatch.Stop();
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

    private static string FormatPendingCallbacks(
        (string MessageId, string RequestType, Address? Target, long AgeMs)[] pending)
    {
        if (pending.Length == 0)
            return "<none>";
        return string.Join(", ", pending.Select(p =>
            $"{p.MessageId}={p.RequestType}@{p.Target}({p.AgeMs}ms)"));
    }

    /// <summary>
    /// Per-hub Quiescing-phase budget. Configured via
    /// <see cref="MessageHubConfiguration.WithQuiesceTimeout"/>; defaults to 2 s.
    /// Tests with deliberately abandoned <c>Observe(...)</c> subscriptions should
    /// drop this to ~100-500 ms.
    /// </summary>
    private TimeSpan QuiesceTimeout => Configuration.QuiesceTimeout;
    private static readonly TimeSpan QuiescePollInterval = TimeSpan.FromMilliseconds(50);


    private readonly ConcurrentBag<Func<IMessageHub, CancellationToken, Task>> asyncDisposeActions = new();
    private readonly ConcurrentBag<Action<IMessageHub>> disposeActions = new();



    private readonly ConcurrentDictionary<(string Conext, Type Type), object?> properties = new();

    public void Set<T>(T obj, string context = "")
    {
        properties[(context, typeof(T))] = obj;
    }

    public T Get<T>(string context = "")
    {
        properties.TryGetValue((context, typeof(T)), out var ret);
        return (T)ret!;
    }


    private IMessageDelivery HandleDispose(IMessageDelivery<DisposeRequest> request)
    {
        Dispose();
        return request.Processed();
    }


    #region Registry
    public IDisposable Register<TMessage>(SyncDelivery<TMessage> action) =>
        Register(action, _ => true);

    public IDisposable Register<TMessage>(AsyncDelivery<TMessage> action) =>
        Register(action, _ => true);

    public IDisposable Register<TMessage>(
        SyncDelivery<TMessage> action,
        DeliveryFilter<TMessage> filter
    )
    {
        return Register((d, _) => Task.FromResult(action(d)), filter);
    }

    public IDisposable RegisterInherited<TMessage>(
        AsyncDelivery<TMessage> action,
        DeliveryFilter<TMessage>? filter = null
    )
    {
        var node = new LinkedListNode<AsyncDelivery>(
            (d, c) =>
                d is IMessageDelivery<TMessage> md && (filter?.Invoke(md) ?? true)
                    ? action(md, c)
                    : Task.FromResult(d)
        );
        rules.AddLast(node);
        return new AnonymousDisposable(() => rules.Remove(node));
    }

    public IDisposable Register(SyncDelivery delivery) =>
        Register((d, _) => Task.FromResult(delivery(d)));

    public IDisposable Register(AsyncDelivery delivery)
    {
        var node = new LinkedListNode<AsyncDelivery>(delivery);
        rules.AddFirst(node);
        return new AnonymousDisposable(() => rules.Remove(node));
    }

    public IDisposable RegisterInherited<TMessage>(
        SyncDelivery<TMessage> action,
        DeliveryFilter<TMessage>? filter = null
    ) => RegisterInherited((d, _) => Task.FromResult(action(d)), filter);

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

    public IDisposable Register(Type tMessage, AsyncDelivery action)
    {
        WithTypeAndRelatedTypesFor(tMessage);
        return Register(action, d => tMessage.IsInstanceOfType(d.Message));
    }

    public IDisposable Register(AsyncDelivery action, DeliveryFilter filter)
    {
        Task<IMessageDelivery> Rule
            (IMessageDelivery delivery, CancellationToken cancellationToken)
            => WrapFilter(delivery, action, filter, cancellationToken);
        var node = new LinkedListNode<AsyncDelivery>(Rule);
        rules.AddFirst(node);
        return new AnonymousDisposable(() =>
        {
            rules.Remove(node);
        });
    }

    private Task<IMessageDelivery> WrapFilter(
        IMessageDelivery delivery,
        AsyncDelivery action,
        DeliveryFilter filter,
        CancellationToken cancellationToken
    )
    {
        if (filter(delivery))
            return action(delivery, cancellationToken);
        return Task.FromResult(delivery);
    }

    public IDisposable Register(Type tMessage, SyncDelivery action) =>
        Register(tMessage, action, _ => true);

    public IDisposable Register(Type tMessage, SyncDelivery action, DeliveryFilter filter) =>
        Register(
            tMessage,
            (d, _) =>
            {
                d = action(d);
                return Task.FromResult(d);
            },
            filter
        );


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
