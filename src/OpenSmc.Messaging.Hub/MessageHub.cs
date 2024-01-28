using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace OpenSmc.Messaging;


public sealed class MessageHub<TAddress> : MessageHubBase<TAddress>, IMessageHub<TAddress>
{
    public override TAddress Address => (TAddress)MessageService.Address;
    void IMessageHub.Schedule(Func<Task> action) => MessageService.Schedule(action);
    public IServiceProvider ServiceProvider { get; }
    private readonly ConcurrentDictionary<string, List<AsyncDelivery>> callbacks = new();

    private readonly ILogger logger;
    public MessageHubConfiguration Configuration { get; }

    public MessageHub(IServiceProvider serviceProvider, HostedHubsCollection hostedHubs,
        MessageHubConfiguration configuration, IMessageHub parentHub) : base(serviceProvider)
    {
        MessageService.Initialize(DeliverMessageAsync);

        hostedHubs1 = hostedHubs;
        ServiceProvider = serviceProvider;
        logger = serviceProvider.GetRequiredService<ILogger<MessageHub<TAddress>>>();

        Configuration = configuration;


        disposeActions.AddRange(configuration.DisposeActions);

        var forwardConfig =
            (configuration.ForwardConfigurationBuilder ?? (x => x)).Invoke(new ForwardConfiguration(this));


        var routePlugin = new RoutePlugin(this, forwardConfig, parentHub);

        Register(HandleCallbacks);

        AddPlugin(routePlugin);
        AddPlugin(new SubscribersPlugin(configuration.ServiceProvider, this));


        foreach (var messageHandler in configuration.MessageHandlers)
            Register(messageHandler.MessageType, d => messageHandler.AsyncDelivery.Invoke(this, d));


        logger.LogInformation("Message hub {address} initialized", Address);

    }


    protected override async Task StartAsync()
    {
        var actions = Configuration.BuildupActions;
        foreach (var buildup in actions)
            await buildup(this);

        await base.StartAsync();
    }


    public override bool Filter(IMessageDelivery d) => true;

    public Task<IMessageDelivery<TResponse>> AwaitResponse<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken)
        => AwaitResponse(request, x => x, x => x, cancellationToken);

    public Task<IMessageDelivery<TResponse>> AwaitResponse<TResponse>(IRequest<TResponse> request, Func<PostOptions, PostOptions> options)
        => AwaitResponse(request, options, new CancellationTokenSource(IMessageHub.DefaultTimeout).Token);
    public Task<IMessageDelivery<TResponse>> AwaitResponse<TResponse>(IRequest<TResponse> request, Func<PostOptions, PostOptions> options,
        CancellationToken cancellationToken)
        => AwaitResponse(request, options, x => x, cancellationToken);

    public Task<TResult> AwaitResponse<TResponse, TResult>(IRequest<TResponse> request,
        Func<PostOptions, PostOptions> options, Func<IMessageDelivery<TResponse>, TResult> selector,
        CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<TResult>(cancellationToken);
        var callbackTask = RegisterCallback(Post(request, options), d =>
        {
            tcs.SetResult(selector((IMessageDelivery<TResponse>)d));
            return d.Processed();
        }, cancellationToken);
        return callbackTask.ContinueWith(_ => tcs.Task.Result, cancellationToken);
    }


    protected IMessageDelivery RegisterCallback<TMessage, TResponse>(IMessageDelivery<TMessage> request, SyncDelivery<TResponse> callback, CancellationToken cancellationToken)
        where TMessage : IRequest<TResponse>
        => RegisterCallback<TMessage, TResponse>(request, d => Task.FromResult(callback(d)), cancellationToken);

    protected IMessageDelivery RegisterCallback<TMessage, TResponse>(IMessageDelivery<TMessage> request, AsyncDelivery<TResponse> callback, CancellationToken cancellationToken)
        where TMessage : IRequest<TResponse>
    {
        RegisterCallback(request, d => callback((IMessageDelivery<TResponse>)d), cancellationToken);
        return request.Forwarded();
    }

    protected Task<IMessageDelivery> RegisterCallback(IMessageDelivery delivery, SyncDelivery callback, CancellationToken cancellationToken)
        => RegisterCallback(delivery, d => Task.FromResult(callback(d)), cancellationToken);


    // ReSharper disable once UnusedMethodReturnValue.Local
    public Task<IMessageDelivery> RegisterCallback(IMessageDelivery delivery, AsyncDelivery callback)
        => RegisterCallback(delivery, callback, new CancellationTokenSource(999).Token);
    public Task<IMessageDelivery> RegisterCallback(IMessageDelivery delivery, AsyncDelivery callback, CancellationToken cancellationToken)

    {
        var tcs = new TaskCompletionSource<IMessageDelivery>(cancellationToken);

        async Task<IMessageDelivery> ResolveCallback(IMessageDelivery d)
        {
            var ret = await callback(d);
            tcs.SetResult(ret);
            return ret;
        }

        callbacks.GetOrAdd(delivery.Id, _ => new()).Add(ResolveCallback);


        return tcs.Task;
    }
    private async Task<IMessageDelivery> HandleCallbacks(IMessageDelivery delivery)
    {
        if (!delivery.Properties.TryGetValue(PostOptions.RequestId, out var requestId) ||
            !callbacks.TryRemove(requestId.ToString(), out var myCallbacks))
            return delivery;

        foreach (var callback in myCallbacks)
            delivery = await callback(delivery);

        return delivery;
    }

    public Task<bool> FlushAsync() => MessageService.FlushAsync();







    object IMessageHub.Address => Address;





    public void ConnectTo(IMessageHub hub)
    {
        hub.DeliverMessage(new MessageDelivery<ConnectToHubRequest>()
        {
            Message = new ConnectToHubRequest(Address, hub.Address),
            Sender = Address,
            Target = hub.Address
        });
    }

    public void Disconnect(IMessageHub hub)
    {
        Post(new DisconnectHubRequest(Address), o => o.WithTarget(hub.Address));
    }

    public IMessageDelivery<TMessage> Post<TMessage>(TMessage message, Func<PostOptions, PostOptions> configure = null)
    {
        var options = new PostOptions(Address, this);
        if (configure != null)
            options = configure(options);

        return (IMessageDelivery<TMessage>)MessageService.Post(message, options);
    }

    public IMessageDelivery DeliverMessage(IMessageDelivery delivery)
    {
        var ret = delivery.ChangeState(MessageDeliveryState.Submitted);
        if (!IsDisposing)
            MessageService.IncomingMessage(ret);
        return ret;
    }




    public IMessageHub GetHostedHub<TAddress1>(TAddress1 address, Func<MessageHubConfiguration, MessageHubConfiguration> config)
    {
        var messageHub = hostedHubs1.GetHub(address, config);
        return messageHub;
    }

    public IMessageHub WithDisposeAction(Action<IMessageHub> disposeAction)
        => WithDisposeAction(hub =>
        {
            disposeAction.Invoke(hub);
            return Task.CompletedTask;
        });

    public IMessageHub WithDisposeAction(Func<IMessageHub, Task> disposeAction)
    {
        disposeActions.Add(disposeAction);
        return this;
    }

    private bool IsDisposing { get;  set; }

    private Task disposing;


    public void Log(Action<ILogger> log)
    {
        log(logger);
    }

    private readonly object locker = new();

    public override Task DisposeAsync()
    {
        lock (locker)
        {
            IsDisposing = true;
            if (disposing != null)
                return disposing;

            return disposing = DoDisposeAsync();
        }
    }


    private async Task DoDisposeAsync()
    {

        await hostedHubs1.DisposeAsync();

        Post(new DisconnectHubRequest(Address));
        await MessageService.DisposeAsync();

        foreach (var configurationDisposeAction in disposeActions)
            await configurationDisposeAction.Invoke(this);

        await base.DisposeAsync();
    }

    private readonly List<Func<IMessageHub, Task>> disposeActions = new();


    public void Dispose()
    {
        MessageService.Schedule(DisposeAsync);
    }



    private readonly List<object> deferrals = new();
    private IDisposable defaultDeferrals;

    class SelfDisposable : IDisposable
    {
        private readonly ICollection<object> collection;
        private readonly Action callback;

        public SelfDisposable(ICollection<object> collection, Action callback)
        {
            this.collection = collection;
            this.callback = callback;
            collection.Add(this);
        }

        public void Dispose()
        {
            collection.Remove(this);
            callback();
        }
    }

    public IDisposable Defer()
    {
        var deferral = new SelfDisposable(deferrals, ReleaseAllTypes);
        return deferral;
    }

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

    public void AddPlugin(IMessageHubPlugin plugin)
    {
        Register(async d =>
        { 
            d = await plugin.DeliverMessageAsync(d);
            return d;
        });
        WithDisposeAction(_ => plugin.DisposeAsync());
    }

 
    private readonly object releaseLock = new();
    private readonly HostedHubsCollection hostedHubs1;


    private void ReleaseAllTypes()
    {
        if (deferrals.Count > 0)
            return;

        lock (releaseLock)
        {
            if (defaultDeferrals == null)
                return;
            defaultDeferrals.Dispose();
            defaultDeferrals = null;

        }
    }



    IMessageDelivery IMessageHub.RegisterCallback<TResponse>(IMessageDelivery<IRequest<TResponse>> request,
        Func<IMessageDelivery<TResponse>, IMessageDelivery> callback, CancellationToken cancellationToken)
    {
        RegisterCallback(request, d => callback((IMessageDelivery<TResponse>)d), cancellationToken);
        return request.Forwarded();
    }
}



