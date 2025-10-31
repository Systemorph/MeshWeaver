using System.Text.Json;
using MeshWeaver.Domain;

namespace MeshWeaver.Messaging;

public interface IMessageHub : IMessageHandlerRegistry, IDisposable
{
    MessageHubConfiguration Configuration { get; }
    long Version { get; }
    Task Started { get; }
    IMessageDelivery<TMessage>? Post<TMessage>(TMessage message, Func<PostOptions, PostOptions>? options = null);
    IMessageDelivery DeliverMessage(IMessageDelivery delivery);
    Address Address { get; }
    IServiceProvider ServiceProvider { get; }

    Task<IMessageDelivery<TResponse>> AwaitResponse<TResponse>(IRequest<TResponse> request) =>
        AwaitResponse(request, new CancellationTokenSource(DefaultTimeout).Token);

    async Task<IMessageDelivery<TResponse>> AwaitResponse<TResponse>(IMessageDelivery<IRequest<TResponse>> request, CancellationToken cancellationToken)
        => (IMessageDelivery<TResponse>)(await AwaitResponse(request, o => o, o => o, cancellationToken))!;

    Task<IMessageDelivery<TResponse>> AwaitResponse<TResponse>(IRequest<TResponse> request,
        CancellationToken cancellationToken)
        => AwaitResponse(request, x => x, x => x, cancellationToken)!;

    async Task<IMessageDelivery<TResponse>> AwaitResponse<TResponse>(IRequest<TResponse> request,
        Func<PostOptions, PostOptions> options, CancellationToken cancellationToken = default)
        => (await AwaitResponse(request, options, o => o, cancellationToken))!;
    Task<TResult?> AwaitResponse<TResponse, TResult>(IRequest<TResponse> request,
        Func<IMessageDelivery<TResponse>, TResult> selector)
        => AwaitResponse(request, x => x, selector);

    Task<TResult?> AwaitResponse<TResponse, TResult>(IRequest<TResponse> request,
        Func<IMessageDelivery<TResponse>, TResult> selector, CancellationToken cancellationToken)
        => AwaitResponse(request, x => x, selector, cancellationToken);

    async Task<TResult?> AwaitResponse<TResponse, TResult>(IRequest<TResponse> request, Func<PostOptions, PostOptions> options,
        Func<IMessageDelivery<TResponse>, TResult> selector, CancellationToken cancellationToken = default)
        => (TResult?)await AwaitResponse((object)request, options, o => selector((IMessageDelivery<TResponse>)o), cancellationToken);


    Task<object?> AwaitResponse(object request, Func<PostOptions, PostOptions> options, Func<IMessageDelivery, object?> selector, CancellationToken cancellationToken = default);
    Task<IMessageDelivery> RegisterCallback<TResponse>(IMessageDelivery<IRequest<TResponse>> request,
        AsyncDelivery<TResponse> callback, CancellationToken cancellationToken = default)
        => RegisterCallback((IMessageDelivery)request, (r, c) => callback((IMessageDelivery<TResponse>)r, c),
            cancellationToken);
    Task<IMessageDelivery> RegisterCallback<TResponse>(IMessageDelivery<IRequest<TResponse>> request, SyncDelivery<TResponse> callback)
        => RegisterCallback((IMessageDelivery)request, (r, _) => Task.FromResult(callback((IMessageDelivery<TResponse>)r)), default);
    Task<IMessageDelivery> RegisterCallback(IMessageDelivery request, SyncDelivery callback)
        => RegisterCallback(request, (r, _) => Task.FromResult(callback(r)), default);
    Task<IMessageDelivery> RegisterCallback<TResponse>(IMessageDelivery<IRequest<TResponse>> delivery, SyncDelivery<TResponse> callback, CancellationToken cancellationToken)
        => RegisterCallback(delivery, (d, _) => Task.FromResult(callback(d)), cancellationToken);

    // ReSharper disable once UnusedMethodReturnValue.Local
    Task<IMessageDelivery> RegisterCallback(IMessageDelivery delivery, AsyncDelivery callback, CancellationToken cancellationToken);
    public void InvokeAsync(Func<CancellationToken, Task> action, Func<Exception, Task> exceptionCallback);

    public void InvokeAsync(Action action)
        => InvokeAsync(action, _ => Task.CompletedTask);
    public void InvokeAsync(Action action, Func<Exception, Task> exceptionCallback) => InvokeAsync(_ =>
    {
        action();
        return Task.CompletedTask;
    }, exceptionCallback);

    /// <summary>
    /// Gets a hosted hub for the specified address.
    /// Returns a non-null <see cref="IMessageHub"/> if <paramref name="create"/> is <see cref="HostedHubCreation.Always"/>.
    /// Returns <c>null</c> if <paramref name="create"/> is <see cref="HostedHubCreation.Never"/> and the hub does not exist.
    /// </summary>
    IMessageHub? GetHostedHub<TAddress>(TAddress address, HostedHubCreation create)
        where TAddress : Address
        => GetHostedHub(address, x => x, create);

    /// <summary>
    /// Gets a hosted hub for the specified address.
    /// </summary>
    /// <typeparam name="TAddress"></typeparam>
    /// <param name="address"></param>
    /// <returns></returns>
    IMessageHub GetHostedHub<TAddress>(TAddress address)
        where TAddress : Address
        => GetHostedHub(address, x => x);


    /// <summary>
    /// Gets a hosted hub for the specified address and configuration.
    /// </summary>
    /// <typeparam name="TAddress"></typeparam>
    /// <param name="address"></param>
    /// <param name="config"></param>
    /// <returns></returns>
    IMessageHub GetHostedHub<TAddress>(TAddress address, Func<MessageHubConfiguration, MessageHubConfiguration> config)
        where TAddress : Address
        => GetHostedHub(address, config, HostedHubCreation.Always)!;

    /// <summary>
    /// Gets a hosted hub for the specified address.
    /// Returns a non-null <see cref="IMessageHub"/> if <paramref name="create"/> is <see cref="HostedHubCreation.Always"/>.
    /// Returns <c>null</c> if <paramref name="create"/> is <see cref="HostedHubCreation.Never"/> and the hub does not exist.
    /// </summary>
    IMessageHub? GetHostedHub<TAddress>(TAddress address, Func<MessageHubConfiguration, MessageHubConfiguration> config, HostedHubCreation create)
        where TAddress : Address;

    IMessageHub RegisterForDisposal(IDisposable disposable) => RegisterForDisposal(_ => disposable.Dispose());
    IMessageHub RegisterForDisposal(Action<IMessageHub> disposeAction);
    IMessageHub RegisterForDisposal(IAsyncDisposable disposable) => RegisterForDisposal((_, _) => disposable.DisposeAsync().AsTask());
    IMessageHub RegisterForDisposal(Func<IMessageHub, CancellationToken, Task> disposeAction);
    JsonSerializerOptions JsonSerializerOptions { get; }
    MessageHubRunLevel RunLevel { get; }

    /// <summary>
    /// Opens a named initialization gate, allowing all deferred messages to be processed.
    /// </summary>
    /// <param name="name">The name of the gate to open</param>
    /// <returns>True if the gate was found and opened, false if already opened or not found</returns>
    bool OpenGate(string name);


    internal Task<IMessageDelivery> HandleMessageAsync(
        IMessageDelivery delivery,
        CancellationToken cancellationToken
    );
    Task? Disposal { get; }
    ITypeRegistry TypeRegistry { get; }

#if DEBUG

    internal static TimeSpan DefaultTimeout => TimeSpan.FromSeconds(3000);
#else
    internal static TimeSpan DefaultTimeout => TimeSpan.FromSeconds(30);

#endif

}
