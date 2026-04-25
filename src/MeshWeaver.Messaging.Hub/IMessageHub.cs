using System.Text.Json;
using MeshWeaver.Domain;

namespace MeshWeaver.Messaging;

public interface IMessageHub : IMessageHandlerRegistry, IDisposable
{
    MessageHubConfiguration Configuration { get; }
    long Version { get; }

    /// <summary>
    /// Sets the initial version for the hub. Only callable during initialization
    /// before any messages are processed.
    /// </summary>
    void SetInitialVersion(long version);

    Task Started { get; }
    IMessageDelivery<TMessage>? Post<TMessage>(TMessage message, Func<PostOptions, PostOptions>? options = null);
    IMessageDelivery DeliverMessage(IMessageDelivery delivery);
    Address Address { get; }
    IServiceProvider ServiceProvider { get; }

    // === Request/response primitives ===
    //
    // Application code uses the `hub.Observe(...)` extension (in MessageHubExtensions)
    // which returns IObservable<IMessageDelivery<TResponse>>. Tests use
    // `MonolithMeshTestBase.AwaitResponseAsync(request, options?, hub?, ct?)`.
    //
    // No Task-returning request/response API on the interface anymore — the framework
    // primitives below are sync factories that return IObservable<IMessageDelivery>
    // backed by an AsyncSubject. There's no callback registration, no
    // TaskCompletionSource, and no Task.

    /// <summary>
    /// Sync factory: returns the response observable for an already-posted delivery.
    /// AsyncSubject backed, framework-timeout applied. Application code calls
    /// <c>hub.Observe(delivery)</c> (extension in MessageHubExtensions, same assembly).
    /// </summary>
    internal IObservable<IMessageDelivery> Observe(IMessageDelivery delivery);

    /// <summary>
    /// Sync factory: posts <paramref name="request"/> and returns the response observable.
    /// Pre-registers the subject before posting (via <see cref="PostOptions.WithMessageId"/>)
    /// so a synchronously-handled response can't slip through before the subscription is
    /// in place. Application code calls <c>hub.Observe(request, options?)</c>.
    /// </summary>
    internal IObservable<IMessageDelivery> Observe(object request, Func<PostOptions, PostOptions> options);
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
    IMessageHub? GetHostedHub(Address address, HostedHubCreation create)
        => GetHostedHub(address, x => x, create);

    /// <summary>
    /// Gets a hosted hub for the specified address.
    /// </summary>
    /// <param name="address"></param>
    /// <returns></returns>
    IMessageHub GetHostedHub(Address address)
        => GetHostedHub(address, x => x);


    /// <summary>
    /// Gets a hosted hub for the specified address and configuration.
    /// </summary>
    /// <param name="address"></param>
    /// <param name="config"></param>
    /// <returns></returns>
    IMessageHub GetHostedHub(Address address, Func<MessageHubConfiguration, MessageHubConfiguration> config)
        => GetHostedHub(address, config, HostedHubCreation.Always)!;

    /// <summary>
    /// Gets a hosted hub for the specified address.
    /// Returns a non-null <see cref="IMessageHub"/> if <paramref name="create"/> is <see cref="HostedHubCreation.Always"/>.
    /// Returns <c>null</c> if <paramref name="create"/> is <see cref="HostedHubCreation.Never"/> and the hub does not exist.
    /// </summary>
    IMessageHub? GetHostedHub(Address address, Func<MessageHubConfiguration, MessageHubConfiguration> config, HostedHubCreation create);

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


    internal void Start();

    /// <summary>
    /// Faults the hub's Started task so that DataSource.Initialized will also fault.
    /// Called when a stream errors during initialization (e.g., access denied).
    /// </summary>
    void FailStartup(Exception error);

    /// <summary>
    /// Cancels the currently executing handler's CancellationToken and creates a fresh one
    /// for subsequent messages. Use this to abort long-running handlers (e.g., streaming)
    /// without disposing the hub.
    /// </summary>
    void CancelCurrentExecution();
}
