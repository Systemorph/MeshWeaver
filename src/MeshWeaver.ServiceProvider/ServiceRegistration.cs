#nullable enable
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.ServiceProvider;

/// <summary>
/// One registration a <see cref="MeshServiceProvider"/> level answers a service with: built from a
/// <see cref="ServiceDescriptor"/> when the level is built, or handed out by an
/// <see cref="IServiceRegistrationSource"/> while it resolves. Immutable once built; the instances it
/// produces live in the scope its lifetime names, never in the registration.
/// </summary>
public sealed class ServiceRegistration
{
    private readonly Func<IServiceProvider, object?, object?>? factory;

    private ServiceRegistration(
        Type serviceType,
        object? serviceKey,
        ServiceLifetime lifetime,
        Type? implementationType,
        object? instance,
        Func<IServiceProvider, object?, object?>? factory,
        bool externallyOwned,
        object? standsInFor,
        object? identity,
        ServiceDescriptor? descriptor,
        long order)
    {
        ServiceType = serviceType;
        ServiceKey = serviceKey;
        Lifetime = lifetime;
        ImplementationType = implementationType;
        Instance = instance;
        this.factory = factory;
        IsExternallyOwned = externallyOwned || instance is not null;
        StandsInFor = standsInFor;
        Identity = identity ?? this;
        Descriptor = descriptor;
        Order = order;
    }

    /// <summary>The service type this registration answers (closed, for a closed generic).</summary>
    public Type ServiceType { get; }

    /// <summary>The service key, or null for an unkeyed registration.</summary>
    public object? ServiceKey { get; }

    /// <summary>The registration's lifetime.</summary>
    public ServiceLifetime Lifetime { get; }

    /// <summary>The type built by its constructor — null for a factory or a provided instance.</summary>
    public Type? ImplementationType { get; }

    /// <summary>The provided instance, or null.</summary>
    public object? Instance { get; }

    /// <summary>Whether a factory builds the instance.</summary>
    public bool IsFactory => factory is not null;

    /// <summary>Whether the scope that resolves it must NOT dispose what it returns — a provided
    /// instance, a <see cref="NotOwnedServiceDescriptor"/> forwarder, a source's stand-in.</summary>
    public bool IsExternallyOwned { get; }

    /// <summary>What a source's stand-in stands in for (another container's registration) — read by
    /// structural walks that follow a stand-in to what it really builds; null otherwise.</summary>
    public object? StandsInFor { get; }

    /// <summary>The descriptor this registration was built from, or null for a source's.</summary>
    public ServiceDescriptor? Descriptor { get; }

    /// <summary>Whether one instance is shared by the container it is registered in.</summary>
    public bool IsSingleton => Lifetime == ServiceLifetime.Singleton && Instance is null;

    /// <summary>Whether one instance is shared per scope.</summary>
    public bool IsScoped => Lifetime == ServiceLifetime.Scoped && Instance is null;

    /// <summary>What a shared instance is keyed by in the scope that holds it: the registration
    /// itself, or — for a closed generic or a source's registration made anew on every lookup — the
    /// open registration it was closed from.</summary>
    internal object Identity { get; }

    /// <summary>Registration order within its level — what an enumeration sorts by.</summary>
    internal long Order { get; }

    /// <summary>The level that holds it, set when the level is built; null for a source's registration.</summary>
    internal ServiceRegistry? Registry { get; set; }

    /// <summary>The shared instance of a singleton registered by descriptor (not a closed generic) —
    /// held on the registration because that registration belongs to exactly one container.</summary>
    internal InstanceSlot? SingletonSlot;

    /// <summary>The implementation type's constructors, longest first — computed on first activation.</summary>
    internal ConstructorPlan? Plan;

    /// <summary>
    /// What an open-generic registration keeps per closed type — its closures and, for a root singleton, the
    /// closed instances. Created on the first closure, so only an open generic that is resolved carries one,
    /// and every other registration stays as small as before.
    /// </summary>
    private OpenGenericState? openState;

    // 0 = not computed yet, 1 = no, 2 = yes — one byte, so a racing first read never sees a torn value.
    private volatile byte namesACollectibleAssembly;

    /// <summary>Whether <see cref="ServiceType"/> names a collectible assembly — computed once per registration,
    /// so a cached closure answers it without walking its generic arguments again.</summary>
    internal bool NamesACollectibleAssembly
    {
        get
        {
            var known = namesACollectibleAssembly;
            if (known == 0)
                namesACollectibleAssembly = known = MeshServiceProvider.NamesACollectibleAssembly(ServiceType) ? (byte)2 : (byte)1;
            return known == 2;
        }
    }

    /// <summary>This open generic's per-closed-type state, created on first use.</summary>
    internal OpenGenericState OpenState =>
        openState ?? Interlocked.CompareExchange(ref openState, new OpenGenericState(), null) ?? openState!;

    /// <summary>Releases the closed singleton instances, as the level that holds them ends; the closures
    /// themselves hold no instance and stay.</summary>
    internal void ReleaseClosedSingletons()
    {
        if (openState is { } state)
            state.Singletons = ImmutableDictionary<Type, InstanceSlot>.Empty;
    }

    /// <summary>
    /// The per-closed-type state of an open-generic registration.
    /// </summary>
    internal sealed class OpenGenericState
    {
        /// <summary>
        /// The closures over types that are NOT collectible, one per closed service type, built on the first
        /// resolve of that type and reused by every later one — so a resolve of <c>ILogger&lt;T&gt;</c> neither
        /// re-closes the implementation type nor allocates a registration. Instance-owned: it lives and dies
        /// with the registration, hence its level. Held strongly, because a type that is not collectible lives
        /// as long as the process anyway — and a weak table's dependent handles would be scanned by every
        /// garbage collection.
        /// </summary>
        internal ImmutableDictionary<Type, ClosedGeneric> Closures = ImmutableDictionary<Type, ClosedGeneric>.Empty;

        /// <summary>
        /// The closures over a COLLECTIBLE type (a NodeType's compiled type, a module's), keyed WEAKLY by the
        /// closed type, so a cached closure never keeps its load context alive. Created on the first such
        /// closure.
        /// </summary>
        internal ConditionalWeakTable<Type, ClosedGeneric>? CollectibleClosures;

        /// <summary>The singleton instances of a root open generic, closed per type argument list — never one
        /// closed over a collectible type (see <c>MeshServiceProvider.SingletonSlotFor</c>).</summary>
        internal ImmutableDictionary<Type, InstanceSlot> Singletons = ImmutableDictionary<Type, InstanceSlot>.Empty;
    }

    /// <summary>One cached closure — null when the type arguments break the implementation's constraints,
    /// which is cached too.</summary>
    internal sealed class ClosedGeneric(ServiceRegistration? registration)
    {
        internal ServiceRegistration? Registration { get; } = registration;
    }

    internal object? Create(IServiceProvider provider, object? requestedKey) => factory!(provider, requestedKey);

    /// <summary>
    /// A registration that answers <paramref name="serviceType"/> with whatever <paramref name="resolve"/>
    /// returns, given the scope the resolution is made in — how an <see cref="IServiceRegistrationSource"/>
    /// stands in for a service another container holds.
    /// </summary>
    /// <param name="serviceType">The service type.</param>
    /// <param name="serviceKey">The service key, or null.</param>
    /// <param name="resolve">Produces the instance for the resolving scope.</param>
    /// <param name="lifetime">How long the resolving container keeps what it returned.</param>
    /// <param name="externallyOwned">Whether the resolving scope must never dispose what it returned.</param>
    /// <param name="standsInFor">What it stands in for — see <see cref="StandsInFor"/>.</param>
    /// <param name="identity">The stable identity of a shared instance — required for a singleton or
    /// scoped registration a source makes anew on every lookup.</param>
    public static ServiceRegistration ForDelegate(
        Type serviceType,
        object? serviceKey,
        Func<MeshServiceProvider, object> resolve,
        ServiceLifetime lifetime = ServiceLifetime.Transient,
        bool externallyOwned = true,
        object? standsInFor = null,
        object? identity = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        ArgumentNullException.ThrowIfNull(resolve);
        return new ServiceRegistration(serviceType, serviceKey, lifetime, null, null,
            (provider, _) => resolve(MeshServiceProvider.Unwrap(provider)), externallyOwned, standsInFor, identity, null, 0);
    }

    /// <summary>
    /// A registration that builds <paramref name="implementationType"/> by its constructor — how a source
    /// closes an open generic for a type argument the container's own registrations never named.
    /// </summary>
    /// <param name="serviceType">The service type.</param>
    /// <param name="implementationType">The type to build.</param>
    /// <param name="lifetime">The lifetime.</param>
    /// <param name="identity">The stable identity of a shared instance (the open registration closed).</param>
    public static ServiceRegistration ForType(Type serviceType, Type implementationType, ServiceLifetime lifetime, object identity)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        ArgumentNullException.ThrowIfNull(implementationType);
        ArgumentNullException.ThrowIfNull(identity);
        return new ServiceRegistration(serviceType, null, lifetime, implementationType, null, null, false, null, identity, null, 0);
    }

    /// <summary>Builds the registration a level holds for <paramref name="descriptor"/>.</summary>
    internal static ServiceRegistration FromDescriptor(ServiceDescriptor descriptor, long order)
    {
        var key = descriptor.ServiceKey;
        var keyed = descriptor.IsKeyedService;
        var notOwned = descriptor is NotOwnedServiceDescriptor;
        if (keyed)
        {
            if (descriptor.KeyedImplementationInstance is { } keyedInstance)
                return new ServiceRegistration(descriptor.ServiceType, key, descriptor.Lifetime, null, keyedInstance, null, true, null, null, descriptor, order);
            if (descriptor.KeyedImplementationFactory is { } keyedFactory)
                return new ServiceRegistration(descriptor.ServiceType, key, descriptor.Lifetime, null, null,
                    (provider, requested) => keyedFactory(provider, requested), notOwned, null, null, descriptor, order);
            return new ServiceRegistration(descriptor.ServiceType, key, descriptor.Lifetime, descriptor.KeyedImplementationType, null, null, notOwned, null, null, descriptor, order);
        }
        if (descriptor.ImplementationInstance is { } instance)
            return new ServiceRegistration(descriptor.ServiceType, null, descriptor.Lifetime, null, instance, null, true, null, null, descriptor, order);
        if (descriptor.ImplementationFactory is { } unkeyedFactory)
            return new ServiceRegistration(descriptor.ServiceType, null, descriptor.Lifetime, null, null,
                (provider, _) => unkeyedFactory(provider), notOwned, null, null, descriptor, order);
        return new ServiceRegistration(descriptor.ServiceType, null, descriptor.Lifetime, descriptor.ImplementationType, null, null, notOwned, null, null, descriptor, order);
    }

    /// <summary>This open-generic registration closed over <paramref name="closedServiceType"/>'s
    /// arguments, or null when the arguments break the implementation's constraints. The closure is built once
    /// per closed type and reused; only a registration under <see cref="KeyedService.AnyKey"/>, which takes the
    /// key it was asked for, is closed anew for every other key.</summary>
    internal ServiceRegistration? Close(Type closedServiceType, object? requestedKey)
    {
        if (ServiceKey is not null && !Equals(ServiceKey, requestedKey))
            return CloseNew(closedServiceType, requestedKey);
        var state = OpenState;
        if (state.Closures.TryGetValue(closedServiceType, out var cached))
            return cached.Registration;
        if (state.CollectibleClosures is { } weak && weak.TryGetValue(closedServiceType, out cached))
            return cached.Registration;
        var closure = new ClosedGeneric(CloseNew(closedServiceType, ServiceKey));
        if (!MeshServiceProvider.NamesACollectibleAssembly(closedServiceType))
            return ImmutableInterlocked.GetOrAdd(ref state.Closures, closedServiceType, closure).Registration;
        weak = state.CollectibleClosures
               ?? Interlocked.CompareExchange(ref state.CollectibleClosures, new ConditionalWeakTable<Type, ClosedGeneric>(), null)
               ?? state.CollectibleClosures!;
        // No callback: a lambda capturing the closure would allocate on every call, the hits included.
        if (!weak.TryAdd(closedServiceType, closure) && weak.TryGetValue(closedServiceType, out var won))
            return won.Registration;
        return closure.Registration;
    }

    private ServiceRegistration? CloseNew(Type closedServiceType, object? requestedKey)
    {
        Type closedImplementation;
        try
        {
            closedImplementation = ImplementationType!.MakeGenericType(closedServiceType.GetGenericArguments());
        }
        catch (ArgumentException)
        {
            return null; // the type arguments violate the implementation's constraints: not a match
        }
        return new ServiceRegistration(closedServiceType, ServiceKey is null ? null : requestedKey, Lifetime, closedImplementation,
            null, null, IsExternallyOwned, null, this, Descriptor, Order)
        {
            Registry = Registry,
        };
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"{ServiceType.FullName}{(ServiceKey is null ? "" : $" [{ServiceKey}]")} ({Lifetime}, "
        + (Instance is not null ? "instance" : IsFactory ? "factory" : ImplementationType?.FullName) + ")";
}

/// <summary>One shared instance: built at most once, under the slot's own lock, never awaited.</summary>
internal sealed class InstanceSlot
{
    internal object? Value;
    internal volatile bool Created;

    /// <summary>The managed thread building the instance right now, or 0 — read under the slot's lock.</summary>
    internal int BuildingThread;
}
