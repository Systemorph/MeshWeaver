using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
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

    // Disposal at the hub level is reactive but never a Task — nothing here is async
    // in the await sense. The first two overloads couple a SYNCHRONOUS cleanup to the
    // hub's lifetime (held in a CompositeDisposable, disposed during ShutDown).
    IMessageHub RegisterForDisposal(IDisposable disposable);
    IMessageHub RegisterForDisposal(Action<IMessageHub> disposeAction);

    /// <summary>
    /// Registers a REACTIVE dispose action — a cleanup that <b>returns
    /// <see cref="IObservable{T}"/></b> (Unit). Use this (never a void
    /// <see cref="Action{T}"/> that self-subscribes) whenever the cleanup itself does
    /// I/O — a final flush, a remote unsubscribe — so it can be <b>chained</b> with the
    /// other dispose actions and its genuinely-async leaves run on the mesh IO pool.
    /// The hub composes the registered actions and subscribes the chain when it disposes;
    /// it does not <c>await</c> — nothing on the hub surface is a <see cref="Task"/>. An
    /// <see cref="Action"/> that buried a <c>Subscribe</c> inside itself would be opaque
    /// and uncomposable, which is exactly what this overload exists to avoid.
    /// </summary>
    IMessageHub RegisterForDisposal(Func<IMessageHub, IObservable<Unit>> disposeAction);
    JsonSerializerOptions JsonSerializerOptions { get; }
    MessageHubRunLevel RunLevel { get; }

    /// <summary>
    /// Per-hub property bag — key is <c>(<paramref name="context"/>, typeof(T))</c>.
    /// Used to cache instance state on a hub without a separate static dictionary
    /// (e.g. <c>AgentChatClient</c>, <c>CancellationTokenSource</c>, completion
    /// callbacks). Disposed with the hub.
    /// </summary>
    void Set<T>(T obj, string context = "");

    /// <summary>
    /// Reads the value previously stored via <see cref="Set{T}"/>. Returns
    /// <c>default</c> when no entry exists.
    /// </summary>
    T Get<T>(string context = "");

    /// <summary>
    /// Opens a named initialization gate, allowing all deferred messages to be processed.
    /// </summary>
    /// <param name="name">The name of the gate to open</param>
    /// <returns>True if the gate was found and opened, false if already opened or not found</returns>
    bool OpenGate(string name);


    internal IObservable<IMessageDelivery> HandleMessageAsync(
        IMessageDelivery delivery,
        CancellationToken cancellationToken
    );
    /// <summary>
    /// True from the moment <see cref="IDisposable.Dispose"/> begins until the process is
    /// dead. The reactive, Task-free "is this hub shutting down?" probe (replaces the old
    /// <c>Disposal is not null</c> check). For *completion*, observe <see cref="DisposalCompleted"/>.
    /// </summary>
    bool IsDisposing { get; }

    /// <summary>
    /// Observable completion of disposal — fires <see cref="Unit"/> once, then completes, when
    /// the hub has finished disposing (or OnError on a disposal fault). Hubs dispose
    /// SYNCHRONOUSLY (only the mesh-level IO pools drain async), so disposal completion is
    /// OBSERVED, never awaited on a hub thread. There is no <see cref="Task"/> on the disposal
    /// surface. A subscriber that attaches after disposal has already finished still receives
    /// the completion immediately. At a genuine async edge (test teardown, grain deactivation)
    /// bridge once with <c>DisposalCompleted.FirstOrDefaultAsync()</c> / <c>.ToTask()</c>.
    /// </summary>
    IObservable<Unit> DisposalCompleted { get; }
    ITypeRegistry TypeRegistry { get; }

    /// <summary>
    /// Multi-line snapshot of the hub's disposal state for failure diagnostics.
    /// Includes hub address, run-level, disposal status, hosted-hub addresses
    /// (and whether each one's disposal is still pending via <see cref="IsDisposing"/>),
    /// and the dataflow buffer counts on the underlying message service. Used by test
    /// base classes when a dispose timeout fires so the failure says *why* it hung
    /// rather than just "operation was canceled".
    /// </summary>
    string GetDisposalDiagnostics();

    /// <summary>
    /// True if this hub or any hosted hub hit Quiescing-phase timeout — i.e. there
    /// were Observe-registered response callbacks still pending when the dispose
    /// drain budget elapsed. Tests should treat this as a dispose failure: a
    /// leaked subscription that never received its reply is a real bug, not a
    /// "test cleanup oddity".
    /// </summary>
    bool AnyHubQuiescingTimedOut();

    /// <summary>
    /// Concise summary of the hubs (and their pending callbacks) that hit Quiescing
    /// timeout, for inclusion in a dispose-failure error message. Empty if
    /// <see cref="AnyHubQuiescingTimedOut"/> is false.
    /// </summary>
    string GetQuiescingTimeoutSummary();

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
