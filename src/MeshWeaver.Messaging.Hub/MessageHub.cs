using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using MeshWeaver.Disposables;
using MeshWeaver.Domain;
using MeshWeaver.Reflection;
using MeshWeaver.ServiceProvider;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Messaging;

public sealed class MessageHub : IMessageHub
{
    public Address Address => Configuration.Address;

    public void InvokeAsync(Func<CancellationToken, Task> action) =>
        Post(new ExecutionRequest(action));

    public IServiceProvider ServiceProvider { get; }
    private readonly ConcurrentDictionary<string, List<AsyncDelivery>> callbacks = new();

    private readonly ILogger logger;
    public MessageHubConfiguration Configuration { get; }
    private readonly HostedHubsCollection hostedHubs;
    private readonly IDisposable deferral;
    public long Version { get; private set; }
    public MessageHubRunLevel RunLevel { get; private set; }
    private readonly IMessageService messageService;
    public ITypeRegistry TypeRegistry { get; }
    private readonly LinkedList<AsyncDelivery> Rules = new();
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
        TypeRegistry = serviceProvider.GetRequiredService<ITypeRegistry>();
        InitializeTypes(this);

        this.hostedHubs = hostedHubs;
        ServiceProvider = serviceProvider;
        logger = serviceProvider.GetRequiredService<ILogger<MessageHub>>();
        Configuration = configuration;

        messageService = new MessageService(configuration.Address,
            serviceProvider.GetRequiredService<ILogger<MessageService>>(), this, parentHub);
        deferral = messageService.Defer(d => d.Message is not ExecutionRequest);



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
            );

        Register(HandleCallbacks);
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



    public async Task<IMessageDelivery> HandleMessageAsync(
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
        
        Logger.LogDebug("Starting processing of {Delivery} in {Address}", delivery, Address);
        delivery = await HandleMessageAsync(delivery, Rules.First, cancellationToken);
        Logger.LogDebug("Finished processing of {Delivery} in {Address}", delivery, Address);

        return FinishDelivery(delivery);
    }

    private IMessageDelivery FinishDelivery(IMessageDelivery delivery)
    {
        // TODO V10: Add logging for failed messages, not found, etc. (31.01.2024, Roland Bürgi)
        return delivery.State == MessageDeliveryState.Submitted ? delivery.Ignored() : delivery;
    }

    private readonly TaskCompletionSource hasStarted = new();
    public Task HasStarted => hasStarted.Task;

    async Task IMessageHub.StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Message hub {address} initialized", Address);


        var actions = Configuration.BuildupActions;
        foreach (var buildup in actions)
            await buildup(this, cancellationToken);

        deferral.Dispose();
        RunLevel = MessageHubRunLevel.Started;

        hasStarted.SetResult();
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
        catch(DeliveryFailureException)
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

    Address IMessageHub.Address => Address;


    public IMessageDelivery<TMessage> Post<TMessage>(
        TMessage message,
        Func<PostOptions, PostOptions> configure = null
    )
    {
        var options = new PostOptions(Address);
        if (configure != null)
            options = configure(options);

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
        RegisterForDisposal((hub,_) =>
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

    public bool IsDisposing => Disposed != null;

    public Task Disposed { get; private set; }
    private readonly TaskCompletionSource disposingTaskCompletionSource = new();

    private readonly object locker = new();


    public void Dispose()
    {
        lock (locker)
        {
            if (IsDisposing)
                return;
            logger.LogDebug("Starting disposing of hub {address}", Address);
            Disposed = disposingTaskCompletionSource.Task;
        }
        Post(new ShutdownRequest(MessageHubRunLevel.DisposeHostedHubs, Version));
    }

    private async Task<IMessageDelivery> HandleShutdown(
        IMessageDelivery<ShutdownRequest> request,
        CancellationToken ct
    )
    {
        while (disposeActions.TryTake(out var configurationDisposeAction))
            await configurationDisposeAction.Invoke(this, ct);

        if (request.Message.Version != Version - 1)
        {
            Post(request.Message with { Version = Version });
            return request.Ignored();
        }

        switch (request.Message.RunLevel)
        {
            case MessageHubRunLevel.DisposeHostedHubs:
                try
                {
                    lock (locker)
                    {
                        if (RunLevel == MessageHubRunLevel.DisposeHostedHubs)
                            return request.Ignored();

                        RunLevel = MessageHubRunLevel.DisposeHostedHubs;
                    }
                    logger.LogDebug("Starting disposing hosted hubs of hub {address}", Address);
                    hostedHubs.Dispose();
                    RunLevel = MessageHubRunLevel.HostedHubsDisposed;
                    logger.LogDebug("Finish disposing hosted hubs of hub {address}", Address);
                }
                catch (Exception e)
                {
                    logger.LogError("Error during disposal of hosted hubs: {exception}", e);
                }
                finally
                {
                    Post(new ShutdownRequest(MessageHubRunLevel.ShutDown, Version));
                }

                break;
            case MessageHubRunLevel.ShutDown:
                try
                {
                    lock (locker)
                    {
                        if (RunLevel == MessageHubRunLevel.ShutDown)
                            return request.Ignored();

                        logger.LogDebug("Starting shutdown of hub {address}", Address);
                        RunLevel = MessageHubRunLevel.ShutDown;
                    }
                    hostedHubs.Dispose();
                    messageService.DisposeAsync().AsTask().ContinueWith(_ => disposingTaskCompletionSource.SetResult());
                }
                catch (Exception e)
                {
                    logger.LogError("Error during shutdown: {exception}", e);
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
        AsyncDelivery rule = (delivery, cancellationToken) =>
            WrapFilter(delivery, action, filter, cancellationToken);
        Rules.AddFirst(rule);
        return new AnonymousDisposable(() => Rules.Remove(rule));
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
