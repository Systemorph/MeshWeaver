using System.Text.Json;
using MeshWeaver.Domain;

namespace MeshWeaver.Messaging;

public interface IMessageHub : IMessageHandlerRegistry, IDisposable
{
#if DEBUG
    
    internal static TimeSpan DefaultTimeout => TimeSpan.FromSeconds(3000);
#else
    internal static TimeSpan DefaultTimeout => TimeSpan.FromSeconds(30);

#endif
    MessageHubConfiguration Configuration { get; }
    long Version { get; }
    IMessageDelivery<TMessage> Post<TMessage>(TMessage message, Func<PostOptions, PostOptions>? options = null);
    IMessageDelivery DeliverMessage(IMessageDelivery delivery);
    Address Address { get; }
    IServiceProvider ServiceProvider { get; }

    Task<IMessageDelivery<TResponse>> AwaitResponse<TResponse>(IRequest<TResponse> request) =>
        AwaitResponse(request, new CancellationTokenSource(DefaultTimeout).Token);

    Task<IMessageDelivery<TResponse>> AwaitResponse<TResponse>(IMessageDelivery<IRequest<TResponse>> request, CancellationToken cancellationToken);

    Task<IMessageDelivery<TResponse>> AwaitResponse<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken);

    Task<IMessageDelivery<TResponse>> AwaitResponse<TResponse>(IRequest<TResponse> request, Func<PostOptions, PostOptions> options, CancellationToken cancellationToken = default);
    Task<TResult> AwaitResponse<TResponse, TResult>(IRequest<TResponse> request, Func<IMessageDelivery<TResponse>, TResult> selector)
        => AwaitResponse(request, x => x, selector);

    Task<TResult> AwaitResponse<TResponse, TResult>(IRequest<TResponse> request, Func<IMessageDelivery<TResponse>, TResult> selector, CancellationToken cancellationToken)
        => AwaitResponse(request, x => x, selector, cancellationToken);

    Task<TResult> AwaitResponse<TResponse, TResult>(IRequest<TResponse> request, Func<PostOptions, PostOptions> options, Func<IMessageDelivery<TResponse>, TResult> selector, CancellationToken cancellationToken = default);

    Task<IMessageDelivery> RegisterCallback<TResponse>(IMessageDelivery<IRequest<TResponse>> request,
        AsyncDelivery<TResponse> callback, CancellationToken cancellationToken = default)
        => RegisterCallback((IMessageDelivery)request, (r, c) => callback((IMessageDelivery<TResponse>)r, c),
            cancellationToken);
    Task<IMessageDelivery> RegisterCallback<TResponse>(IMessageDelivery<IRequest<TResponse>> request, SyncDelivery<TResponse> callback)
        => RegisterCallback((IMessageDelivery) request, (r, _) => Task.FromResult(callback((IMessageDelivery<TResponse>)r)), default);
    Task<IMessageDelivery> RegisterCallback(IMessageDelivery request, SyncDelivery callback)
        => RegisterCallback(request, (r, _) => Task.FromResult(callback(r)), default);
    Task<IMessageDelivery> RegisterCallback<TResponse>(IMessageDelivery<IRequest<TResponse>> delivery, SyncDelivery<TResponse> callback, CancellationToken cancellationToken)
        => RegisterCallback(delivery, (d,_) => Task.FromResult(callback(d)), cancellationToken);

    // ReSharper disable once UnusedMethodReturnValue.Local
    Task<IMessageDelivery> RegisterCallback(IMessageDelivery delivery, AsyncDelivery callback, CancellationToken cancellationToken);
    public void InvokeAsync(Func<CancellationToken, Task> action, Func<Exception, Task> exceptionCallback);

    public void InvokeAsync(Action action, Func<Exception, Task> exceptionCallback) => InvokeAsync(_ =>
    {
        action();
        return Task.CompletedTask;
    }, exceptionCallback);

    IMessageHub GetHostedHub<TAddress>(TAddress address, Func<MessageHubConfiguration, MessageHubConfiguration> config, HostedHubCreation create = default) 
        where TAddress : Address;

    IMessageHub GetHostedHub<TAddress>(TAddress address, HostedHubCreation create = default)
        where TAddress : Address
        => GetHostedHub(address, null!, create);
    IMessageHub RegisterForDisposal(IDisposable disposable) => RegisterForDisposal(_ => disposable.Dispose());
    IMessageHub RegisterForDisposal(Action<IMessageHub> disposeAction);
    IMessageHub RegisterForDisposal(IAsyncDisposable disposable) => RegisterForDisposal((_,_) => disposable.DisposeAsync().AsTask());
    IMessageHub RegisterForDisposal(Func<IMessageHub, CancellationToken, Task> disposeAction);
    JsonSerializerOptions JsonSerializerOptions { get; }
    bool IsDisposing { get; }
    IDisposable Defer(Predicate<IMessageDelivery> deferredFilter);

    internal Task StartAsync(CancellationToken cancellationToken);

    internal Task<IMessageDelivery> HandleMessageAsync(
        IMessageDelivery delivery,
        CancellationToken cancellationToken
    );
    Task Disposal { get; }
    ITypeRegistry TypeRegistry { get; }
}
