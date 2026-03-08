using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq.Expressions;
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


    private readonly Dictionary<string, List<AsyncDelivery>> callbacks = new();
    private readonly HashSet<CancellationTokenSource> pendingCallbackCancellations = new();

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


        messageService = new MessageService(configuration.Address,
            serviceProvider.GetRequiredService<ILogger<MessageService>>(), this, parentHub);

        foreach (var disposeAction in configuration.DisposeActions)
            asyncDisposeActions.Add(disposeAction);

        JsonSerializerOptions = this.CreateJsonSerializationOptions(parentHub);

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
        foreach (var buildup in actions)
            await buildup(this, cancellationToken);

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








    public Task<object?> AwaitResponse(object r, Func<PostOptions, PostOptions> options, Func<IMessageDelivery, object?> selector, CancellationToken cancellationToken = default)
    {
        // Check if r is already a delivery (in which case it's already posted)
        if (r is IMessageDelivery existingDelivery)
        {
            var response = RegisterCallback(
                existingDelivery.Id,
                d => d,
                cancellationToken
            );
            return response.ContinueWith(t =>
            {
                var ret = t.Result;
                return InnerCallback(existingDelivery.Id, ret, selector);
            }, cancellationToken);
        }

        // For new messages, we need to generate the ID first, register callback, THEN post
        // to avoid race condition where response arrives before callback is registered
        var messageId = Guid.NewGuid().AsString();
        var response2 = RegisterCallback(messageId, d => d, cancellationToken);

        // Now post the message with the pre-generated ID
        var request = Post(r, opts => {
            var configured = options(opts);
            return configured.WithMessageId(messageId);
        })!;

        var task = response2
            .ContinueWith(t =>
            {
                var ret = t.Result;
                return InnerCallback(request.Id, ret, selector);

            },
                cancellationToken
            );
        return task;
    }

    private object? InnerCallback(
        string id,
        IMessageDelivery response,
        Func<IMessageDelivery, object?> selector)
    {

        try
        {
            return selector.Invoke(response);
        }
        catch (DeliveryFailureException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw new DeliveryFailureException($"Error while awaiting response for {id}", e);
        }
    }


    public Task<IMessageDelivery> RegisterCallback(
        string messageId,
        SyncDelivery callback,
        CancellationToken cancellationToken
    ) => RegisterCallback(messageId, (d, _) => Task.FromResult(callback(d)), cancellationToken);


    public Task<IMessageDelivery> RegisterCallback(IMessageDelivery delivery, AsyncDelivery callback,
        CancellationToken cancellationToken)
        => RegisterCallback(delivery.Id, callback, cancellationToken);

    public Task<IMessageDelivery> RegisterCallback(
        string messageId,
        AsyncDelivery callback,
        CancellationToken cancellationToken
    )
    {
        // Create a combined cancellation token that cancels when either the provided token
        // or disposal begins
        var disposalCts = new CancellationTokenSource();
        var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            disposalCts.Token);

        var tcs = new TaskCompletionSource<IMessageDelivery>(combinedCts.Token);

        // Register for disposal cancellation
        lock (locker)
        {
            if (RunLevel >= MessageHubRunLevel.ShutDown)
            {
                // If already disposing, immediately cancel the callback
                tcs.SetCanceled(combinedCts.Token);
                disposalCts.Dispose();
                combinedCts.Dispose();
                return tcs.Task;
            }

            // Store the disposal CTS so we can cancel it during disposal
            pendingCallbackCancellations.Add(disposalCts);
        }

        async Task<IMessageDelivery> ResolveCallback(IMessageDelivery d, CancellationToken ct)
        {
            try
            {
                // Clean up disposal cancellation token
                lock (locker)
                {
                    pendingCallbackCancellations.Remove(disposalCts);
                }
                disposalCts.Dispose();
                combinedCts.Dispose();
                if (d.Message is DeliveryFailure failure)
                {
                    tcs.SetException(new DeliveryFailureException(failure));
                    return d.Failed(failure.Message ?? "Delivery failed");
                }

                combinedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, cancellationToken);
                var ret = await callback(d, combinedCts.Token);
                tcs.SetResult(ret);
                return ret;
            }
            finally
            {
                combinedCts.Dispose();
            }
        }

        lock (callbacks)
        {
            logger.LogDebug("Adding callback for {Id}", messageId);
            callbacks.GetOrAdd(messageId, _ => new()).Add(ResolveCallback);
        }

        return tcs.Task;
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

    private async Task<IMessageDelivery> HandleCallbacks(
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
            return delivery;
        }

        List<AsyncDelivery>? myCallbacks;
        lock (callbacks)
        {
            if (!callbacks.Remove(requestIdString, out myCallbacks))
            {
                logger.LogDebug("No callbacks found for response message {MessageType} (ID: {MessageId}) - treating as processed",
                    delivery.Message.GetType().Name, delivery.Id);
                return delivery.Processed();
            }
        }

        logger.LogDebug("Resolving callbacks for | {MessageType} | Hub: {Address} | MessageId: {MessageId} | CallbackCount: {CallbackCount}",
            delivery.Message.GetType().Name, Address, delivery.Id, myCallbacks.Count);
        foreach (var callback in myCallbacks)
        {
            logger.LogTrace("MESSAGE_FLOW: HUB_CALLBACK_INVOKE | {MessageType} | Hub: {Address} | MessageId: {MessageId}",
                delivery.Message.GetType().Name, Address, delivery.Id);
            delivery = await callback(delivery, cancellationToken);
            logger.LogTrace("MESSAGE_FLOW: HUB_CALLBACK_RESULT | {MessageType} | Hub: {Address} | MessageId: {MessageId} | State: {State}",
                delivery.Message.GetType().Name, Address, delivery.Id, delivery.State);
        }

        logger.LogTrace("MESSAGE_FLOW: HUB_CALLBACKS_COMPLETE | {MessageType} | Hub: {Address} | MessageId: {MessageId}",
            delivery.Message.GetType().Name, Address, delivery.Id);
        return delivery.Processed();
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
        Post(new ShutdownRequest(MessageHubRunLevel.DisposeHostedHubs, Version));

        // Safety net: if the normal shutdown path deadlocks for any reason,
        // force-complete the disposal task after a timeout to prevent indefinite hangs.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                if (!disposingTaskCompletionSource.Task.IsCompleted)
                {
                    logger.LogError(
                        "DISPOSAL DEADLOCK DETECTED: Hub {Address} did not complete shutdown within 5 seconds. " +
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
            case MessageHubRunLevel.DisposeHostedHubs:
                var disposeHostedHubsStopwatch = Stopwatch.StartNew();
                lock (locker)
                {
                    if (RunLevel == MessageHubRunLevel.DisposeHostedHubs)
                    {
                        logger.LogWarning(
                            "DisposeHostedHubs already processed for hub {Address}, ignoring (phase time: {elapsed}ms)",
                            Address, phaseStopwatch.ElapsedMilliseconds);
                        return request.Ignored();
                    }

                    RunLevel = MessageHubRunLevel.DisposeHostedHubs;
                }

                hostedHubs.Dispose();
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                Task.Run(async () =>
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                {
                    try
                    {
                        logger.LogWarning("[DISPOSE-TRACE] {address}: Awaiting hostedHubs.Disposal (IsCompleted={isCompleted})",
                            Address, hostedHubs.Disposal?.IsCompleted);
                        await hostedHubs.Disposal!;
                        logger.LogWarning("[DISPOSE-TRACE] {address}: hostedHubs.Disposal completed in {elapsed}ms",
                            Address, disposeHostedHubsStopwatch.ElapsedMilliseconds);
                    }
                    catch (Exception e)
                    {
                        logger.LogWarning("[DISPOSE-TRACE] {address}: hostedHubs.Disposal ERROR after {elapsed}ms: {error}",
                            Address, disposeHostedHubsStopwatch.ElapsedMilliseconds, e.Message);
                    }
                    finally
                    {
                        logger.LogWarning("[DISPOSE-TRACE] {address}: POSTING ShutDown request, Version={version}",
                            Address, Version);
                        Post(new ShutdownRequest(MessageHubRunLevel.ShutDown, Version));
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
                            logger.LogWarning("[DISPOSE-TRACE] {address}: ShutDown already processed, ignoring", Address);
                            return request.Ignored();
                        }

                        logger.LogWarning("[DISPOSE-TRACE] {address}: STARTING ShutDown phase", Address);
                        RunLevel = MessageHubRunLevel.ShutDown;
                    }

                    CancelCallbacks();
                    DisposeImpl();

                    logger.LogWarning("[DISPOSE-TRACE] {address}: Awaiting messageService.DisposeAsync()...", Address);
                    await messageService.DisposeAsync();
                    logger.LogWarning("[DISPOSE-TRACE] {address}: messageService.DisposeAsync() done in {elapsed}ms",
                        Address, shutdownStopwatch.ElapsedMilliseconds);

                    try
                    {
                        disposingTaskCompletionSource.TrySetResult();
                        logger.LogWarning("[DISPOSE-TRACE] {address}: Disposal COMPLETED in {elapsed}ms total",
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
                    logger.LogDebug("Finished shutdown of hub {address} with parent {parent} - final phase took {elapsed}ms, total disposal time: {totalElapsed}ms",
                        Address, Configuration.ParentHub?.Address, phaseStopwatch.ElapsedMilliseconds, disposalStopwatch.ElapsedMilliseconds);
                }

                break;
        }

        return request.Processed();
    }

    private void CancelCallbacks()
    {
        // Cancel all pending callbacks to prevent them from waiting indefinitely
        var pendingCallbacks = pendingCallbackCancellations.ToArray();
        logger.LogDebug("Cancelling {callbackCount} pending callbacks during disposal for hub {address}",
            pendingCallbacks.Length, Address);

        foreach (var cts in pendingCallbacks)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Ignore - callback already completed and disposed
            }
        }
        pendingCallbackCancellations.Clear();

    }


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
