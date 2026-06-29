using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Domain;

namespace MeshWeaver.Messaging;

/// <summary>
/// An address-partitioned, single-threaded actor. A message hub receives
/// <see cref="IMessageDelivery"/> messages and processes them one at a time through its
/// registered handler chain. It owns its hosted child hubs, exposes request/response
/// primitives, a reactive lifecycle, and a per-hub property bag. Implements
/// <see cref="IMessageHandlerRegistry"/> (handler registration) and
/// <see cref="IDisposable"/> (reactive teardown).
/// </summary>
public interface IMessageHub : IMessageHandlerRegistry, IDisposable
{
    /// <summary>The immutable configuration this hub was built from (address, handlers, buildup/dispose actions, timeouts).</summary>
    MessageHubConfiguration Configuration { get; }
    /// <summary>Monotonic counter incremented once per message the hub processes; used for ordering and disposal sequencing.</summary>
    long Version { get; }

    /// <summary>
    /// Sets the initial version for the hub. Only callable during initialization
    /// before any messages are processed.
    /// </summary>
    void SetInitialVersion(long version);

    /// <summary>
    /// Completes when the hub has finished initialization (its buildup actions ran and the init
    /// gate opened); faults if startup failed. Await only at a genuine async edge (tests).
    /// </summary>
    Task Started { get; }
    /// <summary>
    /// Posts a message into the mesh for routing/handling. The side effect is the dispatch itself;
    /// for a correlated response use the <c>hub.Observe(...)</c> extension.
    /// </summary>
    /// <typeparam name="TMessage">The message payload type.</typeparam>
    /// <param name="message">The message payload to send.</param>
    /// <param name="options">Optional configuration of the delivery (target, sender, response-correlation, message id).</param>
    /// <returns>The created delivery wrapping <paramref name="message"/>, or <c>null</c> if it was not posted.</returns>
    IMessageDelivery<TMessage>? Post<TMessage>(TMessage message, Func<PostOptions, PostOptions>? options = null);
    /// <summary>
    /// Inbound entry point: hands an already-routed delivery to this hub, marking it Submitted and
    /// routing it onto the hub's single-threaded action block. Called by the transport/routing layer.
    /// </summary>
    /// <param name="delivery">The delivery to process on this hub.</param>
    /// <returns>The delivery in its post-routing state.</returns>
    IMessageDelivery DeliverMessage(IMessageDelivery delivery);
    /// <summary>The address that identifies this hub within the mesh (its partition key for routing).</summary>
    Address Address { get; }
    /// <summary>The DI service provider scoped to this hub; resolve hub-scoped services from it.</summary>
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
    /// <summary>
    /// Schedules an action to run on the hub's action block (so it executes serially with message
    /// handling, on the hub's thread). The work is posted as an execution request; its
    /// genuinely-async leaf runs off the action block via the IO pool. Use this to marshal external
    /// callbacks back onto the hub.
    /// </summary>
    /// <param name="action">The work to run, receiving the hub's current cancellation token.</param>
    /// <param name="exceptionCallback">Invoked with any exception thrown by <paramref name="action"/>.</param>
    public void InvokeAsync(Func<CancellationToken, Task> action, Func<Exception, Task> exceptionCallback);

    /// <summary>
    /// Schedules a synchronous action onto the hub's action block (serial with message handling).
    /// Exceptions are not observed — use the overload taking an exception callback to handle them.
    /// </summary>
    /// <param name="action">The work to run on the hub thread.</param>
    public void InvokeAsync(Action action)
        => InvokeAsync(action, _ => Task.CompletedTask);
    /// <summary>
    /// Schedules a synchronous action onto the hub's action block (serial with message handling),
    /// routing any exception it throws to <paramref name="exceptionCallback"/>.
    /// </summary>
    /// <param name="action">The work to run on the hub thread.</param>
    /// <param name="exceptionCallback">Invoked with any exception thrown by <paramref name="action"/>.</param>
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
    /// <summary>
    /// Couples a synchronous cleanup to the hub's lifetime; the disposable is disposed during the
    /// hub's ShutDown phase, and a registrant added after disposal began is disposed immediately.
    /// </summary>
    /// <param name="disposable">The resource to dispose when the hub shuts down.</param>
    /// <returns>This hub, for chaining.</returns>
    IMessageHub RegisterForDisposal(IDisposable disposable);
    /// <summary>
    /// Couples a synchronous cleanup callback (receiving this hub) to the hub's lifetime; it runs
    /// during the ShutDown phase. For cleanups that do I/O, prefer the observable-returning overload.
    /// </summary>
    /// <param name="disposeAction">The cleanup to run at shutdown, receiving this hub.</param>
    /// <returns>This hub, for chaining.</returns>
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
    /// <summary>The JSON serialization options used for messages on this hub (inherits and extends the parent hub's options).</summary>
    JsonSerializerOptions JsonSerializerOptions { get; }
    /// <summary>The hub's current lifecycle phase (starting, started, quiescing, shutting down, dead).</summary>
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
    /// <summary>The hub's type registry, mapping message type names to CLR types for (de)serialization and routing.</summary>
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
