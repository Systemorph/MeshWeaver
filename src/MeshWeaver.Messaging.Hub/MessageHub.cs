using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using MeshWeaver.Domain;
using MeshWeaver.Reflection;
using MeshWeaver.ServiceProvider;
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
    private readonly ConcurrentDictionary<string, List<AsyncDelivery>> callbacks = new();
    private readonly HashSet<CancellationTokenSource> pendingCallbackCancellations = new();

    private readonly ILogger logger;
    public MessageHubConfiguration Configuration { get; }
    private readonly HostedHubsCollection hostedHubs;

    public long Version { get; private set; }
    public MessageHubRunLevel RunLevel { get; private set; }
    private readonly IMessageService messageService;
    public ITypeRegistry TypeRegistry { get; }
    private readonly ThreadSafeLinkedList<AsyncDelivery> Rules = new();
    private readonly HashSet<Type> registeredTypes = new();
    private readonly Lock registeredTypesLock = new();
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

        logger.LogDebug("Starting MessageHub construction for address {Address}", configuration.Address);

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

        messageService.Start();

    }

    private IMessageDelivery HandlePingRequest(IMessageDelivery<PingRequest> request)
    {
        Post(new PingResponse(), o => o.ResponseFor(request));
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

            lock (registeredTypesLock)
            {
                registeredTypes.Add(registry.Type);
            }
            WithTypeAndRelatedTypesFor(registry.Type);
        }
    }
    private void WithTypeAndRelatedTypesFor(Type? typeToRegister)
    {
        if (typeToRegister == null) return;

        lock (typeRegistryLock)
        {
            logger.LogDebug("Registering type {TypeName} and related types in hub {Address}", typeToRegister.Name, Address);

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

            logger.LogDebug("Completed type registration for {TypeName} in hub {Address}", typeToRegister.Name, Address);
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



    public bool IsDeferred(IMessageDelivery delivery)
    {
        lock (registeredTypesLock)
        {
            return (Address.Equals(delivery.Target) || delivery.Target == null)
                   && registeredTypes.Any(type => type.IsInstanceOfType(delivery.Message));
        }
    }

    #endregion



    private async Task<IMessageDelivery> HandleMessageAsync(
        IMessageDelivery delivery,
        LinkedListNode<AsyncDelivery> node,
        CancellationToken cancellationToken
    )
    {
        logger.LogTrace("MESSAGE_FLOW: HUB_RULE_INVOKE | {MessageType} | Hub: {Address} | MessageId: {MessageId}", 
            delivery.Message.GetType().Name, Address, delivery.Id);
        
        delivery = await node.Value.Invoke(delivery, cancellationToken);
        
        logger.LogTrace("MESSAGE_FLOW: HUB_RULE_RESULT | {MessageType} | Hub: {Address} | MessageId: {MessageId} | State: {State}", 
            delivery.Message.GetType().Name, Address, delivery.Id, delivery.State);

        if (node.Next == null)
        {
            logger.LogTrace("MESSAGE_FLOW: HUB_RULES_COMPLETE | {MessageType} | Hub: {Address} | MessageId: {MessageId}", 
                delivery.Message.GetType().Name, Address, delivery.Id);
            return delivery;
        }

        logger.LogTrace("MESSAGE_FLOW: HUB_NEXT_RULE | {MessageType} | Hub: {Address} | MessageId: {MessageId}", 
            delivery.Message.GetType().Name, Address, delivery.Id);
        return await HandleMessageAsync(delivery, node.Next, cancellationToken);
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
            logger.LogInformation("Processing ShutdownRequest in {Address}: RunLevel={RunLevel}, Version={RequestVersion}, Expected={ExpectedVersion}",
                Address, shutdownReq.RunLevel, shutdownReq.Version, Version - 1);
        }

        if (Rules.First != null)
        {
            logger.LogTrace("MESSAGE_FLOW: HUB_PROCESSING_RULES | {MessageType} | Hub: {Address} | MessageId: {MessageId} | RuleCount: {RuleCount}", 
                delivery.Message.GetType().Name, Address, delivery.Id, Rules.Count);
            delivery = await HandleMessageAsync(delivery, Rules.First, cancellationToken);
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
        return delivery.State == MessageDeliveryState.Submitted ? delivery.Ignored() : delivery;
    }

    private readonly TaskCompletionSource hasStarted = new();
    public Task Started => hasStarted.Task; 
    
    
    async Task IMessageHub.StartAsync(CancellationToken cancellationToken)
    {
        logger.LogDebug("Message hub {address} initializing", Address);

        var actions = Configuration.BuildupActions;
        foreach (var buildup in actions)
            await buildup(this, cancellationToken);

        RunLevel = MessageHubRunLevel.Started;
        hasStarted.SetResult();

        logger.LogInformation("Message hub {address} fully initialized", Address);
    }


    public Task<IMessageDelivery<TResponse>> AwaitResponse<TResponse>(
        IMessageDelivery<IRequest<TResponse>> request, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<IMessageDelivery<TResponse>>(cancellationToken);
        var callbackTask = RegisterCallback(
            request,
            d =>
            {
                tcs.SetResult((IMessageDelivery<TResponse>)d);
                return d.Processed();
            },
            cancellationToken
        );
        return callbackTask.ContinueWith(_ => tcs.Task.Result, cancellationToken);
    }
    public Task<IMessageDelivery<TResponse>> AwaitResponse<TResponse>(
        IRequest<TResponse> request,
        CancellationToken cancellationToken
    ) => AwaitResponse(request, x => x, x => x, cancellationToken);

    public Task<IMessageDelivery<TResponse>> AwaitResponse<TResponse>(
        IRequest<TResponse> request,
        Func<PostOptions, PostOptions> options
    ) =>
        AwaitResponse(
            request,
            options,
            new CancellationTokenSource(IMessageHub.DefaultTimeout).Token
        );

    public Task<IMessageDelivery<TResponse>> AwaitResponse<TResponse>(
        IRequest<TResponse> request,
        Func<PostOptions, PostOptions> options,
        CancellationToken cancellationToken
    ) => AwaitResponse(request, options, x => x, cancellationToken);

    public Task<TResult> AwaitResponse<TResponse, TResult>(
        IRequest<TResponse> request,
        Func<PostOptions, PostOptions> options,
        Func<IMessageDelivery<TResponse>, TResult> selector,
        CancellationToken cancellationToken
    )
    {
        var delivery = Post(request, options);
        return delivery is null ?
                Task.FromException<TResult>(new ObjectDisposedException("hub is disposed"))
            : AwaitResponse(
            delivery,
            selector,
            cancellationToken
        );
    }

    public Task<TResult> AwaitResponse<TResponse, TResult>(
        IMessageDelivery request,
        Func<IMessageDelivery<TResponse>, TResult> selector,
        CancellationToken cancellationToken
    )
    {
        var response = RegisterCallback(
            request,
            d => d,
            cancellationToken
        );
        var task = response
            .ContinueWith(t =>
                    InnerCallback(request, t.Result, selector),
                cancellationToken
            );
        return task;
    }

    private TResult InnerCallback<TResponse, TResult>(
        IMessageDelivery request,
        IMessageDelivery response,
        Func<IMessageDelivery<TResponse>, TResult> selector)
    {
        try
        {
            if (response is IMessageDelivery<TResponse> tResponse)
                return selector.Invoke(tResponse);
            throw new DeliveryFailureException($"Response for {request} was of unexpected type: {response}");
        }
        catch (DeliveryFailureException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw new DeliveryFailureException($"Error while awaiting response for {request}", e);
        }
    }

    public IMessageDelivery RegisterCallback<TMessage, TResponse>(
        IMessageDelivery<TMessage> request,
        SyncDelivery<TResponse> callback,
        CancellationToken cancellationToken
    )
        where TMessage : IRequest<TResponse> =>
        RegisterCallback<TMessage, TResponse>(
            request,
            (d, _) => Task.FromResult(callback(d)),
            cancellationToken
        );

    public IMessageDelivery RegisterCallback<TMessage, TResponse>(
        IMessageDelivery<TMessage> request,
        AsyncDelivery<TResponse> callback,
        CancellationToken cancellationToken
    )
        where TMessage : IRequest<TResponse>
    {
        RegisterCallback(
            request,
            (d, c) => callback.Invoke((IMessageDelivery<TResponse>)d, c),
            cancellationToken
        );
        return request.Forwarded();
    }

    public Task<IMessageDelivery> RegisterCallback(
        IMessageDelivery delivery,
        SyncDelivery callback,
        CancellationToken cancellationToken
    ) => RegisterCallback(delivery, (d, _) => Task.FromResult(callback(d)), cancellationToken);

    // ReSharper disable once UnusedMethodReturnValue.Local
    public Task<IMessageDelivery> RegisterCallback(
        IMessageDelivery delivery,
        AsyncDelivery callback
    ) => RegisterCallback(delivery, callback, default);

    public Task<IMessageDelivery> RegisterCallback(
        IMessageDelivery delivery,
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
            if (IsDisposing)
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

        callbacks.GetOrAdd(delivery.Id, _ => new()).Add(ResolveCallback);

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
            || !callbacks.TryRemove(requestIdString, out var myCallbacks)
        )
        {
            logger.LogTrace("MESSAGE_FLOW: HUB_NO_CALLBACKS | {MessageType} | Hub: {Address} | MessageId: {MessageId}", 
                delivery.Message.GetType().Name, Address, delivery.Id);
            return delivery;
        }

        logger.LogInformation("Resolving callbacks for | {MessageType} | Hub: {Address} | MessageId: {MessageId} | CallbackCount: {CallbackCount}", 
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
        return delivery;
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
            logger.LogInformation("Posting {MessageType} during disposal from {Sender} to {Target} in hub {Address}",
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

    public IMessageHub? GetHostedHub<TAddress1>(
        TAddress1 address,
        Func<MessageHubConfiguration, MessageHubConfiguration> config,
        HostedHubCreation create
    )
        where TAddress1 : Address
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
                logger.LogWarning("Dispose() called multiple times for hub {address} (elapsed: {elapsed}ms)", Address, totalStopwatch.ElapsedMilliseconds);
                return;
            }
            logger.LogInformation("STARTING DISPOSAL of hub {address}, current Version={Version}, hosted hubs count: {hostedHubsCount}", 
                Address, Version, hostedHubs.Hubs.Count());
            
            disposalStopwatch.Start();
            Disposal = disposingTaskCompletionSource.Task;

        }

        // Log all hosted hubs that will be disposed
        var hostedHubAddresses = hostedHubs.Hubs.Select(h => h.Address.ToString()).ToArray();
        if (hostedHubAddresses.Length > 0)
        {
            logger.LogInformation("Hub {address} has {count} hosted hubs to dispose: [{hubAddresses}]", 
                Address, hostedHubAddresses.Length, string.Join(", ", hostedHubAddresses));
        }
        else
        {
            logger.LogInformation("Hub {address} has no hosted hubs to dispose", Address);
        }

        logger.LogInformation("POSTING initial ShutdownRequest for hub {Address} with Version={Version} (disposal preparation took {elapsed}ms)", 
            Address, Version, totalStopwatch.ElapsedMilliseconds);
        Post(new ShutdownRequest(MessageHubRunLevel.DisposeHostedHubs, Version));
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
        logger.LogInformation("STARTING HandleShutdown for hub {Address}, RunLevel={RunLevel}, RequestVersion={RequestVersion}, total disposal time so far: {totalElapsed}ms",
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
            logger.LogInformation("Completed {actionCount} dispose actions for hub {address} in {elapsed}ms", 
                disposeActionCount, Address, disposeActionsStopwatch.ElapsedMilliseconds);
        }
        if (request.Message.Version != Version - 1)
        {
            logger.LogInformation("Version mismatch for hub {Address}: received {RequestVersion}, expected {ExpectedVersion}, IsDisposing={IsDisposing}",
                Address, request.Message.Version, Version - 1, IsDisposing);

            // During disposal, proceed regardless of version mismatch to avoid loops
            if (IsDisposing)
            {
                logger.LogInformation("Proceeding with shutdown despite version mismatch during disposal for hub {address}", Address);
            }
            else
            {
                logger.LogDebug("Reposting ShutdownRequest with corrected version {NewVersion} for hub {Address}", Version, Address);
                Post(request.Message with { Version = Version });
                return request.Ignored();
            }
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
                Task.Run(async () =>
                {
                    try
                    {
                        logger.LogDebug("Awaiting disposal for hosted hubs in {address}", Address);
                        await hostedHubs.Disposal!;

                    }
                    catch (Exception e)
                    {
                        logger.LogError(
                            "Error during disposal of hosted hubs for hub {address} after {elapsed}ms (total disposal time: {totalElapsed}ms): {exception}",
                            Address, disposeHostedHubsStopwatch.ElapsedMilliseconds,
                            disposalStopwatch.ElapsedMilliseconds, e);
                    }
                    finally
                    {
                        logger.LogDebug(
                            "POSTING ShutDown request after DisposeHostedHubs initiation for hub {Address}, new Version={Version} (phase took {elapsed}ms)",
                            Address, Version, phaseStopwatch.ElapsedMilliseconds);
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
                            logger.LogWarning("ShutDown already processed for hub {Address}, ignoring (phase time: {elapsed}ms)", 
                                Address, phaseStopwatch.ElapsedMilliseconds);
                            return request.Ignored();
                        }

                        logger.LogInformation("STARTING ShutDown for hub {address} (total disposal time so far: {totalElapsed}ms)", 
                            Address, disposalStopwatch.ElapsedMilliseconds);
                        RunLevel = MessageHubRunLevel.ShutDown;
                    }

                    CancelCallbacks();
                    DisposeImpl();

                    var messageServiceStopwatch = Stopwatch.StartNew();
                    logger.LogDebug("Disposing message service for hub {address}", Address);
                    await messageService.DisposeAsync();
                    logger.LogInformation("Message service disposed successfully for hub {address} in {elapsed}ms", 
                        Address, messageServiceStopwatch.ElapsedMilliseconds);

                    try
                    {
                        disposingTaskCompletionSource.TrySetResult();
                        logger.LogInformation("Disposal completed successfully for hub {address} in {elapsed}ms (total disposal time: {totalElapsed}ms)", 
                            Address, shutdownStopwatch.ElapsedMilliseconds, disposalStopwatch.ElapsedMilliseconds);
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
                    logger.LogInformation("Finished shutdown of hub {address} - final phase took {elapsed}ms, total disposal time: {totalElapsed}ms", 
                        Address, phaseStopwatch.ElapsedMilliseconds, disposalStopwatch.ElapsedMilliseconds);
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

    public IDisposable Defer(Predicate<IMessageDelivery> deferredFilter) =>
        messageService.Defer(deferredFilter);

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
        Rules.AddLast(node);
        return new AnonymousDisposable(() => Rules.Remove(node));
    }

    public IDisposable Register(SyncDelivery delivery) =>
        Register((d, _) => Task.FromResult(delivery(d)));

    public IDisposable Register(AsyncDelivery delivery)
    {
        var node = new LinkedListNode<AsyncDelivery>(delivery);
        Rules.AddFirst(node);
        return new AnonymousDisposable(() => Rules.Remove(node));
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
            d => (d.Target == null || Address.Equals(d.Target)) && d is IMessageDelivery<TMessage> md && filter(md)
        );
    }

    public IDisposable Register(Type tMessage, AsyncDelivery action)
    {
        lock (registeredTypesLock)
        {
            registeredTypes.Add(tMessage);
        }
        WithTypeAndRelatedTypesFor(tMessage);
        return Register(action, d => tMessage.IsInstanceOfType(d.Message));
    }

    public IDisposable Register(AsyncDelivery action, DeliveryFilter filter)
    {
        Task<IMessageDelivery> Rule
            (IMessageDelivery delivery, CancellationToken cancellationToken)
            => WrapFilter(delivery, action, filter, cancellationToken);
        var node = new LinkedListNode<AsyncDelivery>(Rule);
        Rules.AddFirst(node);
        return new AnonymousDisposable(() =>
        {
            Rules.Remove(node);
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
        lock (registeredTypesLock)
        {
            registeredTypes.Add(tMessage);
        }
        WithTypeAndRelatedTypesFor(tMessage);
        return Register(
            (d, c) => action(d, c),
            d => tMessage.IsInstanceOfType(d.Message) && filter(d)
        );
    }
    #endregion
}
