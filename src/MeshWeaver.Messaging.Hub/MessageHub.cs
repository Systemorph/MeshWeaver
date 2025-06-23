using System.Collections.Concurrent;
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

    public void InvokeAsync(Func<CancellationToken, Task> action, Action<Exception> exceptionCallback) =>
        Post(new ExecutionRequest(action, exceptionCallback));

    public IServiceProvider ServiceProvider { get; }
    private readonly ConcurrentDictionary<string, List<AsyncDelivery>> callbacks = new();

    private readonly ILogger logger;
    public MessageHubConfiguration Configuration { get; }
    private readonly HostedHubsCollection hostedHubs;

    public long Version { get; private set; }
    public MessageHubRunLevel RunLevel { get; private set; }
    private readonly IMessageService messageService;
    public ITypeRegistry TypeRegistry { get; }
    private readonly ThreadSafeLinkedList<AsyncDelivery> Rules = new();
    private readonly HashSet<Type> registeredTypes = new();
    private ILogger Logger { get; }
    public MessageHub(
        IServiceProvider serviceProvider,
        HostedHubsCollection hostedHubs,
        MessageHubConfiguration configuration,
        IMessageHub parentHub
    )
    {
        serviceProvider.Buildup(this);
        Logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(GetType());
        logger = serviceProvider.GetRequiredService<ILogger<MessageHub>>();

        logger.LogDebug("Starting MessageHub construction for address {Address}", configuration.Address);

        TypeRegistry = serviceProvider.GetRequiredService<ITypeRegistry>();
        InitializeTypes(this);

        this.hostedHubs = hostedHubs;
        ServiceProvider = serviceProvider;
        Configuration = configuration; messageService = new MessageService(configuration.Address,
            serviceProvider.GetRequiredService<ILogger<MessageService>>(), this, parentHub);

        foreach (var disposeAction in configuration.DisposeActions)
            disposeActions.Add(disposeAction);

        JsonSerializerOptions = this.CreateJsonSerializationOptions();

        Register<DisposeRequest>(HandleDispose);
        Register<ShutdownRequest>(HandleShutdown);
        Register<PingRequest>(HandlePingRequest);
        foreach (var messageHandler in configuration.MessageHandlers)
            Register(
                messageHandler.MessageType,
                (d, c) => messageHandler.AsyncDelivery.Invoke(this, d, c)
            ); Register(HandleCallbacks);
        Register(ExecuteRequest);

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
            if (registry.Action != null)
                Register(registry.Action, d => registry.Type.IsInstanceOfType(d.Message));

            registeredTypes.Add(registry.Type);
            WithTypeAndRelatedTypesFor(registry.Type);
        }
    }
    private void WithTypeAndRelatedTypesFor(Type typeToRegister)
    {
        if (typeToRegister == null) return;

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

    private TypeAndHandler GetTypeAndHandler(Type type, object instance)
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
                CreateDelivery(genericArgs.First(), type, instance, null)
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
        Expression cancellationToken
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

    private record TypeAndHandler(Type Type, AsyncDelivery Action);



    public bool IsDeferred(IMessageDelivery delivery)
    {
        return (Address.Equals(delivery.Target) || delivery.Target == null)
               && registeredTypes.Any(type => type.IsInstanceOfType(delivery.Message));
    }

    #endregion



    private async Task<IMessageDelivery> HandleMessageAsync(
        IMessageDelivery delivery,
        LinkedListNode<AsyncDelivery> node,
        CancellationToken cancellationToken
    )
    {
        delivery = await node.Value.Invoke(delivery, cancellationToken);

        if (node.Next == null)
            return delivery;

        return await HandleMessageAsync(delivery, node.Next, cancellationToken);
    }
    async Task<IMessageDelivery> IMessageHub.HandleMessageAsync(
        IMessageDelivery delivery,
        CancellationToken cancellationToken
    )
    {
        ++Version;

        // Log only important messages during disposal
        if (IsDisposing && delivery.Message is ShutdownRequest shutdownReq)
        {
            logger.LogInformation("Processing ShutdownRequest in {Address}: RunLevel={RunLevel}, Version={RequestVersion}, Expected={ExpectedVersion}",
                Address, shutdownReq.RunLevel, shutdownReq.Version, Version - 1);
        }

        delivery = await HandleMessageAsync(delivery, Rules.First, cancellationToken);

        return FinishDelivery(delivery);
    }

    private IMessageDelivery FinishDelivery(IMessageDelivery delivery)
    {
        return delivery.State == MessageDeliveryState.Submitted ? delivery.Ignored() : delivery;
    }

    private readonly TaskCompletionSource hasStarted = new();
    public Task HasStarted => hasStarted.Task; async Task IMessageHub.StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Message hub {address} initializing", Address);

        var actions = Configuration.BuildupActions;
        foreach (var buildup in actions)
            await buildup(this, cancellationToken);

        // Complete startup and allow deferred messages to flow
        messageService.CompleteStartup();

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
        => AwaitResponse(
            Post(request, options),
            selector,
            cancellationToken
        );
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
        var tcs = new TaskCompletionSource<IMessageDelivery>(cancellationToken);

        async Task<IMessageDelivery> ResolveCallback(IMessageDelivery d, CancellationToken ct)
        {

            if (d.Message is DeliveryFailure failure)
            {
                tcs.SetException(new DeliveryFailureException(failure));
                return d.Failed(failure.Message);
            }

            var ret = await callback(d, ct);
            tcs.SetResult(ret);
            return ret;
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
        if (
            !delivery.Properties.TryGetValue(PostOptions.RequestId, out var requestId)
            || !callbacks.TryRemove(requestId.ToString(), out var myCallbacks)
        )
            return delivery;

        foreach (var callback in myCallbacks)
            delivery = await callback(delivery, cancellationToken);

        return delivery;
    }

    Address IMessageHub.Address => Address; public IMessageDelivery<TMessage> Post<TMessage>(
        TMessage message,
        Func<PostOptions, PostOptions> configure = null
    )
    {
        var options = new PostOptions(Address);
        if (configure != null)
            options = configure(options);

        // Log only important messages during disposal
        if (IsDisposing && (message is ShutdownRequest || logger.IsEnabled(LogLevel.Debug)))
        {
            logger.LogInformation("Posting {MessageType} during disposal from {Sender} to {Target} in hub {Address}",
                typeof(TMessage).Name, options.Sender, options.Target, Address);
        }

        return (IMessageDelivery<TMessage>)messageService.Post(message, options);
    }

    public IMessageDelivery DeliverMessage(IMessageDelivery delivery)
    {
        var ret = delivery.ChangeState(MessageDeliveryState.Submitted);
        return messageService.RouteMessageAsync(ret, default);
    }

    public IMessageHub GetHostedHub<TAddress1>(
        TAddress1 address,
        Func<MessageHubConfiguration, MessageHubConfiguration> config,
        HostedHubCreation create
    )
        where TAddress1 : Address
    {
        var messageHub = hostedHubs.GetHub(address, config, create);
        return messageHub;
    }

    public IMessageHub RegisterForDisposal(Action<IMessageHub> disposeAction) =>
        RegisterForDisposal((hub, _) =>
        {
            disposeAction.Invoke(hub);
            return Task.CompletedTask;
        });

    public IMessageHub RegisterForDisposal(Func<IMessageHub, CancellationToken, Task> disposeAction)
    {
        disposeActions.Add(disposeAction);
        return this;
    }

    public JsonSerializerOptions JsonSerializerOptions { get; }

    public bool IsDisposing => Disposal != null;

    public Task Disposal { get; private set; }
    private readonly TaskCompletionSource disposingTaskCompletionSource = new();

    private readonly object locker = new(); public void Dispose()
    {
        lock (locker)
        {
            if (IsDisposing)
            {
                logger.LogWarning("Dispose() called multiple times for hub {address}", Address);
                return;
            }
            logger.LogInformation("STARTING DISPOSAL of hub {address}, current Version={Version}", Address, Version);
            Disposal = disposingTaskCompletionSource.Task;
        }

        logger.LogInformation("POSTING initial ShutdownRequest for hub {Address} with Version={Version}", Address, Version);
        Post(new ShutdownRequest(MessageHubRunLevel.DisposeHostedHubs, Version));
    }
    private async Task<IMessageDelivery> HandleShutdown(
        IMessageDelivery<ShutdownRequest> request,
        CancellationToken ct
    )
    {
        logger.LogInformation("STARTING HandleShutdown for hub {Address}, RunLevel={RunLevel}, RequestVersion={RequestVersion}",
            Address, request.Message.RunLevel, request.Message.Version);

        // Process dispose actions first
        while (disposeActions.TryTake(out var configurationDisposeAction))
        {
            try
            {
                await configurationDisposeAction.Invoke(this, ct);
            }
            catch (Exception e)
            {
                logger.LogError("Error in dispose action for hub {address}: {exception}", Address, e);
                // Continue with other dispose actions
            }
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
                try
                {
                    lock (locker)
                    {
                        if (RunLevel == MessageHubRunLevel.DisposeHostedHubs)
                        {
                            logger.LogWarning("DisposeHostedHubs already processed for hub {Address}, ignoring", Address);
                            return request.Ignored();
                        }

                        RunLevel = MessageHubRunLevel.DisposeHostedHubs;
                    }
                    logger.LogInformation("STARTING DisposeHostedHubs for hub {address}", Address);
                    hostedHubs.Dispose();
                    await hostedHubs.Disposal;
                    logger.LogInformation("COMPLETED DisposeHostedHubs for hub {address}", Address);
                    RunLevel = MessageHubRunLevel.HostedHubsDisposed;
                }
                catch (Exception e)
                {
                    logger.LogError("Error during disposal of hosted hubs: {exception}", e);
                }
                finally
                {
                    logger.LogInformation("POSTING ShutDown request after DisposeHostedHubs completion for hub {Address}, new Version={Version}", Address, Version);
                    Post(new ShutdownRequest(MessageHubRunLevel.ShutDown, Version));
                }

                break;
            case MessageHubRunLevel.ShutDown:
                try
                {
                    lock (locker)
                    {
                        if (RunLevel == MessageHubRunLevel.ShutDown)
                        {
                            logger.LogWarning("ShutDown already processed for hub {Address}, ignoring", Address);
                            return request.Ignored();
                        }

                        logger.LogInformation("STARTING ShutDown for hub {address}", Address);
                        RunLevel = MessageHubRunLevel.ShutDown;
                    }

                    await messageService.DisposeAsync();
                    logger.LogInformation("Message service disposed successfully for hub {address}", Address);

                    try
                    {
                        disposingTaskCompletionSource.TrySetResult();
                        logger.LogInformation("Disposal completed successfully for hub {address}", Address);
                    }
                    catch (InvalidOperationException)
                    {
                        // Task completion source was already set, ignore
                        logger.LogDebug("Disposal task completion source was already set for hub {address}", Address);
                    }
                }
                catch (Exception e)
                {
                    logger.LogError("Error during shutdown: {exception}", e);
                    try
                    {
                        disposingTaskCompletionSource.TrySetException(e);
                    }
                    catch (InvalidOperationException)
                    {
                        // Task completion source was already set, ignore
                        logger.LogDebug("Disposal task completion source was already set for hub {address}", Address);
                    }
                }
                finally
                {
                    RunLevel = MessageHubRunLevel.Dead;
                    //await ((IAsyncDisposable)ServiceProvider).DisposeAsync();
                    logger.LogDebug("Finished shutdown of hub {address}", Address);

                }

                break;
        }

        return request.Processed();
    }



    private readonly ConcurrentBag<Func<IMessageHub, CancellationToken, Task>> disposeActions = new();

    public IDisposable Defer(Predicate<IMessageDelivery> deferredFilter) =>
        messageService.Defer(deferredFilter);

    private readonly ConcurrentDictionary<(string Conext, Type Type), object> properties = new();

    public void Set<T>(T obj, string context = "")
    {
        properties[(context, typeof(T))] = obj;
    }

    public T Get<T>(string context = "")
    {
        properties.TryGetValue((context, typeof(T)), out var ret);
        return (T)ret;
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
        DeliveryFilter<TMessage> filter = null
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
        Rules.AddLast(node);
        return new AnonymousDisposable(() => Rules.Remove(node));
    }

    public IDisposable RegisterInherited<TMessage>(
        SyncDelivery<TMessage> action,
        DeliveryFilter<TMessage> filter = null
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
        registeredTypes.Add(tMessage);
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
        registeredTypes.Add(tMessage);
        WithTypeAndRelatedTypesFor(tMessage);
        return Register(
            (d, c) => action(d, c),
            d => tMessage.IsInstanceOfType(d.Message) && filter(d)
        );
    }
    #endregion
}
