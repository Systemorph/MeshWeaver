using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenSmc.Messaging.Hub;

namespace OpenSmc.Messaging;


public class MessageHub<TAddress>(IServiceProvider serviceProvider, HostedHubsCollection hostedHubs) : MessageHubBase(serviceProvider), IMessageHub<TAddress>
{
    public TAddress Address => (TAddress)MessageService.Address;
    void IMessageHub.Schedule(Func<Task> action) => MessageService.Schedule(action);
    public IServiceProvider ServiceProvider { get; } = serviceProvider;

    protected readonly ILogger Logger = serviceProvider.GetRequiredService<ILogger<MessageHub<TAddress>>>();
    protected override IMessageHub Hub => this;
    private RoutePlugin routePlugin;
    private SubscribersPlugin subscribersPlugin;


    internal override void Initialize(MessageHubConfiguration configuration, ForwardConfiguration forwardConfiguration)
    {
        base.Initialize(configuration, forwardConfiguration);
        
        subscribersPlugin = new SubscribersPlugin(configuration.ServiceProvider);
        routePlugin = new RoutePlugin(configuration.ServiceProvider, forwardConfiguration);

        var deferredTypes = GetDeferredRequestTypes().ToHashSet();

        bool DefaultDeferralsLambda(IMessageDelivery d) =>
            deferredTypes.Contains(d.Message.GetType()) || configuration.Deferrals.Select(f => f(d))
                .DefaultIfEmpty()
                .Aggregate((x, y) => x || y);

        defaultDeferrals = MessageService.Defer(DefaultDeferralsLambda);

        foreach (var messageHandler in configuration.MessageHandlers)
            Register(messageHandler.MessageType, messageHandler.Action, messageHandler.Filter);


        MessageService.Schedule(StartAsync);
    }


    protected virtual async Task StartAsync()
    {
        await InitializeAsync();
        //Post(new HubInfo(Address));

        foreach (var buildup in Configuration.BuildupActions)
            await buildup(this);

        ReleaseAllTypes();
    }



    public virtual async Task InitializeAsync()
    {
        try
        {
            await AddPluginAsync(subscribersPlugin);

            await routePlugin.InitializeAsync(this);
            RegisterAfter(Rules.Last, d =>
            {
                if (!routePlugin.Filter(d))
                    return Task.FromResult(d);
                return routePlugin.DeliverMessageAsync(d);
            });

            Logger.LogInformation("Message hub {address} initialized", Address);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Could not initialize state of message hub {address}", Address);
        }
    }


    public Task<TResponse> AwaitResponse<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken)
        => AwaitResponse(request, x => x, x => x.Message, cancellationToken);

    public Task<TResponse> AwaitResponse<TResponse>(IRequest<TResponse> request, Func<PostOptions, PostOptions> options,
        CancellationToken cancellationToken = default)
        => AwaitResponse(request, options, x => x.Message, cancellationToken);

    public Task<TResult> AwaitResponse<TResponse, TResult>(IRequest<TResponse> request,
        Func<PostOptions, PostOptions> options, Func<IMessageDelivery<TResponse>, TResult> selector,
        CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<TResult>(cancellationToken);
        MessageService.Schedule(() =>
        {
            RegisterCallback(Post(request, options), d =>
            {
                tcs.SetResult(selector((IMessageDelivery<TResponse>)d));
                return d.Processed();
            }, cancellationToken);
        });
        return tcs.Task;
    }


    public virtual Task<bool> FlushAsync() => MessageService.FlushAsync();







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
        var messageHub = hostedHubs.GetHub(address, config);
        return messageHub;
    }

    protected bool IsDisposing { get; private set; }

    private readonly TaskCompletionSource disposing = new();


    public void Log(Action<ILogger> log)
    {
        log(Logger);
    }

    private readonly object locker = new();

    public override Task DisposeAsync()
    {
        lock (locker)
        {
            if (!IsDisposing)
            {
                IsDisposing = true;
                SyncDispose();
                return DoDisposeAsync();
            }

            return disposing.Task;
        }
    }

    private bool isAsyncDisposing;

    private async Task DoDisposeAsync()
    {
        lock (locker)
        {
            if (isAsyncDisposing)
                return;
            isAsyncDisposing = true;
        }

        await hostedHubs.DisposeAsync();

        Post(new DisconnectHubRequest(Address));
        await MessageService.DisposeAsync();

        foreach (var configurationDisposeAction in Configuration.DisposeActions)
            await configurationDisposeAction.Invoke(this);

        await base.DisposeAsync();
        disposing.SetResult();
    }

    private bool isSyncDisposed;

    public void Dispose()
    {
        SyncDispose();
    }

    private void SyncDispose()
    {
        lock (locker)
        {
            if (isSyncDisposed)
                return;
            isSyncDisposed = true;
        }

        foreach (var action in Configuration.DisposeActions)
            action(this);
    }

    protected virtual void HandleHeartbeat()
    {
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

    public async Task AddPluginAsync(IMessageHubPlugin plugin)
    {
        await plugin.InitializeAsync(this);
        Register(async d =>
        {
            if (plugin.Filter(d))
                d = await plugin.DeliverMessageAsync(d);
            return d;
        });
    }

 
    private readonly object releaseLock = new();

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

    protected virtual IEnumerable<Type> GetDeferredRequestTypes()
    {
        return GetType().GetInterfaces()
                .Where(t => t.IsGenericType &&
                            (t.GetGenericTypeDefinition() == typeof(IMessageHandler<>) ||
                             t.GetGenericTypeDefinition() == typeof(IMessageHandlerAsync<>)))
                .Select(t => t.GetGenericArguments()[0])
                .Where(t => t.Assembly != typeof(IMessageHub).Assembly)
                .Where(t => !t.IsGenericType || t.GetGenericTypeDefinition() != typeof(CreateRequest<>))
            ;
    }


    IMessageDelivery IMessageHub.RegisterCallback<TResponse>(IMessageDelivery<IRequest<TResponse>> request,
        Func<IMessageDelivery<TResponse>, IMessageDelivery> callback, CancellationToken cancellationToken)
    {
        RegisterCallback(request, d => callback((IMessageDelivery<TResponse>)d), cancellationToken);
        return request.Forwarded();
    }
}



