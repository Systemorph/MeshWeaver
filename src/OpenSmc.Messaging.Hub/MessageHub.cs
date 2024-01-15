using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OpenSmc.Messaging.Hub;

public class MessageHub : IMessageHub
{
    public MessageHub(IServiceProvider serviceProvider)
        : base(serviceProvider)
    {
    }
}


public class MessageHub<THub, TAddress> : MessageHubHandler,
                                                  IMessageHub<TAddress>,
                                                  IMessageHandler<DeleteHubRequest>,
                                                  IMessageHandler<ConnectToHubRequest>,
                                                  IMessageHandler<HeartbeatEvent>
    where THub : MessageHub<THub, TAddress>
{
    public TAddress Address => (TAddress)Me;
    void IMessageHub.Schedule(Func<Task> action) => MessageService.Schedule(action);

    public IServiceProvider ServiceProvider { get; }

    protected HostedHubsCollection HostedHubsCollection;
    protected readonly ILogger Logger;

    protected MessageHub(IServiceProvider serviceProvider)
        : base(serviceProvider.GetRequiredService<IEventsRegistry>())
    {
        ServiceProvider = serviceProvider;
        Logger = serviceProvider.GetRequiredService<ILogger<THub>>();
    }

    public override void Initialize(IMessageHub hub, MessageHubConfiguration configuration)
    {
        base.Initialize(hub, configuration);

        var deferredTypes = GetDeferredRequestTypes().ToHashSet();
        DeliveryFilter defaultDeferralsLambda = d => deferredTypes.Contains(d.Message.GetType()) || configuration.Deferrals.Select(f => f(d)).DefaultIfEmpty().Aggregate((x, y) => x || y);
        defaultDeferrals = MessageService.Defer(x => defaultDeferralsLambda(x));

        HostedHubsCollection = new(Configuration, this);

        foreach (var messageHandler in configuration.MessageHandlers)
            Register(messageHandler.MessageType, messageHandler.Action, messageHandler.Filter);

        foreach (var startConf in configuration.StartConfigurations)
        {
            var messageHub = GetHub(startConf.Address);
            foreach (object creationObject in startConf.CreationObjects)
            {
                messageHub.Post(creationObject);
            }
        }
        MessageService.Schedule(StartAsync);
    }

    protected virtual async Task StartAsync()
    {
        await InitializeAsync();
        Post(new HubInfo(Address));

        foreach (var buildup in Configuration.BuildupActions)
            await buildup(this);

        ReleaseAllTypes();
    }



    public virtual Task InitializeAsync()
    {
        try
        {
            Logger.LogInformation("Message hub {address} initialized", Address);
        }

        catch (Exception ex)
        {
            Logger.LogError(ex, "Could not initialize state of message hub {address}", Address);
        }
        return Task.CompletedTask;
    }


    public IMessageHub GetHub(object address, HostedHubOptions options)
    {
        return HostedHubsCollection.GetHub(address, options);
    }

    public IMessageHub GetHub(object address, Func<HostedHubOptions, HostedHubOptions> configuration = null)
    {
        return HostedHubsCollection.GetHub(address, configuration);
    }

    public THub2 GetHub<THub2>(object address, Func<HostedHubOptions, HostedHubOptions> configuration = null)
        where THub2 : class
    {
        return HostedHubsCollection.GetHub<THub2>(address, configuration);
    }

    public Task<TResponse> AwaitResponse<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken)
        => AwaitResponse(request, x => x, x => x.Message, cancellationToken);

    public Task<TResponse> AwaitResponse<TResponse>(IRequest<TResponse> request, Func<PostOptions, PostOptions> options, CancellationToken cancellationToken = default)
        => AwaitResponse(request, options, x => x.Message, cancellationToken);

    public Task<TResult> AwaitResponse<TResponse, TResult>(IRequest<TResponse> request, Func<PostOptions, PostOptions> options, Func<IMessageDelivery<TResponse>, TResult> selector, CancellationToken cancellationToken = default)
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

    void IMessageHub.SetParent(IMessageHub hub)
    {
        HostedHubsCollection.SetParent(hub);
    }



    IMessageDelivery IMessageHandler<ConnectToHubRequest>.HandleMessage(IMessageDelivery<ConnectToHubRequest> delivery)
    {
        return delivery.Processed();
    }

    IMessageDelivery IMessageHub.WriteToObservable(IMessageDelivery message)
    {
        Out.OnNext(message);
        return message;
    }


    object IMessageHub.Address => Address;

    IMessageDelivery IMessageHandler<DeleteHubRequest>.HandleMessage(IMessageDelivery<DeleteHubRequest> delivery)
    {
        return DeleteHub(delivery);
    }

    protected virtual IMessageDelivery DeleteHub(IMessageDelivery<DeleteHubRequest> delivery)
    {
        var deletedAddress = delivery.Message.Address;
        if (Address.Equals(deletedAddress))
            return DeleteMyself(delivery);
        return delivery.Processed();
    }

    private IMessageDelivery DeleteMyself(IMessageDelivery<DeleteHubRequest> delivery)
    {
        foreach (var messageHub in HostedHubsCollection.Hubs)
            messageHub.Post(delivery.Message);

        return delivery.Processed();
    }


    public void ConnectTo(IMessageHub hub)
    {
        hub.DeliverMessage(new MessageDelivery<ConnectToHubRequest>()
        {
            Message = new ConnectToHubRequest(Address, hub.Address, this, hub.DeliverMessage),
            Sender = Address,
            Target = hub.Address
        });
    }

    public void Disconnect(IMessageHub hub)
    {
        Post(new DisconnectHubRequest(Address), o => o.WithTarget(hub.Address));
    }

    public IMessageDelivery<TMessage> Post<TMessage>(TMessage message, Func<PostOptions, PostOptions> options = null)
    {
        return (IMessageDelivery<TMessage>)MessageService.Post(message, options);
    }

    public IMessageDelivery DeliverMessage(IMessageDelivery delivery)
    {
        var ret = delivery.ChangeState(MessageDeliveryState.Submitted);
        if (!IsDisposing)
            MessageService.IncomingMessage(ret);
        return ret;
    }

    protected Subject<IMessageDelivery> Out { get; } = new();
    IObservable<IMessageDelivery> IMessageHub.Out => Out;

    protected bool IsDisposing { get; private set; }

    private readonly TaskCompletionSource disposing = new();

    public bool HasHub(object address)
    {
        return HostedHubsCollection.HasHub(address);
    }

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

        await HostedHubsCollection.DisposeAsync();

        MessageService.Post(new DisconnectHubRequest(Address));

        await base.DisposeAsync();
        await MessageService.DisposeAsync();
        Out.OnCompleted();
        disposing.SetResult();
    }

    void IMessageHub.DeleteChild(object address)
    {
        DeleteHub(address);
    }

    public virtual void DeleteHub(object address)
    {
        HostedHubsCollection.Delete(address);
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

    protected virtual void HandleHeartbeat() { }

    IMessageDelivery IMessageHandler<HeartbeatEvent>.HandleMessage(IMessageDelivery<HeartbeatEvent> delivery)
    {
        HandleHeartbeat();
        return delivery.Processed();
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
                                    (t.GetGenericTypeDefinition() == typeof(IMessageHandler<>) || t.GetGenericTypeDefinition() == typeof(IMessageHandlerAsync<>)))
                        .Select(t => t.GetGenericArguments()[0])
                        .Where(t => t.Assembly != typeof(IMessageHub).Assembly)
                        .Where(t => !t.IsGenericType || t.GetGenericTypeDefinition() != typeof(CreateRequest<>))
            ;
    }


    IMessageDelivery IMessageHub.RegisterCallback<TResponse>(IMessageDelivery<IRequest<TResponse>> request, Func<IMessageDelivery<TResponse>, IMessageDelivery> callback, CancellationToken cancellationToken)
    {
        RegisterCallback(request, d => callback((IMessageDelivery<TResponse>)d), cancellationToken);
        return request.Forwarded();
    }



