using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Json.More;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenSmc.Messaging.Serialization;

namespace OpenSmc.Messaging;

public record ShutdownRequest(MessageHubRunLevel RunLevel, long Version);

public enum MessageHubRunLevel{Starting, Started, DisposeHostedHubs, HostedHubsDisposed, ShutDown, Dead }
public record ExecutionRequest(Func<CancellationToken, Task> Action);

public sealed class MessageHub<TAddress> : MessageHubBase<TAddress>, IMessageHub<TAddress>, IMessageHandler<ShutdownRequest>
{
    public override TAddress Address => (TAddress)MessageService.Address;


    void IMessageHub.Schedule(Func<CancellationToken, Task> action) => Post(new ExecutionRequest(action));
    private void Schedule(Func<CancellationToken, Task> action) => Post(new ExecutionRequest(action));
    public IServiceProvider ServiceProvider { get; }
    private readonly ConcurrentDictionary<string, List<AsyncDelivery>> callbacks = new();

    private readonly ILogger logger;
    public MessageHubConfiguration Configuration { get; }
    private readonly HostedHubsCollection hostedHubs;
    private readonly IDisposable deferral;
    public long Version { get; private set; }
    public MessageHubRunLevel RunLevel { get; private set; }

    public MessageHub(IServiceProvider serviceProvider, HostedHubsCollection hostedHubs,
        MessageHubConfiguration configuration, IMessageHub parentHub) : base(serviceProvider)
    {

        deferral = MessageService.Defer(d => d.Message is not ExecutionRequest);

        this.hostedHubs = hostedHubs;
        ServiceProvider = serviceProvider;
        logger = serviceProvider.GetRequiredService<ILogger<MessageHub<TAddress>>>();

        Configuration = configuration;
        var configurations =
            Configuration.Get<ImmutableList<Func<SerializationConfiguration, SerializationConfiguration>>>() ??
            ImmutableList<Func<SerializationConfiguration, SerializationConfiguration>>.Empty;
        SerializationOptions = configurations.Aggregate(
            CreateSerializationConfiguration(), (c, f) => f.Invoke(c)).Options;
        var typeRegistry = serviceProvider.GetRequiredService<ITypeRegistry>();
        DeserializationOptions = new JsonSerializerOptions(SerializationOptions);
        SerializationOptions.Converters.Add(new JsonNodeConverter());
        SerializationOptions.Converters.Add(new TypedObjectSerializeConverter(typeRegistry, null));
        DeserializationOptions.Converters.Add(new TypedObjectDeserializeConverter(typeRegistry));

        JsonSerializerOptions = new JsonSerializerOptions();
        JsonSerializerOptions.Converters.Add(new SerializationConverter(SerializationOptions, DeserializationOptions));

        MessageService.Initialize(DeliverMessageAsync, DeserializationOptions);

        disposeActions.AddRange(configuration.DisposeActions);

        var forwardConfig =
            (configuration.ForwardConfigurationBuilder ?? (x => x)).Invoke(new RouteConfiguration(this));

        AddPlugin(new RoutePlugin(this, forwardConfig, parentHub));


        Register(HandleCallbacks);
        Register(ExecuteRequest);

        foreach (var messageHandler in configuration.MessageHandlers)
            Register(messageHandler.MessageType, (d,c) => messageHandler.AsyncDelivery.Invoke(this, d,c));



        Schedule(StartAsync);
        logger.LogInformation("Message hub {address} initialized", Address);
    }

    private SerializationConfiguration CreateSerializationConfiguration()
    {
        return new SerializationConfiguration(this)
            .WithOptions(o =>
            {
                o.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                o.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault;
            });
    }


    public override async Task<IMessageDelivery> DeliverMessageAsync(IMessageDelivery delivery, CancellationToken cancellationToken)
    {
        ++Version;
        delivery = await base.DeliverMessageAsync(delivery, cancellationToken);
        return FinishDelivery(delivery);
    }

    private IMessageDelivery FinishDelivery(IMessageDelivery delivery)
    {
        // TODO V10: Add logging for failed messages, not found, etc. (31.01.2024, Roland Bürgi)
        return delivery;
    }


    private async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var (_,factory) in Configuration.PluginFactories)
            AddPlugin(factory.Invoke(this));

        var actions = Configuration.BuildupActions;
        foreach (var buildup in actions)
            await buildup(this, cancellationToken);

        deferral.Dispose();
        RunLevel = MessageHubRunLevel.Started;
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


    public IMessageDelivery RegisterCallback<TMessage, TResponse>(IMessageDelivery<TMessage> request, SyncDelivery<TResponse> callback, CancellationToken cancellationToken)
        where TMessage : IRequest<TResponse>
        => RegisterCallback<TMessage, TResponse>(request, (d,_) => Task.FromResult(callback(d)), cancellationToken);

    public IMessageDelivery RegisterCallback<TMessage, TResponse>(IMessageDelivery<TMessage> request, AsyncDelivery<TResponse> callback, CancellationToken cancellationToken)
        where TMessage : IRequest<TResponse>
    {
        RegisterCallback(request, (d,c) => callback.Invoke((IMessageDelivery<TResponse>)d, c), cancellationToken);
        return request.Forwarded();
    }

    public Task<IMessageDelivery> RegisterCallback(IMessageDelivery delivery, SyncDelivery callback, CancellationToken cancellationToken)
        => RegisterCallback(delivery, (d, _) => Task.FromResult(callback(d)), cancellationToken);


    // ReSharper disable once UnusedMethodReturnValue.Local
    public Task<IMessageDelivery> RegisterCallback(IMessageDelivery delivery, AsyncDelivery callback)
        => RegisterCallback(delivery, callback, default);
    public Task<IMessageDelivery> RegisterCallback(IMessageDelivery delivery, AsyncDelivery callback, CancellationToken cancellationToken)

    {
        var tcs = new TaskCompletionSource<IMessageDelivery>(cancellationToken);

        async Task<IMessageDelivery> ResolveCallback(IMessageDelivery d, CancellationToken ct)
        {
            var ret = await callback(d, ct);
            tcs.SetResult(ret);
            return ret;
        }

        callbacks.GetOrAdd(delivery.Id, _ => new()).Add(ResolveCallback);


        return tcs.Task;
    }

    private async Task<IMessageDelivery> ExecuteRequest(IMessageDelivery delivery, CancellationToken cancellationToken)
    {
        if (delivery.Message is not ExecutionRequest er)
            return delivery;
        await er.Action.Invoke(cancellationToken);
        return delivery.Processed();
    }
    private async Task<IMessageDelivery> HandleCallbacks(IMessageDelivery delivery, CancellationToken cancellationToken)
    {
        if (!delivery.Properties.TryGetValue(PostOptions.RequestId, out var requestId) ||
            !callbacks.TryRemove(requestId.ToString(), out var myCallbacks))
            return delivery;

        foreach (var callback in myCallbacks)
            delivery = await callback(delivery, cancellationToken);

        return delivery;
    }








    object IMessageHub.Address => Address;





    public void ConnectTo(IMessageHub hub)
    {
        hub.DeliverMessage(new MessageDelivery<ConnectToHubRequest>
        {
            Message = new ConnectToHubRequest(),
            Sender = Address,
            Target = hub.Address
        });
    }

    public void Disconnect(IMessageHub hub)
    {
        Post(new DisconnectHubRequest(), o => o.WithTarget(hub.Address));
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
        MessageService.IncomingMessage(ret);
        return ret;
    }




    public IMessageHub GetHostedHub<TAddress1>(TAddress1 address, Func<MessageHubConfiguration, MessageHubConfiguration> config)
    {
        var messageHub = hostedHubs.GetHub(address, config);
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

    // TODO V10: replace two ser/des to this single one (2023/09/27, Dmitry Kalabin)
    public JsonSerializerOptions JsonSerializerOptions { get; }
    public JsonSerializerOptions SerializationOptions { get; }
    public JsonSerializerOptions DeserializationOptions { get; }

    private bool IsDisposing => disposingTaskCompletionSource != null;

    private TaskCompletionSource disposingTaskCompletionSource;


    private readonly object locker = new();

    private static readonly TimeSpan ShutDownTimeout = TimeSpan.FromSeconds(10);

    public void Dispose()
    {
        lock (locker)
        {
            if(IsDisposing)
                return;
            disposingTaskCompletionSource = new(new CancellationTokenSource(ShutDownTimeout).Token);
        }

        //Post(new DisconnectHubRequest(), o => o.WithTarget(MessageTargets.Subscriptions));
        Post(new ShutdownRequest(MessageHubRunLevel.DisposeHostedHubs, Version));
    }

    IMessageDelivery IMessageHandler<ShutdownRequest>.HandleMessage(IMessageDelivery<ShutdownRequest> request)
    {
        if (request.Message.Version != Version - 1)
        {
            Post(request.Message with { Version = Version });
            return request.Ignored();
        }
        Task.Run(async () =>
        {
            switch (request.Message.RunLevel)
            {
                case MessageHubRunLevel.DisposeHostedHubs:
                    RunLevel = MessageHubRunLevel.DisposeHostedHubs;
                    await hostedHubs.DisposeAsync();
                    Post(new ShutdownRequest(MessageHubRunLevel.ShutDown, Version));
                    RunLevel = MessageHubRunLevel.HostedHubsDisposed;
                    return request.Processed();
                case MessageHubRunLevel.ShutDown:
                    RunLevel = MessageHubRunLevel.ShutDown;
                    await ShutdownAsync();
                    RunLevel = MessageHubRunLevel.Dead;
                    disposingTaskCompletionSource.SetResult();
                    return request.Processed();
            }

            return request.Ignored();
        });

        return request.Forwarded();
    }



    public override Task DisposeAsync()
    {
        if(!IsDisposing)
            Dispose();
        return disposingTaskCompletionSource.Task;
    }




    private async Task ShutdownAsync()
    {
        await hostedHubs.DisposeAsync();

        foreach (var configurationDisposeAction in disposeActions)
            await configurationDisposeAction.Invoke(this);


        await MessageService.DisposeAsync();
        await base.DisposeAsync();
    }

    private readonly List<Func<IMessageHub, Task>> disposeActions = new();






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
        logger.LogDebug("Adding plugin {plugin} in Address {address}", plugin.GetType(), Address);

        var def = MessageService.Defer(plugin.IsDeferred);
        Register(async (d,c) =>
        { 
            d = await plugin.DeliverMessageAsync(d,c);
            return d;
        });
        WithDisposeAction(_ => plugin.DisposeAsync());
        Schedule(async c =>
        {
            logger.LogDebug("Initializing plugin {plugin} in Address {address}", plugin.GetType(), Address);
            await plugin.StartAsync(c);
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
            plugin.Initialized.ContinueWith(_ =>
            {
                logger.LogDebug("Finished initializing plugin {plugin} in Address {address}", plugin.GetType(), Address);
                def.Dispose();
            }, c);
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
        });
    }

    #region combined serialization/deserialization converter

    private class SerializationConverter(JsonSerializerOptions serializationOptions, JsonSerializerOptions deserializationOptions) : JsonConverter<object>
    {
        public override bool CanConvert(Type typeToConvert) => true; // TODO V10: this might be a bit problematic in case none of the sub-converters has a support for this type (2023/09/27, Dmitry Kalabin)

        public override object Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using JsonDocument doc = JsonDocument.ParseValue(ref reader);
            var node = doc.RootElement.AsNode();
            return node.Deserialize(typeToConvert, deserializationOptions);
        }

        public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
        {
            var node = JsonSerializer.SerializeToNode(value, serializationOptions);
            node.WriteTo(writer);
        }
    }

    #endregion combined serialization/deserialization converter

    public ILogger Logger => logger;
}
