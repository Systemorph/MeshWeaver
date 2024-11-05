using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Messaging;

public record ShutdownRequest(MessageHubRunLevel RunLevel, long Version);

public enum MessageHubRunLevel
{
    Starting,
    Started,
    DisposeHostedHubs,
    HostedHubsDisposed,
    ShutDown,
    Dead
}

public record ExecutionRequest(Func<CancellationToken, Task> Action);

public sealed class MessageHub
    : MessageHubBase,
        IMessageHub,
        IMessageHandlerAsync<ShutdownRequest>
{
    public override object Address => MessageService.Address;

    public void InvokeAsync(Func<CancellationToken, Task> action) =>
        Post(new ExecutionRequest(action));

    public IServiceProvider ServiceProvider { get; }
    private readonly ConcurrentDictionary<string, List<AsyncDelivery>> callbacks = new();

    private readonly ILogger logger;
    public MessageHubConfiguration Configuration { get; }
    private readonly HostedHubsCollection hostedHubs;
    private readonly IDisposable deferral;
    public long Version { get; private set; }
    private RouteService routeService;
    public MessageHubRunLevel RunLevel { get; private set; }

    public MessageHub(
        IServiceProvider serviceProvider,
        HostedHubsCollection hostedHubs,
        MessageHubConfiguration configuration,
        IMessageHub parentHub
    )
        : base(serviceProvider)
    {
        deferral = MessageService.Defer(d => d.Message is not ExecutionRequest);

        this.hostedHubs = hostedHubs;
        ServiceProvider = serviceProvider;
        logger = serviceProvider.GetRequiredService<ILogger<MessageHub>>();
        Configuration = configuration;
        routeService = new RouteService(parentHub, this);

        foreach (var disposeAction in configuration.DisposeActions) 
            disposeActions.Add(disposeAction);

        JsonSerializerOptions = this.CreateJsonSerializationOptions();

        Register(HandleCallbacks);
        Register(ExecuteRequest);

        foreach (var messageHandler in configuration.MessageHandlers)
            Register(
                messageHandler.MessageType,
                (d, c) => messageHandler.AsyncDelivery.Invoke(this, d, c)
            );

        MessageService.Start(this);

    }


    async Task<IMessageDelivery> IMessageHub.DeliverMessageAsync(
        IMessageDelivery delivery,
        CancellationToken cancellationToken
    )
    {
        ++Version; 
        
        Logger.LogDebug("Starting processing of {Delivery} in {Address}", delivery, Address);
        delivery = await base.DeliverMessageAsync(delivery, cancellationToken);
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
        Hub = this;
        logger.LogInformation("Message hub {address} initialized", Address);


        var actions = Configuration.BuildupActions;
        foreach (var buildup in actions)
            await buildup(this, cancellationToken);

        deferral.Dispose();
        RunLevel = MessageHubRunLevel.Started;

        hasStarted.SetResult();
    }

    public override bool Filter(IMessageDelivery d) => true;

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
        var tcs = new TaskCompletionSource<TResult>(cancellationToken);
        var callbackTask = RegisterCallback(
            request,
            d =>
            {
                tcs.SetResult(selector((IMessageDelivery<TResponse>)d));
                return d.Processed();
            },
            cancellationToken
        );
        return callbackTask.ContinueWith(_ => tcs.Task.Result, cancellationToken);
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
            var ret = await callback(d, ct);

            if (d.Message is DeliveryFailure failure) 
                tcs.SetException(new DeliveryFailureException(failure));
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

    object IMessageHub.Address => Address;


    public IMessageDelivery<TMessage> Post<TMessage>(
        TMessage message,
        Func<PostOptions, PostOptions> configure = null
    )
    {
        var options = new PostOptions(Address);
        if (configure != null)
            options = configure(options);

        return (IMessageDelivery<TMessage>)MessageService.Post(message, options);
    }

    public IMessageDelivery DeliverMessage(IMessageDelivery delivery)
    {
        var ret = delivery.ChangeState(MessageDeliveryState.Submitted);
        MessageService.IncomingMessage(ret);
        return ret;
    }

    public IMessageHub GetHostedHub<TAddress1>(
        TAddress1 address,
        Func<MessageHubConfiguration, MessageHubConfiguration> config
    )
    {
        var messageHub = hostedHubs.GetHub(address, config);
        return messageHub;
    }

    public IMessageHub WithDisposeAction(Action<IMessageHub> disposeAction) =>
        WithDisposeAction(hub =>
        {
            disposeAction.Invoke(hub);
            return Task.CompletedTask;
        });

    public IMessageHub WithDisposeAction(Func<IMessageHub, Task> disposeAction)
    {
        disposeActions.Add(disposeAction);
        return this;
    }

    public JsonSerializerOptions JsonSerializerOptions { get; }

    private bool IsDisposing => disposingTaskCompletionSource != null;

    private TaskCompletionSource disposingTaskCompletionSource;

    private readonly object locker = new();

    private static readonly TimeSpan ShutDownTimeout = TimeSpan.FromSeconds(10);

    public void Dispose()
    {
        lock (locker)
        {
            if (!IsDisposing)
            {
                logger.LogDebug("Starting disposing of hub {address}", Address);
                disposingTaskCompletionSource = new(new CancellationTokenSource(ShutDownTimeout).Token);
            }
        }
        Post(new ShutdownRequest(MessageHubRunLevel.DisposeHostedHubs, Version));
    }

    async Task<IMessageDelivery> IMessageHandlerAsync<ShutdownRequest>.HandleMessageAsync(
        IMessageDelivery<ShutdownRequest> request,
        CancellationToken ct
    )
    {
        while (disposeActions.TryTake(out var configurationDisposeAction))
            await configurationDisposeAction.Invoke(this);


        if (request.Message.Version != Version - 1)
        {
            Post(request.Message with { Version = Version });
            return request.Ignored();
        }

        HandleShutdown(request);
        return request.Forwarded();
    }

    private async void HandleShutdown(IMessageDelivery<ShutdownRequest> request)
    {
        switch (request.Message.RunLevel)
        {
            case MessageHubRunLevel.DisposeHostedHubs:
                try
                {
                    lock (locker)
                    {
                        if (RunLevel == MessageHubRunLevel.DisposeHostedHubs)
                            return;

                        logger.LogDebug("Starting disposing hosted hubs of hub {address}", Address);
                        RunLevel = MessageHubRunLevel.DisposeHostedHubs;
                    }
                    await hostedHubs.DisposeAsync();
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
                            return;

                        logger.LogDebug("Starting shutdown of hub {address}", Address);
                        RunLevel = MessageHubRunLevel.ShutDown;
                    }

                    await ShutdownAsync();

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

                    disposingTaskCompletionSource.SetResult();
                }

                break;
        }
    }

    public override Task DisposeAsync()
    {
        if (!IsDisposing)
            Dispose();
        return disposingTaskCompletionSource.Task;
    }

    private async Task ShutdownAsync()
    {
        await hostedHubs.DisposeAsync();

        await MessageService.DisposeAsync();
        await base.DisposeAsync();
    }

    private readonly ConcurrentBag<Func<IMessageHub, Task>> disposeActions = new();

    public IDisposable Defer(Predicate<IMessageDelivery> deferredFilter) =>
        MessageService.Defer(deferredFilter);

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


}
