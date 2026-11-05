#nullable enable
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeshWeaver.ServiceProvider;

/// <summary>
/// The instances a MODULE container built and owns, and who owns each — one registry per mesh, held by
/// the mesh's root container as an instance (never static). Read by the ownership diagnostics
/// (<c>Doc/Architecture/LiveModuleUpdate</c> → "Ownership diagnostics"): a scope that is about to dispose
/// one of these without owning it is the defect of MeshWeaver#6588.
///
/// <para>Holds its entries weakly (an ephemeron per instance): an entry lives exactly as long as the
/// instance it describes, so a retired module generation is never pinned by it.</para>
/// </summary>
public sealed class ModuleInstanceOwners
{
    private readonly ConditionalWeakTable<object, Owner> owners = new();
    private ImmutableHashSet<string> reported = ImmutableHashSet<string>.Empty;
    private readonly DeferredLogger logger = new(ModuleScopeOwnership.Category);
    private ImmutableDictionary<string, DeferredLogger> named = ImmutableDictionary<string, DeferredLogger>.Empty;
    private ILoggerFactory? bound;

    /// <summary>Where the mesh's module diagnostics are written — bound once the root container is built
    /// (<see cref="ServiceProviderExtensions.SetupModules"/>, or at host start on a plain Microsoft DI host), so
    /// no diagnostic ever has to RESOLVE a logger in the middle of a resolve (in a module container that resolve
    /// goes through the module's fallback, and from there back into the very registration being diagnosed; at
    /// boot it asks for a root logger factory that may be under construction, MeshWeaver.Plugins#4964).
    /// What is written before then is held and flushed here.</summary>
    /// <param name="factory">The root's logger factory, or null for none.</param>
    public void UseLoggerFactory(ILoggerFactory? factory)
    {
        Volatile.Write(ref bound, factory);
        logger.Bind(ModuleScopeOwnership.LoggerFrom(factory));
        foreach (var (category, deferred) in Volatile.Read(ref named))
            deferred.Bind(factory?.CreateLogger(category) ?? NullLogger.Instance);
    }

    internal ILogger Logger => logger;

    /// <summary>A logger of <paramref name="category"/> that needs no logger factory yet: it holds what is
    /// written until <see cref="UseLoggerFactory"/> binds it, and resolves nothing — so it is safe to take while
    /// the root is being built and to write through while it is being disposed.</summary>
    /// <param name="category">The log category.</param>
    /// <returns>The category's one logger.</returns>
    public ILogger LoggerFor(string category)
    {
        var deferred = ImmutableInterlocked.GetOrAdd(ref named, category, c => new DeferredLogger(c));
        // A factory bound while this category was being added must still reach it; Bind is first-wins.
        if (Volatile.Read(ref bound) is { } factory)
            deferred.Bind(factory.CreateLogger(category));
        return deferred;
    }

    /// <summary>Records that <paramref name="owner"/> owns <paramref name="instance"/> and is the only
    /// scope that may dispose it.</summary>
    /// <param name="instance">A disposable instance a module container built (or a proxy that forwards
    /// <c>Dispose</c> to one).</param>
    /// <param name="owner">The scope that disposes it, or null when no scope a caller can see does (the
    /// module's generation owns it).</param>
    /// <param name="ownerName">How the owner is named in a report.</param>
    public void Record(object instance, MeshServiceProvider? owner, string ownerName)
    {
        ArgumentNullException.ThrowIfNull(instance);
        // FIRST record wins: the container that BUILT the instance records it before any factory that
        // later returns the same object can (the build completes inside that factory's resolve). A factory
        // re-recording it would take over its owner and hide the very disposal this registry exists to name
        // (review of #7027).
        owners.TryAdd(instance, new Owner(owner is null ? null : new WeakReference<MeshServiceProvider>(owner), ownerName));
    }

    /// <summary>Whether <paramref name="instance"/> is a module-owned instance that
    /// <paramref name="scope"/> does not own; <paramref name="ownerName"/> names the owner when it is.</summary>
    internal bool IsOwnedByAnotherThan(object instance, MeshServiceProvider scope, out string ownerName)
    {
        ownerName = "";
        if (!owners.TryGetValue(instance, out var owner))
            return false;
        if (owner.Scope is not null && owner.Scope.TryGetTarget(out var held) && ReferenceEquals(held, scope))
            return false;
        ownerName = owner.Name;
        return true;
    }

    /// <summary>True the first time <paramref name="key"/> is reported — what keeps a defect that repeats
    /// on every circuit from flooding the log.</summary>
    internal bool FirstReport(string key) => ImmutableInterlocked.Update(ref reported, set => set.Add(key));

    /// <summary>Whether <paramref name="key"/> was reported already.</summary>
    internal bool Reported(string key) => Volatile.Read(ref reported).Contains(key);

    private sealed record Owner(WeakReference<MeshServiceProvider>? Scope, string Name);
}

/// <summary>
/// 🚨 <b>Ownership diagnostics</b> for module services (MeshWeaver#6584, MeshWeaver#6588). Both defects
/// wrote NO log line while they were live, so a quiet production log could never tell "fixed" from
/// "never logs". These are the lines the defects WOULD write: each fires only when the defect is present —
/// nothing on the happy path — at <see cref="LogLevel.Warning"/>, once per registration, under the
/// category <see cref="Category"/> and the stable prefix <see cref="Marker"/>.
/// </summary>
public static class ModuleScopeOwnership
{
    /// <summary>The logger category every ownership diagnostic is written under.</summary>
    public const string Category = "MeshWeaver.ServiceProvider.ModuleScopeOwnership";

    /// <summary>The stable prefix of every ownership diagnostic — what a log query matches.</summary>
    public const string Marker = "[ModuleScopeOwnership]";

    internal static ILogger LoggerFrom(ILoggerFactory? factory) =>
        factory?.CreateLogger(Category) ?? NullLogger.Instance;
}

/// <summary>
/// A logger that holds what is written until it is bound, then forwards (MeshWeaver.Plugins#4964). The module
/// registry and the ownership diagnostics run while the root container is being built — where the root logger
/// factory can be the service under construction — and again while it is being disposed, so neither may resolve
/// a logger when it writes; and a diagnostic reported once must not be spent on a logger that drops it.
/// Bounded: a host that never binds it (no generic host, no <see cref="ServiceProviderExtensions.SetupModules"/>)
/// holds the first <see cref="Capacity"/> entries and no more.
/// </summary>
internal sealed class DeferredLogger(string category) : ILogger
{
    internal const int Capacity = 256;

    private sealed record State(ILogger? Target, ImmutableQueue<Action<ILogger>> Held, int Count);

    private State state = new(null, ImmutableQueue<Action<ILogger>>.Empty, 0);

    public string Category { get; } = category;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Action<ILogger> entry = target => target.Log(logLevel, eventId, state, exception, formatter);
        while (true)
        {
            var current = Volatile.Read(ref this.state);
            if (current.Target is { } target)
            {
                entry(target);
                return;
            }
            if (current.Count >= Capacity)
                return;
            var held = current with { Held = current.Held.Enqueue(entry), Count = current.Count + 1 };
            if (ReferenceEquals(Interlocked.CompareExchange(ref this.state, held, current), current))
                return;
        }
    }

    public bool IsEnabled(LogLevel logLevel) =>
        Volatile.Read(ref state).Target?.IsEnabled(logLevel) ?? logLevel != LogLevel.None;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
        Volatile.Read(ref this.state).Target?.BeginScope(state);

    /// <summary>Binds the logger and flushes what it held, in order. The first binding wins.</summary>
    public void Bind(ILogger target)
    {
        while (true)
        {
            var current = Volatile.Read(ref state);
            if (current.Target is not null)
                return;
            var open = new State(target, ImmutableQueue<Action<ILogger>>.Empty, 0);
            if (!ReferenceEquals(Interlocked.CompareExchange(ref state, open, current), current))
                continue;
            foreach (var entry in current.Held)
                entry(target);
            return;
        }
    }
}

/// <summary>
/// MeshWeaver#6588: watches every registration of a level that hands out a FACTORY's result owned by the
/// resolving scope. When such a factory returns an instance a module owns (<see cref="ModuleInstanceOwners"/>),
/// the resolving scope will dispose it when it ends — under the owner — and the diagnostic is written as that
/// scope begins to end. Reflection-activated and provided-instance registrations build or hold their own
/// instance, so they cannot transfer ownership and are not watched; neither is a registration that is
/// externally owned.
/// </summary>
/// <param name="owners">The mesh's registry — captured here, never resolved during a resolve.</param>
internal sealed class OwnershipTransferObserver(ModuleInstanceOwners owners) : IActivationObserver
{
    public void Activating(in Activation activation)
    {
    }

    public void Activated(in Activation activation, object instance)
    {
        var registration = activation.Registration;
        if (registration.IsExternallyOwned || !registration.IsFactory || instance is not (IDisposable or IAsyncDisposable))
            return;
        var scope = activation.ActivationScope;
        if (!owners.IsOwnedByAnotherThan(instance, scope, out var ownerName))
            return;
        // Claimed when the scope ENDS, by registration alone (review of #7027): claimed at resolve, the first
        // scope to RESOLVE took the report and a scope ending before it disposed the instance silently; keyed
        // by the owner too, every caller tag was a new key — repeated lines and a key retained per circuit.
        // Once reported, later activations subscribe nothing, so a live defect costs no handler per circuit.
        var key = $"6588|{RuntimeHelpers.GetHashCode(registration.Identity)}|{registration.ServiceType.FullName}";
        if (owners.Reported(key))
            return;
        var service = registration.ServiceType.FullName;
        var instanceType = instance.GetType().FullName;
        var scopeTag = scope.Tag;
        scope.Ending += (_, _) =>
        {
            if (owners.FirstReport(key))
                owners.Logger.LogWarning(
                    "{Marker} Scope {Scope} is ending and will dispose {Service} ({Instance}), which {Owner} owns — "
                    + "a registration owned by the resolving scope returned a module-owned instance, so a scope that "
                    + "does not own it disposes it under its owner and every other user of it (MeshWeaver#6588). "
                    + "Reported once per registration.",
                    ModuleScopeOwnership.Marker, scopeTag, service, instanceType, ownerName);
        };
    }
}

/// <summary>
/// The ownership diagnostics of ONE module container (MeshWeaver#6584; see
/// <see cref="ModuleScopeOwnership"/>): records who owns each shared instance it builds, and reports a
/// SCOPED instance that no caller owns, or one served to a caller that does not own it. Held by the
/// container as an instance, never static; its entries are weak.
/// <para>🚨 Its logger is never resolved when the container is created (MeshWeaver.Plugins#4964): a module's
/// logging setup (<c>PostConfigure&lt;LoggerFilterOptions&gt;</c>) is served from this container, so the
/// container can be created INSIDE the root logger factory's construction, where resolving that factory asks
/// for the service being built. <see cref="ModuleInstanceOwners.Logger"/> holds what is reported before the root
/// is bound.</para>
/// </summary>
internal sealed class ModuleScopeDiagnostics(Func<ILogger> logger, ModuleInstanceOwners? owners) : IActivationObserver
{
    private readonly ConditionalWeakTable<object, ScopedOwner> scoped = new();
    private ImmutableHashSet<string> reported = ImmutableHashSet<string>.Empty;

    private static bool Watched(ServiceRegistration registration) =>
        (registration.IsSingleton || registration.IsScoped)
        && !registration.IsExternallyOwned
        && !IsInfrastructure(registration.ImplementationType ?? registration.ServiceType);

    // Reported as the activation is ATTEMPTED (review of #7027): the defect's throwing shape —
    // DialogService over an uninitialised NavigationManager — never returns an instance.
    public void Activating(in Activation activation)
    {
        if (Watched(activation.Registration))
            ReportIfBuiltForNoCaller(activation.Registration, activation.ServiceType, activation.ActivationScope);
    }

    public void Activated(in Activation activation, object instance)
    {
        if (Watched(activation.Registration))
            Built(activation.Registration, instance, activation.ActivationScope);
    }

    // The defect's own shape: a SCOPED registration built in the module container's ROOT scope is one
    // instance per generation, shared by every circuit, request and hub, and built against no caller.
    private void ReportIfBuiltForNoCaller(ServiceRegistration registration, Type service, MeshServiceProvider scope)
    {
        if (!registration.IsScoped
            || !scope.IsContainerRoot
            || ModuleServiceProvider.CallerOfModuleScope(scope) is not null
            || !FirstReport($"root|{RuntimeHelpers.GetHashCode(registration.Identity)}|{service.FullName}"))
            return;
        logger().LogWarning(
            "{Marker} Module scoped service {Service} ({Implementation}) is being built in the module container's "
            + "root scope, which no caller owns — every circuit, request and hub resolving it there shares one "
            + "instance, built against no caller's NavigationManager or IJSRuntime (MeshWeaver#6584). "
            + "Reported once per registration.",
            ModuleScopeOwnership.Marker, service.FullName, (registration.ImplementationType ?? service).FullName);
    }

    private static bool IsInfrastructure(Type type) => type.Assembly == typeof(ModuleScopeDiagnostics).Assembly;

    private void Built(ServiceRegistration registration, object instance, MeshServiceProvider scope)
    {
        var caller = ModuleServiceProvider.CallerOfModuleScope(scope);
        if (instance is IDisposable or IAsyncDisposable)
            owners?.Record(instance, scope, caller is null ? $"module scope '{scope.Tag}'" : $"the module scope owned by '{caller.Tag}'");
        if (!registration.IsScoped)
            return;
        // FIRST record wins (review of #7027): a scoped factory answering one shared object to callers A then
        // B must keep A as the owner, so B being served it is reported rather than silently re-owned.
        scoped.TryAdd(instance, new ScopedOwner(caller is null ? null : new WeakReference<MeshServiceProvider>(caller), caller?.Tag?.ToString()));
    }

    /// <summary>Reports <paramref name="instance"/>, a module's scoped instance, when it is handed to
    /// <paramref name="caller"/> although another caller (or none) owns the module scope it was built in.</summary>
    public void ServedTo(object instance, MeshServiceProvider caller)
    {
        if (!scoped.TryGetValue(instance, out var owner))
            return;
        if (owner.Caller is not null && owner.Caller.TryGetTarget(out var held) && ReferenceEquals(held, caller))
            return;
        if (!FirstReport($"served|{instance.GetType().FullName}"))
            return;
        logger().LogWarning(
            "{Marker} Module scoped service {Instance} built in the module scope owned by {Owner} was served to "
            + "{Caller}, which does not own that scope — one caller is sharing another's instance (MeshWeaver#6584). "
            + "Reported once per service type.",
            ModuleScopeOwnership.Marker, instance.GetType().FullName, owner.Tag ?? "no caller", caller.Tag);
    }

    private bool FirstReport(string key) => ImmutableInterlocked.Update(ref reported, set => set.Add(key));

    private sealed record ScopedOwner(WeakReference<MeshServiceProvider>? Caller, string? Tag);
}
