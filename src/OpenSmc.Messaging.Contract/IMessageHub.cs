using Microsoft.Extensions.Logging;


namespace OpenSmc.Messaging;


public interface IMessageHub : IMessageHandlerRegistry, IAsyncDisposable, IDisposable
{
    IMessageDelivery<TMessage> Post<TMessage>(TMessage message, Func<PostOptions, PostOptions> options = null);
    IMessageDelivery DeliverMessage(IMessageDelivery delivery);
    IObservable<IMessageDelivery> Out { get; }
    internal IMessageDelivery WriteToObservable(IMessageDelivery delivery);
    object Address { get; }
    IServiceProvider ServiceProvider { get; }
    void ConnectTo(IMessageHub hub);

    Task<TResponse> AwaitResponse<TResponse>(IRequest<TResponse> request) =>
        AwaitResponse(request, default);

    Task<TResponse> AwaitResponse<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken);

    Task<TResponse> AwaitResponse<TResponse>(IRequest<TResponse> request, Func<PostOptions, PostOptions> options, CancellationToken cancellationToken = default);

    Task<TResult> AwaitResponse<TResponse, TResult>(IRequest<TResponse> request, Func<IMessageDelivery<TResponse>, TResult> selector, CancellationToken cancellationToken = default)
        => AwaitResponse(request, x => x, selector, cancellationToken);

    Task<TResult> AwaitResponse<TResponse, TResult>(IRequest<TResponse> request, Func<PostOptions, PostOptions> options, Func<IMessageDelivery<TResponse>, TResult> selector, CancellationToken cancellationToken = default);

    IMessageDelivery RegisterCallback<TResponse>(IMessageDelivery<IRequest<TResponse>> request, Func<IMessageDelivery<TResponse>, IMessageDelivery> callback, CancellationToken cancellationToken = default);
    Task<IMessageDelivery> RegisterCallback(IMessageDelivery delivery, SyncDelivery callback, CancellationToken cancellationToken = default)
        => RegisterCallback(delivery, d => Task.FromResult(callback(d)), cancellationToken);

    // ReSharper disable once UnusedMethodReturnValue.Local
    Task<IMessageDelivery> RegisterCallback(IMessageDelivery delivery, AsyncDelivery callback, CancellationToken cancellationToken = default);
    Task<bool> FlushAsync();
    public void Schedule(Func<Task> action);


    void Log(Action<ILogger> log);
    IDisposable Defer();
    IDisposable Defer(Predicate<IMessageDelivery> deferredFilter);


    void Set<T>(T obj, string context = "");
    T Get<T>(string context = "");
    Task AddPluginAsync(IMessageHubPlugin plugin);
    IMessageHub GetHub<TAddress1>(TAddress1 address);
}



public interface IMessageHub<out TAddress> : IMessageHub
{
    new TAddress Address { get; }

}


public record MessageHubModuleConfiguration
{
    public IMessageHub Host { get; init; }

    public IMessageDelivery RegisterCallback<TResponse>(IMessageDelivery<IRequest<TResponse>> request, Func<IMessageDelivery<TResponse>, IMessageDelivery> callback, CancellationToken cancellationToken = default)
        => Host.RegisterCallback(request, callback, cancellationToken);
}
