using Microsoft.Extensions.Logging;


namespace OpenSmc.Messaging;


public interface IMessageHub : IMessageHandlerRegistry, IAsyncDisposable, IDisposable
{
    MessageHubConfiguration Configuration { get; }
    long Version { get; }
    internal static TimeSpan DefaultTimeout => TimeSpan.FromSeconds(3);
    IMessageDelivery<TMessage> Post<TMessage>(TMessage message, Func<PostOptions, PostOptions> options = null);
    IMessageDelivery DeliverMessage(IMessageDelivery delivery);
    object Address { get; }
    IServiceProvider ServiceProvider { get; }
    void ConnectTo(IMessageHub hub);

    Task<IMessageDelivery<TResponse>> AwaitResponse<TResponse>(IRequest<TResponse> request) =>
        AwaitResponse(request, new CancellationTokenSource(DefaultTimeout).Token);

    Task<IMessageDelivery<TResponse>> AwaitResponse<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken);

    Task<IMessageDelivery<TResponse>> AwaitResponse<TResponse>(IRequest<TResponse> request, Func<PostOptions, PostOptions> options);
    Task<IMessageDelivery<TResponse>> AwaitResponse<TResponse>(IRequest<TResponse> request, Func<PostOptions, PostOptions> options, CancellationToken cancellationToken);
    Task<TResult> AwaitResponse<TResponse, TResult>(IRequest<TResponse> request, Func<IMessageDelivery<TResponse>, TResult> selector)
        => AwaitResponse(request, x => x, selector);

    Task<TResult> AwaitResponse<TResponse, TResult>(IRequest<TResponse> request, Func<IMessageDelivery<TResponse>, TResult> selector, CancellationToken cancellationToken)
        => AwaitResponse(request, x => x, selector, cancellationToken);

    Task<TResult> AwaitResponse<TResponse, TResult>(IRequest<TResponse> request, Func<PostOptions, PostOptions> options, Func<IMessageDelivery<TResponse>, TResult> selector, CancellationToken cancellationToken = default);

    IMessageDelivery RegisterCallback<TResponse>(IMessageDelivery<IRequest<TResponse>> request, Func<IMessageDelivery<TResponse>, IMessageDelivery> callback, CancellationToken cancellationToken = default);
    Task<IMessageDelivery> RegisterCallback(IMessageDelivery delivery, SyncDelivery callback, CancellationToken cancellationToken = default)
        => RegisterCallback(delivery, d => Task.FromResult(callback(d)), cancellationToken);

    // ReSharper disable once UnusedMethodReturnValue.Local
    Task<IMessageDelivery> RegisterCallback(IMessageDelivery delivery, AsyncDelivery callback, CancellationToken cancellationToken = default);
    Task<bool> FlushAsync();
    public void Schedule(Func<Task> action);



    void Set<T>(T obj, string context = "");
    T Get<T>(string context = "");
    void AddPlugin(IMessageHubPlugin plugin);
    IMessageHub GetHostedHub<TAddress1>(TAddress1 address, Func<MessageHubConfiguration, MessageHubConfiguration> config);

    IMessageHub WithDisposeAction(Action<IMessageHub> disposeAction);
    IMessageHub WithDisposeAction(Func<IMessageHub, Task> disposeAction);
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
