#nullable enable
using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.ServiceProvider;

/// <summary>
/// MeshWeaver's dependency-injection container: one class for the mesh's ROOT container, for each
/// hub's LEVEL (a scope with registrations of its own) and for every plain scope begun from either — a
/// Blazor circuit, a request, a module scope a caller owns. It implements the standard
/// <c>Microsoft.Extensions.DependencyInjection</c> contracts over <see cref="ServiceDescriptor"/>s, so
/// every library that registers into an <see cref="IServiceCollection"/> runs on it unchanged
/// (<c>Doc/Architecture/HubServiceLayers</c>).
///
/// <para><b>Why our own.</b> A hub must EXTEND the container it is built under: its own registrations
/// win in the hub, a parent's scoped or transient service resolved in the hub is built with the HUB's
/// dependencies (the per-hub <c>INodeValidator</c>s take the hub's own <c>IMessageHub</c>), and a
/// mesh's module registry must be asked for types no descriptor named when the container was built
/// (live module update). The standard provider supports none of the three. What this class keeps per
/// level is exactly the level's own descriptors, frozen, and the instances it built — nothing is copied
/// from a parent (MeshWeaver#7145; the per-hub cost it replaced is <c>Doc/Architecture/PortalHeapIsHubs</c>
/// → "Per-hub DI and type metadata").</para>
///
/// <para><b>Lookup.</b> A single resolve asks the level's own registrations, then each parent level's in
/// turn, then the root's, and last the root's <see cref="IServiceRegistrationSource"/>s; the first level
/// that answers wins, and on it the last registration. An enumeration is every level's registrations,
/// the root's first. <b>Lifetimes</b> are relative to where a registration lives and where it is asked:
/// a singleton is one instance in the container that registers it, built with that container's
/// dependencies; a scoped service is one instance per resolving scope and a transient one per resolve,
/// both built with the RESOLVING scope's dependencies — which is how a root-scoped service resolved in a
/// hub sees the hub's own overrides.</para>
///
/// <para><b>Threads.</b> Registrations are immutable once a level is built. A shared instance is built
/// at most once, under its own slot's lock — taken synchronously, never awaited, and never a lock
/// covering more than that one instance, so two first builds only wait for each other when one really
/// needs the other. A fault is not cached: the next resolve builds afresh. A cycle is detected per
/// resolve chain and refused by name.</para>
///
/// <para><b>Ownership.</b> A scope disposes only what it built, in reverse order, when it ends: never a
/// provided instance, a <see cref="NotOwnedServiceDescriptor"/> forwarder's result, or a source's
/// stand-in. A parent does not track its children — a scope nobody closes is garbage once nobody holds
/// it.</para>
/// </summary>
public sealed class MeshServiceProvider :
    IServiceProvider,
    IKeyedServiceProvider,
    ISupportRequiredService,
    IServiceProviderIsKeyedService,
    IServiceScopeFactory,
    IServiceScope,
    IAsyncDisposable
{
    /// <summary>The message of the <see cref="ObjectDisposedException"/> a resolve on an ended scope
    /// throws — the one string the platform's teardown classifiers recognise a disposed container by.</summary>
    public const string DisposedMessage =
        "Instances cannot be resolved and nested scopes cannot be begun from this service scope as it "
        + "(or one of its parent scopes) has already been disposed.";

    /// <summary>The object name every such <see cref="ObjectDisposedException"/> carries.</summary>
    public const string DisposedObjectName = "MeshWeaver.ServiceScope";

    private static readonly ImmutableArray<IServiceRegistrationSource> NoSources = [];
    private static readonly ImmutableArray<IActivationObserver> NoObservers = [];

    private readonly MeshServiceProvider? parent;
    private readonly ServiceRegistry? registry;
    private readonly MeshServiceProvider level;
    private readonly MeshServiceProvider root;
    private readonly object gate = new();
    private ImmutableDictionary<SlotKey, InstanceSlot> slots = ImmutableDictionary<SlotKey, InstanceSlot>.Empty;
    private ImmutableStack<object> disposables = ImmutableStack<object>.Empty;
    private int disposed;
    private EventHandler? ending;
    private ILogger? faultLogger;

    private MeshServiceProvider(MeshServiceProvider? parent, ServiceRegistry? registry, object? tag)
    {
        this.parent = parent;
        this.registry = registry;
        level = registry is not null ? this : parent!.level;
        root = parent?.root ?? this;
        Tag = tag;
    }

    /// <summary>Builds a ROOT container over <paramref name="descriptors"/>, asking
    /// <paramref name="sources"/> last on every lookup.</summary>
    internal static MeshServiceProvider Build(
        IEnumerable<ServiceDescriptor> descriptors,
        IEnumerable<IServiceRegistrationSource>? sources = null,
        IEnumerable<IActivationObserver>? observers = null,
        object? tag = null) =>
        new(null, new ServiceRegistry(descriptors,
            sources?.ToImmutableArray() ?? NoSources, observers?.ToImmutableArray() ?? NoObservers), tag);

    /// <summary>A tag naming the scope (a hub level's, a caller's module scope) — or null.</summary>
    public object? Tag { get; }

    /// <summary>The scope this one was begun from, or null for a container's root.</summary>
    public MeshServiceProvider? Parent => parent;

    /// <summary>Whether this is a container's root (the mesh's, or a module generation's own container).</summary>
    public bool IsContainerRoot => parent is null;

    /// <summary>The root of the container this scope belongs to.</summary>
    public MeshServiceProvider ContainerRoot => root;

    /// <summary>Whether the scope has ended (it, or a scope it was begun from, was disposed).</summary>
    public bool IsDisposed
    {
        get
        {
            for (var scope = this; scope is not null; scope = scope.parent)
                if (Volatile.Read(ref scope.disposed) != 0)
                    return true;
            return false;
        }
    }

    /// <summary>How many registrations this scope holds of its OWN — zero for a plain scope. Nothing a
    /// parent registered is counted, because nothing a parent registered is held here.</summary>
    public int OwnRegistrationCount => registry?.Count ?? 0;

    /// <summary>How many shared instances this scope holds in its own slots (scoped instances, and
    /// singletons of closed generics or of a source) — the per-scope instance cache.</summary>
    public int CachedInstanceCount => Volatile.Read(ref slots).Count;

    /// <summary>Raised once, when the scope begins to end — before anything it owns is disposed.</summary>
    public event EventHandler? Ending
    {
        add { lock (gate) ending += value; }
        remove { lock (gate) ending -= value; }
    }

    /// <summary>Where a fault in an asynchronous disposal that a synchronous <see cref="Dispose"/> started
    /// is written — bound by the root once it is built.</summary>
    internal void UseFaultLogger(ILogger? logger) => root.faultLogger = logger;

    // ------------------------------------------------------------------ scopes

    /// <summary>Begins a plain scope: no registrations of its own, scoped instances of its own.</summary>
    /// <param name="tag">An optional tag naming the scope.</param>
    public MeshServiceProvider BeginScope(object? tag = null)
    {
        ThrowIfDisposed();
        return new MeshServiceProvider(this, null, tag);
    }

    /// <summary>
    /// Begins a LEVEL: a scope with <paramref name="descriptors"/> of its own, frozen now — what every
    /// hub built under a parent is. Its registrations win in it and in every scope begun from it.
    /// </summary>
    /// <param name="descriptors">The level's own registrations, in order.</param>
    /// <param name="tag">An optional tag naming the level.</param>
    public MeshServiceProvider BeginScope(IEnumerable<ServiceDescriptor> descriptors, object? tag = null) =>
        BeginScope(descriptors, tag, null);

    internal MeshServiceProvider BeginScope(IEnumerable<ServiceDescriptor> descriptors, object? tag,
        IEnumerable<IActivationObserver>? observers)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        ThrowIfDisposed();
        return new MeshServiceProvider(this,
            new ServiceRegistry(descriptors, NoSources, observers?.ToImmutableArray() ?? NoObservers), tag);
    }

    /// <inheritdoc />
    public IServiceScope CreateScope() => BeginScope();

    IServiceProvider IServiceScope.ServiceProvider => this;

    // ------------------------------------------------------------------ IServiceProvider

    /// <inheritdoc />
    public object? GetService(Type serviceType) => Resolve(new ServiceIdentifier(serviceType, null), false, null);

    /// <inheritdoc />
    public object GetRequiredService(Type serviceType) => Resolve(new ServiceIdentifier(serviceType, null), true, null)!;

    /// <inheritdoc />
    public object? GetKeyedService(Type serviceType, object? serviceKey) =>
        Resolve(new ServiceIdentifier(serviceType, serviceKey), false, null);

    /// <inheritdoc />
    public object GetRequiredKeyedService(Type serviceType, object? serviceKey) =>
        Resolve(new ServiceIdentifier(serviceType, serviceKey), true, null)!;

    /// <inheritdoc />
    public bool IsService(Type serviceType) => CanResolve(new ServiceIdentifier(serviceType, null));

    /// <inheritdoc />
    public bool IsKeyedService(Type serviceType, object? serviceKey) =>
        CanResolve(new ServiceIdentifier(serviceType, serviceKey));

    // ------------------------------------------------------------------ introspection (module machinery)

    /// <summary>Every registration a resolve of <paramref name="serviceType"/> under
    /// <paramref name="serviceKey"/> can reach from this scope, in enumeration order — every level's own,
    /// the root's first, then what the sources add.</summary>
    /// <param name="serviceType">The (closed) service type.</param>
    /// <param name="serviceKey">The service key, or null.</param>
    public IReadOnlyList<ServiceRegistration> RegistrationsOf(Type serviceType, object? serviceKey = null)
    {
        var id = new ServiceIdentifier(serviceType, serviceKey);
        var all = ImmutableArray.CreateBuilder<ServiceRegistration>();
        foreach (var held in LevelsTopDown())
            foreach (var (registration, _) in RegistrationsOn(held, id))
                all.Add(registration);
        return all.ToImmutable();
    }

    /// <summary>This scope's OWN registrations of <paramref name="serviceType"/> (no parent's, no source's),
    /// in registration order — empty for a plain scope.</summary>
    /// <param name="serviceType">The (closed) service type.</param>
    /// <param name="serviceKey">The service key, or null.</param>
    public IReadOnlyList<ServiceRegistration> OwnRegistrationsOf(Type serviceType, object? serviceKey = null) =>
        registry?.AllOf(new ServiceIdentifier(serviceType, serviceKey)) ?? [];

    /// <summary>Every registration this scope holds of its own, in registration order.</summary>
    public IReadOnlyList<ServiceRegistration> OwnRegistrations => registry is null ? [] : registry.All;

    /// <summary>Resolves exactly <paramref name="registration"/> — one this scope can reach — as a resolve
    /// made in this scope would: a singleton in the level that registers it, anything else here.</summary>
    /// <param name="registration">The registration to resolve.</param>
    /// <param name="serviceType">The service type it is resolved as; its own when null.</param>
    /// <param name="serviceKey">The key it is resolved under; its own when null.</param>
    public object ResolveRegistration(ServiceRegistration registration, Type? serviceType = null, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ThrowIfDisposed();
        var owner = level;
        for (var held = level; held is not null; held = held.parent?.level)
            if (ReferenceEquals(held.registry, registration.Registry))
            {
                owner = held;
                break;
            }
        return Activate(registration, owner,
            new ServiceIdentifier(serviceType ?? registration.ServiceType, serviceKey ?? registration.ServiceKey), null);
    }

    /// <summary>Whether this scope's own level (no parent) can answer <paramref name="serviceType"/>
    /// under <paramref name="serviceKey"/>.</summary>
    /// <param name="serviceType">The service type.</param>
    /// <param name="serviceKey">The service key, or null.</param>
    public bool RegistersItself(Type serviceType, object? serviceKey = null)
    {
        RegistryRead?.Invoke();
        return level.registry!.Has(new ServiceIdentifier(serviceType, serviceKey));
    }

    /// <summary>Raised whenever a reader inspects this scope's registrations (<see cref="RegistersItself"/>) —
    /// the controlled switch a test arms to land a concurrent step exactly inside such a read.</summary>
    internal event Action? RegistryRead;

    // ------------------------------------------------------------------ ownership

    /// <summary>Hands <paramref name="instance"/> to this scope: disposed when the scope ends, in reverse
    /// order with everything the scope built. Throws <see cref="ObjectDisposedException"/> when the scope
    /// has ended — the caller still owns the instance then.</summary>
    /// <param name="instance">The instance to dispose with the scope.</param>
    public void DisposeWithScope(IDisposable instance) => Own(instance);

    /// <summary>As <see cref="DisposeWithScope(IDisposable)"/>, for an asynchronously disposable instance.</summary>
    /// <param name="instance">The instance to dispose with the scope.</param>
    public void DisposeWithScope(IAsyncDisposable instance) => Own(instance);

    private void Own(object instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        lock (gate)
        {
            if (disposed != 0)
                throw Disposed();
            disposables = disposables.Push(instance);
        }
    }

    /// <summary>Ends the scope: raises <see cref="Ending"/>, then disposes what it built, last first.
    /// An instance that is only asynchronously disposable is disposed asynchronously — never blocked on;
    /// its fault, if any, is logged.</summary>
    public void Dispose()
    {
        if (!BeginEnding(out var owned))
            return;
        var faults = ImmutableList<Exception>.Empty;
        foreach (var item in owned)
        {
            try
            {
                if (item is IDisposable disposable)
                    disposable.Dispose();
                else if (item is IAsyncDisposable asyncDisposable)
                    ObserveLate(asyncDisposable.DisposeAsync(), item);
            }
            catch (Exception exception)
            {
                faults = faults.Add(exception);
            }
        }
        ThrowFaults(faults);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (!BeginEnding(out var owned))
            return;
        var faults = ImmutableList<Exception>.Empty;
        foreach (var item in owned)
        {
            try
            {
                if (item is IAsyncDisposable asyncDisposable)
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                else if (item is IDisposable disposable)
                    disposable.Dispose();
            }
            catch (Exception exception)
            {
                faults = faults.Add(exception);
            }
        }
        ThrowFaults(faults);
    }

    private bool BeginEnding(out ImmutableStack<object> owned)
    {
        EventHandler? handlers;
        lock (gate)
        {
            owned = ImmutableStack<object>.Empty;
            if (disposed != 0)
                return false;
            disposed = 1;
            handlers = ending;
            ending = null;
        }
        handlers?.Invoke(this, EventArgs.Empty);
        lock (gate)
        {
            owned = disposables;
            disposables = ImmutableStack<object>.Empty;
            // Released with the scope: an ended scope holds nothing it built.
            slots = ImmutableDictionary<SlotKey, InstanceSlot>.Empty;
        }
        if (registry is not null)
            foreach (var registration in registry.All)
            {
                registration.SingletonSlot = null;
                registration.ReleaseClosedSingletons();
            }
        return true;
    }

    private static void ThrowFaults(ImmutableList<Exception> faults)
    {
        if (faults.Count == 1)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(faults[0]).Throw();
        if (faults.Count > 1)
            throw new AggregateException("Disposing the service scope faulted.", faults);
    }

    private void ObserveLate(ValueTask pending, object item)
    {
        if (pending.IsCompletedSuccessfully)
            return;
        var logger = root.faultLogger;
        pending.AsTask().ContinueWith(
            task =>
            {
                if (task.Exception is { } fault)
                {
                    if (logger is not null)
                        logger.LogWarning(fault, "Asynchronous disposal of {Type} faulted", item.GetType().FullName);
                    else
                        Trace.TraceWarning($"Asynchronous disposal of {item.GetType().FullName} faulted: {fault}");
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void ThrowIfDisposed()
    {
        if (IsDisposed)
            throw Disposed();
    }

    private static ObjectDisposedException Disposed() => new(DisposedObjectName, DisposedMessage);

    // ------------------------------------------------------------------ resolution

    private static bool IsBuiltIn(Type type) =>
        type == typeof(IServiceProvider)
        || type == typeof(IServiceScopeFactory)
        || type == typeof(IServiceProviderIsService)
        || type == typeof(IServiceProviderIsKeyedService)
        || type == typeof(IKeyedServiceProvider)
        || type == typeof(ISupportRequiredService)
        || type == typeof(MeshServiceProvider);

    internal object? Resolve(ServiceIdentifier id, bool required, ResolveContext? context)
    {
        ArgumentNullException.ThrowIfNull(id.Type);
        // 🚨 An ended scope refuses every service it can ANSWER — and answers a service nobody registered
        // as before: null (or "not registered"), never ObjectDisposedException. Teardown code probes
        // optional services on a dying hub (`GetService<IStreamOwnerResolver>()`), and the container this
        // replaced answered those with null after disposal; turning them into a throw moved the fault onto
        // whatever thread the teardown happened to run on (ScopeTeardownRenderTest).
        if (id.Key is null && IsBuiltIn(id.Type))
        {
            ThrowIfDisposed();
            return this;
        }
        if (ReferenceEquals(id.Key, KeyedService.AnyKey) && !ElementTypeOf(id.Type, out _, out _))
            throw new InvalidOperationException(
                $"KeyedService.AnyKey cannot be used to resolve a single service of type '{id.Type.FullName}' — only an enumeration.");
        if (Find(id) is { } found)
        {
            ThrowIfDisposed();
            return Activate(found.Registration, found.Level, id, context);
        }
        if (IsImplicit(id))
        {
            ThrowIfDisposed();
            if (TryImplicit(id, context, out var implicitValue))
                return implicitValue;
        }
        if (required)
            throw NotRegistered(id);
        return null;
    }

    private static InvalidOperationException NotRegistered(ServiceIdentifier id) =>
        new(id.Key is null
            ? $"The requested service '{id.Type.FullName}' has not been registered."
            : $"The requested service '{id.Key} ({id.Type.FullName})' has not been registered.");

    /// <summary>The registration a single resolve answers, and the level that holds it.</summary>
    private (ServiceRegistration Registration, MeshServiceProvider Level)? Find(ServiceIdentifier id)
    {
        for (var held = level; held is not null; held = held.parent?.level)
        {
            var own = held.registry!;
            if (own.Default(id) is { } registration)
                return (registration, held);
            if (own.Sources.Length > 0 && LastFromSources(own, id) is { } sourced)
                return (sourced, held);
        }
        return null;
    }

    private static ServiceRegistration? LastFromSources(ServiceRegistry own, ServiceIdentifier id)
    {
        ServiceRegistration? last = null;
        foreach (var source in own.Sources)
            foreach (var registration in source.RegistrationsFor(id.Type, id.Key, false))
                last = Sourced(registration, own);
        return last;
    }

    // A source's registration belongs to the level whose source made it: its singletons are held there,
    // and that level's observers watch its activations.
    private static ServiceRegistration Sourced(ServiceRegistration registration, ServiceRegistry own)
    {
        registration.Registry ??= own;
        return registration;
    }

    private IEnumerable<(ServiceRegistration Registration, MeshServiceProvider Level)> RegistrationsOn(
        MeshServiceProvider held, ServiceIdentifier id)
    {
        var own = held.registry!;
        var explicitOnes = own.AllOf(id);
        foreach (var registration in explicitOnes)
            yield return (registration, held);
        foreach (var source in own.Sources)
            foreach (var registration in source.RegistrationsFor(id.Type, id.Key, explicitOnes.Count > 0))
                yield return (Sourced(registration, own), held);
    }

    private ImmutableArray<MeshServiceProvider> LevelsTopDown()
    {
        var levels = ImmutableArray.CreateBuilder<MeshServiceProvider>();
        for (var held = level; held is not null; held = held.parent?.level)
            levels.Add(held);
        levels.Reverse();
        return levels.ToImmutable();
    }

    private bool CanResolve(ServiceIdentifier id)
    {
        if (id.Key is null && IsBuiltIn(id.Type))
            return true;
        if (Find(id) is not null)
            return true;
        if (ElementTypeOf(id.Type, out _, out _))
            return true;
        if (DeferredTypeOf(id.Type) is { } deferred)
            return CanResolve(id with { Type = deferred });
        return false;
    }

    private object Activate(ServiceRegistration registration, MeshServiceProvider owner, ServiceIdentifier id, ResolveContext? context)
    {
        if (registration.Instance is { } instance)
            return instance;
        switch (registration.Lifetime)
        {
            case ServiceLifetime.Singleton:
                var holder = SingletonHolder(registration, owner);
                return holder.Shared(holder.SingletonSlotFor(registration, owner, id), registration, id, context);
            case ServiceLifetime.Scoped:
                return Shared(SlotIn(registration, id), registration, id, context);
            default:
                return Build(registration, id, context);
        }
    }

    // 🚨 A root singleton CLOSED over a collectible type (a NodeType's compiled type, a module's) is held by
    // the level that asked for it — the hub — never by the root: held in the root it would pin its load
    // context for the life of the process. Each asking level then has its own instance.
    private MeshServiceProvider SingletonHolder(ServiceRegistration registration, MeshServiceProvider owner) =>
        owner.IsContainerRoot && !ReferenceEquals(level, owner)
                              && !ReferenceEquals(registration.Identity, registration)
                              && registration.NamesACollectibleAssembly
            ? level
            : owner;

    private InstanceSlot SingletonSlotFor(ServiceRegistration registration, MeshServiceProvider owner, ServiceIdentifier id)
    {
        if (ReferenceEquals(this, owner) && ReferenceEquals(registration.Registry, registry)
            && !ReferenceEquals(registration.ServiceKey, KeyedService.AnyKey))
        {
            if (ReferenceEquals(registration.Identity, registration))
                return registration.SingletonSlot ?? Interlocked.CompareExchange(ref registration.SingletonSlot, new InstanceSlot(), null)
                    ?? registration.SingletonSlot!;
            if (registration.Identity is ServiceRegistration open && ReferenceEquals(open.Registry, registry)
                && !(registration.ServiceType == id.Type ? registration.NamesACollectibleAssembly : NamesACollectibleAssembly(id.Type)))
                return ImmutableInterlocked.GetOrAdd(ref open.OpenState.Singletons, id.Type, _ => new InstanceSlot());
        }
        return SlotIn(registration, id);
    }

    private InstanceSlot SlotIn(ServiceRegistration registration, ServiceIdentifier id) =>
        ImmutableInterlocked.GetOrAdd(ref slots, new SlotKey(registration.Identity, id.Type, id.Key), _ => new InstanceSlot());

    private object Shared(InstanceSlot slot, ServiceRegistration registration, ServiceIdentifier id, ResolveContext? context)
    {
        if (slot.Created)
            return slot.Value!;
        // Refused BEFORE the lock: the lock is re-entrant, so a cycle on this thread would recurse forever.
        ResolveContext.ThrowIfCycle(context, this, registration.Identity, id);
        lock (slot)
        {
            if (slot.Created)
                return slot.Value!;
            // 🚨 The same thread back in the same slot while building it is a cycle — also one that runs
            // through a FACTORY, whose own resolves carry no resolve chain (it is handed the scope itself,
            // which it may keep). Refused by name instead of recursing until the stack overflows.
            var thread = Environment.CurrentManagedThreadId;
            if (slot.BuildingThread == thread)
                throw new InvalidOperationException(
                    $"A circular dependency was detected for the service of type '{id.Type.FullName}': "
                    + "building it asked for itself again.");
            slot.BuildingThread = thread;
            try
            {
                var value = Build(registration, id, context);
                slot.Value = value;
                slot.Created = true;
                return value;
            }
            finally
            {
                slot.BuildingThread = 0;
            }
        }
    }

    private object Build(ServiceRegistration registration, ServiceIdentifier id, ResolveContext? context)
    {
        ResolveContext.ThrowIfCycle(context, this, registration.Identity, id);
        var node = new ResolveContext(this, context, registration.Identity, id);
        var observers = registration.Registry?.Observers ?? NoObservers;
        var activation = new Activation(registration, id.Type, this);
        foreach (var observer in observers)
            observer.Activating(activation);
        object? value;
        try
        {
            value = registration.IsFactory
                ? registration.Create(this, id.Key)
                : Construct(registration, id, node);
        }
        finally
        {
            node.Done = true;
        }
        if (value is null)
            throw new InvalidOperationException(
                $"The factory registered for {Describe(id)} returned null; a registration must produce an instance.");
        if (!registration.IsExternallyOwned && value is IDisposable or IAsyncDisposable)
        {
            try
            {
                Own(value);
            }
            catch (ObjectDisposedException)
            {
                // The scope ended while the instance was being built: nobody else will ever dispose it.
                (value as IDisposable)?.Dispose();
                throw;
            }
        }
        foreach (var observer in observers)
            observer.Activated(activation, value);
        return value;
    }

    private object Construct(ServiceRegistration registration, ServiceIdentifier id, ResolveContext node)
    {
        var type = registration.ImplementationType
                   ?? throw new InvalidOperationException($"{registration} has neither an instance, a factory nor a type.");
        var plan = PlanFor(registration, type);
        ConstructorChoice? chosen = null;
        foreach (var candidate in plan.Constructors)
        {
            if (chosen is not null && candidate.Parameters.Length < chosen.Parameters.Length)
                break;
            if (!CanSatisfy(candidate, id))
                continue;
            if (chosen is not null)
                throw new InvalidOperationException(
                    $"Cannot build '{type.FullName}': two constructors of {candidate.Parameters.Length} parameters can both be satisfied — "
                    + "the choice between them is ambiguous.");
            chosen = candidate;
        }
        if (chosen is null)
        {
            var longest = plan.Constructors[0];
            var missing = longest.Parameters.FirstOrDefault(p => !p.Parameter.HasDefaultValue && !p.IsServiceKey
                && !CanResolve(new ServiceIdentifier(p.Parameter.ParameterType, p.KeyFor(id.Key))));
            throw new InvalidOperationException(
                $"Cannot build '{type.FullName}' for {Describe(id)}: no public constructor has every parameter resolvable"
                + (missing is null ? "." : $" — '{missing.Parameter.ParameterType.FullName} {missing.Parameter.Name}' is not registered."));
        }
        var arguments = new object?[chosen.Parameters.Length];
        for (var i = 0; i < arguments.Length; i++)
        {
            var parameter = chosen.Parameters[i];
            if (parameter.IsServiceKey)
            {
                var parameterType = parameter.Parameter.ParameterType;
                if (id.Key is not null && !parameterType.IsInstanceOfType(id.Key))
                    throw new InvalidOperationException(
                        $"Cannot build '{type.FullName}': its [ServiceKey] parameter '{parameter.Parameter.Name}' is a "
                        + $"{parameterType.FullName}, and the service was asked for under a {id.Key.GetType().FullName} key.");
                arguments[i] = id.Key;
                continue;
            }
            var dependency = new ServiceIdentifier(parameter.Parameter.ParameterType, parameter.KeyFor(id.Key));
            arguments[i] = Resolve(dependency, false, node)
                           ?? (parameter.Parameter.HasDefaultValue
                               ? parameter.Parameter.DefaultValue
                               : throw NotRegistered(dependency));
        }
        return chosen.Constructor.Invoke(BindingFlags.DoNotWrapExceptions, null, arguments, null);
    }

    private bool CanSatisfy(ConstructorChoice candidate, ServiceIdentifier id)
    {
        foreach (var parameter in candidate.Parameters)
        {
            if (parameter.IsServiceKey || parameter.Parameter.HasDefaultValue)
                continue;
            if (!CanResolve(new ServiceIdentifier(parameter.Parameter.ParameterType, parameter.KeyFor(id.Key))))
                return false;
        }
        return true;
    }

    // Held on the registration it belongs to — a hub's registration dies with the hub, so a collectible
    // implementation type is never pinned by a plan. A closed generic is planned per activation.
    private static ConstructorPlan PlanFor(ServiceRegistration registration, Type type)
    {
        if (!ReferenceEquals(registration.Identity, registration))
            return ConstructorPlan.For(type);
        return registration.Plan ?? Interlocked.CompareExchange(ref registration.Plan, ConstructorPlan.For(type), null) ?? registration.Plan!;
    }

    // ------------------------------------------------------------------ implicit relationships

    // The relationship types a constructor may take without anyone registering them, as the container
    // this one replaced answered them: every collection shape of T, and a deferred T.
    private static bool ElementTypeOf(Type type, out Type element, out bool asList)
    {
        asList = false;
        element = typeof(object);
        if (type.IsArray && type.GetArrayRank() == 1)
        {
            element = type.GetElementType()!;
            return true;
        }
        if (!type.IsConstructedGenericType)
            return false;
        var definition = type.GetGenericTypeDefinition();
        if (definition == typeof(IEnumerable<>) || definition == typeof(IReadOnlyCollection<>) || definition == typeof(IReadOnlyList<>))
        {
            element = type.GetGenericArguments()[0];
            return true;
        }
        if (definition == typeof(IList<>) || definition == typeof(ICollection<>))
        {
            element = type.GetGenericArguments()[0];
            asList = true;
            return true;
        }
        return false;
    }

    private bool IsImplicit(ServiceIdentifier id) =>
        ElementTypeOf(id.Type, out _, out _)
        || (DeferredTypeOf(id.Type) is { } deferred && CanResolve(id with { Type = deferred }));

    private static Type? DeferredTypeOf(Type type) =>
        type.IsConstructedGenericType
        && (type.GetGenericTypeDefinition() == typeof(Lazy<>) || type.GetGenericTypeDefinition() == typeof(Func<>))
            ? type.GetGenericArguments()[0]
            : null;

    private bool TryImplicit(ServiceIdentifier id, ResolveContext? context, out object? value)
    {
        if (ElementTypeOf(id.Type, out var element, out var asList))
        {
            var items = ImmutableArray.CreateBuilder<object>();
            foreach (var held in LevelsTopDown())
                foreach (var (registration, owner) in RegistrationsOn(held, id with { Type = element }))
                    items.Add(Activate(registration, owner, id with { Type = element }, context));
            // 🚨 A collection of DEFERRED services nobody registered as such — IEnumerable<Lazy<T>>, the
            // node-validator set every query provider takes — is one deferred element PER REGISTRATION OF T,
            // each building exactly its own registration when first asked. Read as "registrations of
            // Lazy<T>" it would be EMPTY, and every validator (the row-level security among them) would be
            // silently skipped.
            if (items.Count == 0 && DeferredTypeOf(element) is { } inner)
            {
                var innerId = id with { Type = inner };
                var wrap = (element.GetGenericTypeDefinition() == typeof(Lazy<>) ? DeferredLazyOver : DeferredFuncOver)
                    .MakeGenericMethod(inner);
                foreach (var held in LevelsTopDown())
                    foreach (var (registration, owner) in RegistrationsOn(held, innerId))
                    {
                        var (which, holder) = (registration, owner);
                        Func<object> build = () => Activate(which, holder, innerId, null);
                        items.Add(wrap.Invoke(null, BindingFlags.DoNotWrapExceptions, null, [build], null)!);
                    }
            }
            var array = Array.CreateInstance(element, items.Count);
            for (var i = 0; i < items.Count; i++)
                array.SetValue(items[i], i);
            value = asList ? Activator.CreateInstance(typeof(List<>).MakeGenericType(element), array) : array;
            return true;
        }
        if (DeferredTypeOf(id.Type) is { } deferred && CanResolve(id with { Type = deferred }))
        {
            var method = (id.Type.GetGenericTypeDefinition() == typeof(Lazy<>) ? DeferredLazy : DeferredFunc)
                .MakeGenericMethod(deferred);
            value = method.Invoke(null, BindingFlags.DoNotWrapExceptions, null, [this, id.Key], null);
            return true;
        }
        value = null;
        return false;
    }

    private static readonly MethodInfo DeferredLazy =
        typeof(MeshServiceProvider).GetMethod(nameof(LazyOf), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo DeferredFunc =
        typeof(MeshServiceProvider).GetMethod(nameof(FuncOf), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static Lazy<T> LazyOf<T>(MeshServiceProvider scope, object? key) where T : notnull =>
        new(() => (T)scope.GetRequiredKeyedService(typeof(T), key), LazyThreadSafetyMode.ExecutionAndPublication);

    private static Func<T> FuncOf<T>(MeshServiceProvider scope, object? key) where T : notnull =>
        () => (T)scope.GetRequiredKeyedService(typeof(T), key);

    private static readonly MethodInfo DeferredLazyOver =
        typeof(MeshServiceProvider).GetMethod(nameof(LazyOver), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo DeferredFuncOver =
        typeof(MeshServiceProvider).GetMethod(nameof(FuncOver), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static Lazy<T> LazyOver<T>(Func<object> build) where T : notnull =>
        new(() => (T)build(), LazyThreadSafetyMode.ExecutionAndPublication);

    private static Func<T> FuncOver<T>(Func<object> build) where T : notnull => () => (T)build();

    // ------------------------------------------------------------------ helpers

    /// <summary>The <see cref="MeshServiceProvider"/> behind <paramref name="provider"/> — the provider
    /// itself, or the scope a factory was handed — or null when it is not one.</summary>
    /// <param name="provider">A provider, or null.</param>
    public static MeshServiceProvider? From(IServiceProvider? provider) => provider switch
    {
        null => null,
        MeshServiceProvider scope => scope,
        _ => provider.GetService(typeof(MeshServiceProvider)) as MeshServiceProvider,
    };

    internal static MeshServiceProvider Unwrap(IServiceProvider provider) =>
        From(provider) ?? throw new InvalidOperationException(
            $"A {provider.GetType().FullName} is not a MeshWeaver service scope.");

    /// <summary>Whether <paramref name="type"/> — or a generic argument or element type of it — comes from a
    /// collectible assembly (a module's, a compiled NodeType's): a type no long-lived container may hold.</summary>
    /// <param name="type">The type.</param>
    public static bool NamesACollectibleAssembly(Type type) =>
        type.Assembly.IsCollectible
        || (type.IsGenericType && !type.IsGenericTypeDefinition && type.GetGenericArguments().Any(NamesACollectibleAssembly))
        || (type.HasElementType && NamesACollectibleAssembly(type.GetElementType()!));

    private static string Describe(ServiceIdentifier id) =>
        id.Key is null ? $"'{id.Type.FullName}'" : $"'{id.Key} ({id.Type.FullName})'";

    /// <inheritdoc />
    public override string ToString() =>
        $"MeshServiceProvider[{Tag ?? (IsContainerRoot ? "root" : "scope")}, own registrations {OwnRegistrationCount}]";

    private readonly record struct SlotKey(object Identity, Type Type, object? Key);
}

/// <summary>
/// One step of a resolve chain through constructors — so a cycle is refused by name, with its path,
/// instead of recursing. A factory is handed the scope itself (it may keep it), so a chain restarts
/// inside a factory; a cycle through a factory is caught where it re-enters a shared instance it is
/// building (<c>InstanceSlot.BuildingThread</c>). A finished step no longer counts.
/// </summary>
internal sealed class ResolveContext(MeshServiceProvider scope, ResolveContext? parent, object identity, ServiceIdentifier id)
{
    private readonly MeshServiceProvider scope = scope;
    private readonly ResolveContext? parent = parent;
    private readonly object identity = identity;
    private readonly ServiceIdentifier id = id;

    internal volatile bool Done;

    internal static void ThrowIfCycle(ResolveContext? context, MeshServiceProvider scope, object identity, ServiceIdentifier id)
    {
        for (var step = context; step is not null; step = step.parent)
        {
            if (step.Done || !ReferenceEquals(step.identity, identity) || step.id != id || !ReferenceEquals(step.scope, scope))
                continue;
            var chain = ImmutableList<string>.Empty;
            for (var link = context; link is not null; link = link.parent)
                chain = chain.Insert(0, link.id.Type.Name);
            throw new InvalidOperationException(
                $"A circular dependency was detected for the service of type '{id.Type.FullName}': "
                + string.Join(" -> ", chain.Add(id.Type.Name)) + ".");
        }
    }
}
