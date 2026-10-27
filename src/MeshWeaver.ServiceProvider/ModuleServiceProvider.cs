#nullable enable
using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.ServiceProvider;

/// <summary>
/// A service provider for ONE generation of a module, in a container of its OWN — never a scope of the
/// mesh's root container (policy <c>module-live-update-default</c>, <c>Doc/Architecture/LiveModuleUpdate</c>
/// → "Root services").
///
/// <para>🚨 <b>Why not a scope of the root.</b> A scope of the root would answer a module service's
/// dependencies through the root's registrations, and whatever the root built for a module type would be
/// held by the root for the life of the process: a module service that took
/// <c>IOptions&lt;ItsOwnOptions&gt;</c> would leave the closed options in the root, and the module's
/// collectible context could never be collected. A separate container cannot do that: the root is reached
/// only through <see cref="ModuleFallbackSource"/>, which refuses every type that names the module.</para>
/// </summary>
public static class ModuleServiceProvider
{
    /// <summary>
    /// Builds the module container: <paramref name="services"/> registered in it; a closed generic over
    /// a module type closed HERE from <paramref name="rootOpenGenerics"/> (so <c>IOptions&lt;T&gt;</c>,
    /// <c>ILogger&lt;T&gt;</c> of a module type live and die with the generation); every other service
    /// resolved from <paramref name="root"/>, which therefore never sees a module type.
    /// Dispose the returned provider to dispose the generation's singletons.
    /// </summary>
    /// <param name="services">The module's own registrations.</param>
    /// <param name="root">The mesh's root provider.</param>
    /// <param name="isModuleType">Whether a type names the module (its own type or a generic over one).</param>
    /// <param name="rootOpenGenerics">The root's open-generic registrations.</param>
    public static IServiceProvider Create(
        IServiceCollection services,
        IServiceProvider root,
        Func<Type, bool> isModuleType,
        IEnumerable<ServiceDescriptor> rootOpenGenerics)
        => CreateForCallers(services, root, isModuleType, rootOpenGenerics, NoRegistrationsElsewhere);

    private static IReadOnlyList<Func<MeshServiceProvider?, object>> NoRegistrationsElsewhere(Type type) => [];

    /// <summary>
    /// As <see cref="Create(IServiceCollection, IServiceProvider, Func{Type, bool}, IEnumerable{ServiceDescriptor})"/>,
    /// plus <paramref name="registrationsElsewhere"/>: for a type a MODULE declares (this one or another),
    /// the registrations made of it in OTHER module containers, each a resolver of one instance — served
    /// after the module's own, so <c>IEnumerable&lt;T&gt;</c> is the whole list and a single resolve still
    /// answers the module's own last registration. Empty for a platform type.
    /// </summary>
    /// <param name="services">The module's own registrations.</param>
    /// <param name="root">The mesh's root provider.</param>
    /// <param name="isModuleType">Whether a type names the module (its own type or a generic over one).</param>
    /// <param name="rootOpenGenerics">The root's open-generic registrations.</param>
    /// <param name="registrationsElsewhere">A module type's registrations in other module containers, in order.</param>
    public static IServiceProvider Create(
        IServiceCollection services,
        IServiceProvider root,
        Func<Type, bool> isModuleType,
        IEnumerable<ServiceDescriptor> rootOpenGenerics,
        Func<Type, IReadOnlyList<Func<object>>> registrationsElsewhere)
    {
        ArgumentNullException.ThrowIfNull(registrationsElsewhere);
        return CreateForCallers(services, root, isModuleType, rootOpenGenerics,
            type => registrationsElsewhere(type).Select(resolve => (Func<MeshServiceProvider?, object>)(_ => resolve())).ToArray());
    }

    /// <summary>
    /// As <see cref="Create(IServiceCollection, IServiceProvider, Func{Type, bool}, IEnumerable{ServiceDescriptor}, Func{Type, IReadOnlyList{Func{object}}})"/>,
    /// with resolvers that are told the CALLER a module type is resolved for — the root-side scope (a
    /// Blazor circuit, a request, a hub scope) the asking module scope serves, or null for none — so a
    /// scoped registration in ANOTHER module's container is built in that module's scope owned by the
    /// same caller (MeshWeaver#6584, <see cref="ResolveAt(IServiceProvider, Type, int, MeshServiceProvider?)"/>).
    /// </summary>
    /// <param name="services">The module's own registrations.</param>
    /// <param name="root">The mesh's root provider.</param>
    /// <param name="isModuleType">Whether a type names the module (its own type or a generic over one).</param>
    /// <param name="rootOpenGenerics">The root's open-generic registrations.</param>
    /// <param name="registrationsElsewhere">A module type's registrations in other module containers, in order,
    /// each resolving for a caller.</param>
    public static IServiceProvider CreateForCallers(
        IServiceCollection services,
        IServiceProvider root,
        Func<Type, bool> isModuleType,
        IEnumerable<ServiceDescriptor> rootOpenGenerics,
        Func<Type, IReadOnlyList<Func<MeshServiceProvider?, object>>> registrationsElsewhere)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(root);
        // MeshWeaver#6584/#6588: who owns each shared instance this container builds, and the warning a
        // scoped instance built for no caller — or served to a caller that does not own it — writes.
        var owners = root.GetService<ModuleInstanceOwners>();
        var diagnostics = new ModuleScopeDiagnostics(
            ModuleScopeOwnership.LoggerFrom(root.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()), owners);
        var callers = new ModuleCallerScopes();
        var descriptors = ImmutableList.CreateBuilder<ServiceDescriptor>();
        descriptors.Add(ServiceDescriptor.Singleton(diagnostics));
        descriptors.AddRange(services);
        descriptors.Add(ServiceDescriptor.Singleton(callers));
        if (MeshServiceProvider.From(root) is { } rootScope)
            // Where every root registration a module scope asks for is resolved (see RootResolutionScope):
            // in a module scope a CALLER owns (MeshWeaver#6584), that caller's own scope — its circuit's
            // NavigationManager and IJSRuntime; in any other module scope, one root scope of its own,
            // disposed with it.
            descriptors.Add(ServiceDescriptor.Scoped(sp => CallerOfModuleScope(MeshServiceProvider.From(sp)) is { } caller
                ? new RootResolutionScope(caller, owned: false)
                : new RootResolutionScope(rootScope.BeginScope(), owned: true)));
        IEnumerable<IActivationObserver> observers = owners is null
            ? [diagnostics]
            : [diagnostics, new OwnershipTransferObserver(owners)];
        var container = MeshServiceProvider.Build(
            descriptors.ToImmutable(),
            [new ModuleFallbackSource(root, isModuleType, rootOpenGenerics, registrationsElsewhere)],
            observers,
            tag: "module container");
        callers.Attach(container);
        // The callers' module scopes end FIRST, before the generation's singletons they may use.
        container.Ending += (_, _) => callers.Dispose();
        return container;
    }

    /// <summary>
    /// The ROOT-side scope a resolution on the mesh's root container is made for — a Blazor circuit's,
    /// a request's, a hub's — or null when it is made in the root container itself (a singleton, or a
    /// resolve off the root provider). What a root forwarder or a root stand-in for a module type passes
    /// as the caller (MeshWeaver#6584).
    /// </summary>
    /// <param name="scope">The scope the resolution is made in.</param>
    public static MeshServiceProvider? CallerOfRootScope(MeshServiceProvider? scope) =>
        scope is null || scope.IsContainerRoot ? null : scope;

    /// <summary>As <see cref="CallerOfRootScope(MeshServiceProvider?)"/>, for a root-side provider.</summary>
    /// <param name="provider">The provider the resolution is made from.</param>
    public static MeshServiceProvider? CallerOfRootScope(IServiceProvider? provider) =>
        CallerOfRootScope(MeshServiceProvider.From(provider));

    /// <summary>
    /// The caller a MODULE container's scope serves: the root-side scope that owns it (or owns a scope it
    /// was begun from) — or null for the module container's own root and every scope begun from it.
    /// </summary>
    /// <param name="scope">A scope of a module container.</param>
    public static MeshServiceProvider? CallerOfModuleScope(MeshServiceProvider? scope)
    {
        for (var current = scope; current is not null; current = current.Parent)
            if (current.Tag is CallerTag tag)
                return tag.Caller;
        return null;
    }

    /// <summary>
    /// The module scope <paramref name="caller"/> owns in <paramref name="moduleContainer"/> — created on
    /// first use, disposed with the caller (or with the generation, whichever ends first) — or
    /// <paramref name="moduleContainer"/> itself when there is no caller.
    ///
    /// <para>🚨 <b>Why</b> (MeshWeaver#6584). A module's SCOPED registration resolved from the module
    /// container's own root is ONE instance per generation, shared by every circuit, and its root
    /// dependencies come from a root scope no circuit initialised: a component library's <c>ThemeService</c> was
    /// one instance for every user, and <c>DialogService</c> subscribed to a <c>NavigationManager</c> that
    /// throws <c>'RemoteNavigationManager' has not been initialized</c>. In the caller's own module scope
    /// it is that caller's instance, built against that caller's <c>NavigationManager</c> and
    /// <c>IJSRuntime</c>.</para>
    /// </summary>
    /// <param name="moduleContainer">A provider <see cref="CreateForCallers"/> built.</param>
    /// <param name="caller">The root-side scope resolving, or null.</param>
    public static IServiceProvider InCallerScope(IServiceProvider moduleContainer, MeshServiceProvider? caller)
    {
        ArgumentNullException.ThrowIfNull(moduleContainer);
        if (caller is null || MeshServiceProvider.From(moduleContainer) is not { } scope)
            return moduleContainer;
        return InCallerScope(scope, caller);
    }

    private static MeshServiceProvider InCallerScope(MeshServiceProvider moduleScope, MeshServiceProvider caller) =>
        moduleScope.GetService<ModuleCallerScopes>() is { } callers ? callers.For(caller) : moduleScope;

    /// <summary>
    /// 🚨 <b>Built before published</b> (MeshWeaver#6542, MeshWeaver#6556): builds every
    /// PROCESS-LIFETIME instance of <paramref name="moduleContainer"/> the mesh's root can reach — each
    /// SINGLETON registered under a service <paramref name="rootReachable"/> accepts — unless building it
    /// would build a SCOPED service. The caller does this BEFORE the container can be reached from the
    /// mesh's root container, so a fault in a module constructor fails the module's load or swap instead of
    /// the first root-side caller, and the root only ever reads instances that exist.
    ///
    /// <para><b>What it does NOT build, and why (MeshWeaver#6556).</b> A SCOPED registration: its
    /// instance belongs to a scope — a Blazor circuit, a request — so the one a boot or a swap would
    /// build is an instance no scope asked for, made against scoped state nobody initialised. A component library's
    /// <c>DialogService</c> subscribes to a <c>NavigationManager</c> in its constructor; the boot-built
    /// one threw <c>'RemoteNavigationManager' has not been initialized</c> and withdrew the whole
    /// module. Nor a singleton whose constructor graph — read STRUCTURALLY off the registrations
    /// (<see cref="ScopedDependencyWalk"/>) — reaches a scoped registration: it is not a sound
    /// process-lifetime instance, and building it at boot runs the same uninitialised scoped state.
    /// A delegate-activated registration names no dependencies, so it is built.</para>
    ///
    /// <para>Throws what a constructor throws; the caller fails the module's load or swap with it, and
    /// disposes the container.</para>
    /// </summary>
    /// <param name="moduleContainer">A provider <see cref="CreateForCallers"/> built, not yet reachable from the root.</param>
    /// <param name="root">The mesh's root provider — whose registrations the module's fallback resolves.</param>
    /// <param name="rootReachable">The services (type and key) the root reaches the module by — its
    /// forwarders and its module-type stand-ins. A singleton under none of them is left to its first resolve.</param>
    public static void BuildRootReachableSingletons(
        IServiceProvider moduleContainer, IServiceProvider root, Func<Type, object?, bool> rootReachable)
        => BuildRootReachableSingletons(moduleContainer, root, rootReachable, NoContainersElsewhere);

    /// <summary>
    /// As <see cref="BuildRootReachableSingletons(IServiceProvider, IServiceProvider, Func{Type, object?, bool})"/>,
    /// with the registrations a type a module DECLARES has in OTHER module containers — so a singleton
    /// whose constructor takes another module's SCOPED type is recognised as reaching a scoped service
    /// and is left to its first resolve. Without it the walk sees only this container and the root, and
    /// the root is never asked about a module's type: the singleton was judged sound, built at boot, and
    /// built the other module's scoped service with no caller, against scoped state nobody initialised —
    /// which threw and withdrew the module.
    /// </summary>
    /// <param name="moduleContainer">A provider <see cref="CreateForCallers"/> built, not yet reachable from the root.</param>
    /// <param name="root">The mesh's root provider — whose registrations the module's fallback resolves.</param>
    /// <param name="rootReachable">The services the root reaches the module by.</param>
    /// <param name="registrationsElsewhere">For a type a module declares, EVERY module container that
    /// registers it and the key it is held under there — what <c>ModuleFallbackSource</c> resolves it
    /// from. The walk skips the container it is currently in, so this must not pre-exclude the module the
    /// walk started in: a nested walk (A → B → A) reaches A's registrations from inside B.</param>
    public static void BuildRootReachableSingletons(
        IServiceProvider moduleContainer, IServiceProvider root, Func<Type, object?, bool> rootReachable,
        Func<Type, IEnumerable<(IServiceProvider Container, object? Key)>> registrationsElsewhere)
    {
        ArgumentNullException.ThrowIfNull(moduleContainer);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(rootReachable);
        ArgumentNullException.ThrowIfNull(registrationsElsewhere);
        if (MeshServiceProvider.From(moduleContainer) is not { } scope)
            return;
        var walk = new ScopedDependencyWalk(scope, MeshServiceProvider.From(root), registrationsElsewhere);
        var registrations = scope.OwnRegistrations
            .Where(r => r.IsSingleton
                        && rootReachable(r.ServiceType, r.ServiceKey)
                        && !walk.ReachesAScopedService(r, ScopedDependencyWalk.Side.Module))
            .ToArray();
        foreach (var registration in registrations)
            scope.ResolveRegistration(registration);
    }

    private static IEnumerable<(IServiceProvider Container, object? Key)> NoContainersElsewhere(Type type) => [];

    /// <summary>
    /// One stand-in registration per resolver in <paramref name="resolvers"/>, each answering its own
    /// instance, in order — so the list reads in the resolvers' order and a single resolve answers the
    /// last unless the container registers the type itself.
    /// </summary>
    /// <param name="type">The service type the stand-ins expose.</param>
    /// <param name="resolvers">One resolver per registration, in order.</param>
    public static IEnumerable<ServiceRegistration> StandIns(Type type, IReadOnlyList<Func<object>> resolvers) =>
        resolvers.Select(resolve => ServiceRegistration.ForDelegate(type, null, _ => resolve())).ToArray();

    /// <summary>
    /// As <see cref="StandIns(Type, IReadOnlyList{Func{object}})"/>, each resolver told the caller
    /// <paramref name="callerOf"/> reads off the scope the stand-in is resolved in (MeshWeaver#6584).
    /// </summary>
    /// <param name="type">The service type the stand-ins expose.</param>
    /// <param name="key">The key they are registered under, or null.</param>
    /// <param name="resolvers">One resolver per registration, in order.</param>
    /// <param name="callerOf">The caller a resolving scope serves — <see cref="CallerOfRootScope(MeshServiceProvider?)"/>
    /// on the root, <see cref="CallerOfModuleScope"/> in a module container.</param>
    public static IEnumerable<ServiceRegistration> StandIns(
        Type type, object? key, IReadOnlyList<Func<MeshServiceProvider?, object>> resolvers,
        Func<MeshServiceProvider?, MeshServiceProvider?> callerOf)
    {
        ArgumentNullException.ThrowIfNull(callerOf);
        return resolvers.Select(resolve => ServiceRegistration.ForDelegate(type, key, scope => resolve(callerOf(scope)))).ToArray();
    }

    /// <summary>
    /// As <see cref="ResolveAt(IServiceProvider, Type, int)"/>, for <paramref name="caller"/>: a registration
    /// that is not a singleton is resolved in the module scope the caller owns
    /// (<see cref="InCallerScope(IServiceProvider, MeshServiceProvider?)"/>) — that caller's instance, built
    /// against that caller's scope — and a singleton in the module container, the generation's one
    /// instance, as before.
    /// </summary>
    /// <param name="provider">The provider holding the registrations — a module container.</param>
    /// <param name="serviceType">The (closed) service type.</param>
    /// <param name="position">Zero-based position among that type's registrations.</param>
    /// <param name="caller">The root-side scope resolving, or null.</param>
    public static object ResolveAt(IServiceProvider provider, Type serviceType, int position, MeshServiceProvider? caller)
    {
        if (caller is null || MeshServiceProvider.From(provider) is not { } scope)
            return ResolveAt(provider, serviceType, position);
        var registration = RegistrationAt(scope, serviceType, position);
        if (registration.IsSingleton || registration.Instance is not null)
            return scope.ResolveRegistration(registration, serviceType);
        var instance = InCallerScope(scope, caller).ResolveRegistration(registration, serviceType);
        ReportIfServedAcrossCallers(provider, instance, caller);
        return instance;
    }

    /// <summary>
    /// MeshWeaver#6584's diagnostic at a SERVING point: writes a <see cref="ModuleScopeOwnership.Marker"/>
    /// warning when <paramref name="instance"/> — a scoped instance <paramref name="moduleContainer"/> built —
    /// is handed to <paramref name="caller"/> although the module scope it was built in is owned by another
    /// caller, or by none. Nothing happens for any other instance, or when there is no caller.
    /// </summary>
    /// <param name="moduleContainer">A provider <see cref="CreateForCallers"/> built.</param>
    /// <param name="instance">The instance being handed out.</param>
    /// <param name="caller">The root-side scope it is handed to, or null.</param>
    public static void ReportIfServedAcrossCallers(IServiceProvider moduleContainer, object instance, MeshServiceProvider? caller)
    {
        ArgumentNullException.ThrowIfNull(moduleContainer);
        ArgumentNullException.ThrowIfNull(instance);
        if (caller is not null
            && MeshServiceProvider.From(moduleContainer)?.GetService<ModuleScopeDiagnostics>() is { } diagnostics)
            diagnostics.ServedTo(instance, caller);
    }

    /// <summary>
    /// Resolves the <paramref name="position"/>-th registration of <paramref name="serviceType"/> in
    /// <paramref name="provider"/>, counted in registration order — exactly the element
    /// <c>GetServices(serviceType)</c> yields at that position, and nothing else.
    ///
    /// <para>🚨 This is how a forwarder stands in for ONE of several registrations of the same service
    /// type. A forwarder that called <c>GetRequiredService(serviceType)</c> instead answers the LAST
    /// registration for every position, so <c>GetServices</c> through N such forwarders returns N copies
    /// of the last one, or — when the forwarders were de-duplicated by type — that one alone. On the core
    /// #6127 images every per-node hub resolved only the LAST <c>IHarness</c> the AI module registers, so the
    /// home-page chat lost the harness it was bound to.</para>
    /// </summary>
    /// <param name="provider">The provider holding the registrations — a module container.</param>
    /// <param name="serviceType">The (closed) service type.</param>
    /// <param name="position">Zero-based position among that type's registrations.</param>
    public static object ResolveAt(IServiceProvider provider, Type serviceType, int position)
    {
        if (MeshServiceProvider.From(provider) is not { } scope)
            return provider.GetServices(serviceType).ElementAt(position)
                   ?? throw new InvalidOperationException($"Registration {position} of {serviceType} resolved to null.");
        return scope.ResolveRegistration(RegistrationAt(scope, serviceType, position), serviceType);
    }

    // 🚨 The module container's OWN registrations only — never what its ModuleFallbackSource adds. A root
    // stand-in that resolves position i of a type the module does NOT register (a cached stand-in for a
    // module-declared type after its module swapped to a new generation, whose container holds the new
    // generation's type) would otherwise land on the module's own fallback stand-in for the ROOT's
    // registration — which is that same root stand-in. Each hop is a fresh resolve, so no cycle check sees
    // it: on the Autofac container this counted positions over every registration a lookup reached, and a
    // portal process died of a StackOverflowException in exactly that cycle (MeshWeaver#5388). Counted over
    // the module's own, such a position is refused by name. Pinned by AStaleRootStandInIsRefusedNotRecursedTest.
    private static ServiceRegistration RegistrationAt(MeshServiceProvider scope, Type serviceType, int position) =>
        scope.ContainerRoot.OwnRegistrationsOf(serviceType).ElementAtOrDefault(position)
        ?? throw new InvalidOperationException(
            $"{serviceType} has no registration at position {position} in this module container.");
}

/// <summary>
/// Where one module scope resolves every root registration it asks for, so the root's lifetimes keep
/// their meaning across the container boundary: a singleton is the root's one instance, while a scoped
/// service is never built — and shared — in the root itself (MeshWeaver#6432). For a module scope a
/// CALLER owns (MeshWeaver#6584) that is the caller's own scope — a circuit's <c>NavigationManager</c> is
/// that circuit's — and is not disposed here; for any other module scope it is a root scope of its own,
/// disposed with it.
/// </summary>
internal sealed class RootResolutionScope(MeshServiceProvider scope, bool owned) : IDisposable
{
    /// <summary>The scope root registrations are resolved in.</summary>
    public MeshServiceProvider Scope { get; } = scope;

    /// <inheritdoc />
    public void Dispose()
    {
        if (owned)
            Scope.Dispose();
    }
}

/// <summary>The tag of a module scope a caller owns — names the caller.</summary>
internal sealed class CallerTag(MeshServiceProvider caller)
{
    /// <summary>The root-side scope that owns the module scope.</summary>
    public MeshServiceProvider Caller { get; } = caller;

    /// <inheritdoc />
    public override string ToString() => $"module scope owned by {Caller.Tag}";
}

/// <summary>
/// The module scopes CALLERS own in one module container (MeshWeaver#6584,
/// <see cref="ModuleServiceProvider.InCallerScope(IServiceProvider, MeshServiceProvider?)"/>): one per caller,
/// begun on first use, disposed when the caller ends — it is handed to the caller's own scope — or when
/// the generation ends, whichever is first. Held per container, never static.
///
/// <para>The container this replaced built every shared instance of a scope under ONE lock for that
/// scope, so a caller-owned module scope had to borrow its caller's lock or the two deadlocked when
/// their first builds crossed. <see cref="MeshServiceProvider"/> builds each shared instance under that
/// instance's own lock, so two first builds wait for each other only when one really needs the other
/// — and nothing has to be borrowed.</para>
/// </summary>
internal sealed class ModuleCallerScopes : IDisposable
{
    private readonly object gate = new();
    private MeshServiceProvider? container;
    private ImmutableDictionary<MeshServiceProvider, CallerOwnedModuleScope> scopes =
        ImmutableDictionary.Create<MeshServiceProvider, CallerOwnedModuleScope>(ReferenceEqualityComparer.Instance);
    private bool disposed;

    /// <summary>Binds the container the scopes are begun from — once it is built.</summary>
    public void Attach(MeshServiceProvider moduleContainer) => container = moduleContainer;

    /// <summary>The module scope <paramref name="caller"/> owns, begun on first use.</summary>
    public MeshServiceProvider For(MeshServiceProvider caller)
    {
        CallerOwnedModuleScope created;
        MeshServiceProvider begun;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (scopes.TryGetValue(caller, out var existing) && existing.Scope is { } live)
                return live;
            begun = (container ?? throw new InvalidOperationException("The module container is not built yet."))
                .BeginScope(new CallerTag(caller));
            created = new CallerOwnedModuleScope(this, caller, begun);
            scopes = scopes.SetItem(caller, created);
        }
        try
        {
            // Owned by the caller: it ends when the caller ends, disposed the way the caller is (a Blazor
            // circuit's scope is disposed asynchronously).
            caller.DisposeWithScope((IAsyncDisposable)created);
        }
        catch (ObjectDisposedException)
        {
            created.Dispose();
            throw;
        }
        return begun;
    }

    /// <summary>Drops <paramref name="owned"/> once it has ended.</summary>
    public void Forget(MeshServiceProvider caller, CallerOwnedModuleScope owned)
    {
        lock (gate)
        {
            if (scopes.TryGetValue(caller, out var held) && ReferenceEquals(held, owned))
                scopes = scopes.Remove(caller);
        }
    }

    /// <summary>Ends every caller's module scope — the generation is ending.</summary>
    public void Dispose()
    {
        ImmutableDictionary<MeshServiceProvider, CallerOwnedModuleScope> ending;
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            ending = scopes;
            scopes = scopes.Clear();
            container = null;
        }
        foreach (var owned in ending.Values)
            owned.Dispose();
    }
}

/// <summary>
/// One caller's module scope, as the caller's scope holds it. Disposing it ends the scope and lets
/// go of it, so a caller that outlives the generation (a long-lived hub scope across a live swap) holds
/// nothing of that generation — the module's collectible context stays collectible.
/// </summary>
internal sealed class CallerOwnedModuleScope : IDisposable, IAsyncDisposable
{
    private ModuleCallerScopes? owner;
    private MeshServiceProvider? caller;
    private MeshServiceProvider? scope;

    public CallerOwnedModuleScope(ModuleCallerScopes owner, MeshServiceProvider caller, MeshServiceProvider scope)
    {
        this.owner = owner;
        this.caller = caller;
        this.scope = scope;
    }

    /// <summary>The module scope, or null once it ended.</summary>
    public MeshServiceProvider? Scope => Volatile.Read(ref scope);

    /// <inheritdoc />
    public void Dispose() => Release()?.Dispose();

    /// <inheritdoc />
    public ValueTask DisposeAsync() => Release()?.DisposeAsync() ?? ValueTask.CompletedTask;

    private MeshServiceProvider? Release()
    {
        var ending = Interlocked.Exchange(ref scope, null);
        if (ending is null)
            return null;
        var from = Interlocked.Exchange(ref owner, null);
        var of = Interlocked.Exchange(ref caller, null);
        if (from is not null && of is not null)
            from.Forget(of, this);
        return ending;
    }
}

/// <summary>
/// The module container's view of everything it does not register itself — see
/// <see cref="ModuleServiceProvider"/>.
/// </summary>
internal sealed class ModuleFallbackSource(
    IServiceProvider root,
    Func<Type, bool> isModuleType,
    IEnumerable<ServiceDescriptor> rootOpenGenerics,
    Func<Type, IReadOnlyList<Func<MeshServiceProvider?, object>>> registrationsElsewhere) : IServiceRegistrationSource
{
    private readonly ImmutableDictionary<Type, ServiceDescriptor> openGenerics = rootOpenGenerics
        .Where(d => d.ServiceType.IsGenericTypeDefinition && d.ImplementationType is not null && !d.IsKeyedService)
        .GroupBy(d => d.ServiceType)
        .ToImmutableDictionary(g => g.Key, g => g.Last());

    public IEnumerable<ServiceRegistration> RegistrationsFor(Type type, object? serviceKey, bool explicitRegistrationsExist)
    {
        if (serviceKey is not null)
            yield break;
        // 🚨 A type a MODULE declares — this one or another — is answered from the MODULES, never the
        // root: the registrations made of it in other module containers (another module's own, or a
        // contribution a dependent module made to this module's list). Measured on the control instance
        // from core #6127 on: every AI provider module registers MeshWeaver.AI's catalog sources and
        // chat-client factories, and those never reached the AI module's container — the model catalog
        // listed no provider, "No IChatClientFactory is registered", every PR review stopped — while the
        // provider's own code could not resolve the AI module's ChatClientCredentialResolver at all.
        // After the module's own registrations, so IEnumerable<T> is the whole list and a single resolve
        // still answers the module's own last registration.
        var elsewhere = registrationsElsewhere(type);
        if (elsewhere.Count > 0)
        {
            // Resolved for the caller this module scope serves (MeshWeaver#6584): another module's
            // scoped registration is that caller's instance, in that module's scope the caller owns.
            foreach (var standIn in ModuleServiceProvider.StandIns(type, null, elsewhere, ModuleServiceProvider.CallerOfModuleScope))
                yield return standIn;
            yield break;
        }
        if (isModuleType(type))
        {
            // A closed generic over a module type nobody registered closed: closed HERE, from the
            // root's open generic — so it lives and dies with the generation.
            if (!explicitRegistrationsExist && type.IsConstructedGenericType
                && openGenerics.TryGetValue(type.GetGenericTypeDefinition(), out var open)
                && Close(open, type) is { } closed)
                yield return ServiceRegistration.ForType(type, closed, open.Lifetime, open);
            yield break; // never ask the root about a module type — that is what pins it
        }
        if (explicitRegistrationsExist)
            yield break; // the module registers it itself
        if (root.GetService<IServiceProviderIsService>() is { } isService && !isService.IsService(type))
            yield break;
        // 🚨 One stand-in PER ROOT REGISTRATION, never one for the type. A single
        // `root.GetRequiredService(type)` stand-in made every IEnumerable<T> a module service took
        // contain exactly ONE element — the root's last — while the root itself held all of them.
        // Measured on the core 7585f2a6 portals: the MCP server (an AI-module service taking
        // IEnumerable<McpServerTool>) listed one tool of 37, `restore_from_point_in_time`, the last
        // one declared; every other tool answered "Unknown tool". Pinned by
        // ModuleServicesKeepEveryRegistrationTest.
        if (MeshServiceProvider.From(root) is { } rootScope)
        {
            // 🚨 Resolved in the requesting module scope's OWN root scope, never in the root itself
            // (MeshWeaver#6432, #6391). A root registration's lifetime is relative to the scope that
            // resolves it: resolved in the root, a SCOPED service (PortalApplication) became a
            // root-shared instance — and its constructor reads the mesh hub's scope. In a root scope a
            // singleton is still the root's one instance, a scoped service is that scope's, and a
            // transient's disposal is tracked there and ends with the module scope.
            foreach (var rootRegistration in rootScope.RegistrationsOf(type))
            {
                var stood = rootRegistration;
                yield return ServiceRegistration.ForDelegate(type, null,
                    scope => scope.GetRequiredService<RootResolutionScope>().Scope.ResolveRegistration(stood, type),
                    // Names the root registration it resolves, so the scoped-dependency walk follows
                    // exactly what this module's fallback reaches (ScopedDependencyWalk).
                    standsInFor: stood);
            }
            yield break;
        }
        // A root that is not a MeshWeaver container can only be asked by type: each stand-in takes its
        // element of the root's enumeration, in order.
        var count = root.GetServices(type).Count();
        for (var position = 0; position < count; position++)
        {
            var at = position;
            yield return ServiceRegistration.ForDelegate(type, null, _ => root.GetServices(type).ElementAt(at)!);
        }
    }

    private static Type? Close(ServiceDescriptor open, Type type)
    {
        try
        {
            return open.ImplementationType!.MakeGenericType(type.GetGenericArguments());
        }
        catch (ArgumentException)
        {
            return null; // the arguments break the implementation's constraints: not a match
        }
    }
}

/// <summary>
/// Whether building a registration would build a SCOPED service, read STRUCTURALLY off the
/// registrations — never off type names (MeshWeaver#6556,
/// <see cref="ModuleServiceProvider.BuildRootReachableSingletons(IServiceProvider, IServiceProvider, Func{Type, object?, bool})"/>).
/// A reflection-activated registration depends on every parameter type of its public constructors — a
/// superset of the one the container picks, and over-approximating only leaves a singleton to its first
/// resolve; a delegate or a provided instance names none. Followed through transients and the module's own
/// singletons, and into the root's registrations for every type that names no collectible assembly — which
/// is what the module's fallback resolves there. A ROOT singleton ends the walk: it is the root's one
/// instance, built in the root whoever asks. One walk per build, never shared.
/// </summary>
internal sealed class ScopedDependencyWalk(
    MeshServiceProvider module,
    MeshServiceProvider? root,
    Func<Type, IEnumerable<(IServiceProvider Container, object? Key)>>? elsewhere = null)
{
    /// <summary>Which container a registration lives in — what its dependencies are looked up in.</summary>
    internal enum Side
    {
        /// <summary>The module container.</summary>
        Module,

        /// <summary>The mesh's root container.</summary>
        Root,
    }

    private ImmutableDictionary<(object Identity, Type Type), bool> verdicts = ImmutableDictionary<(object Identity, Type Type), bool>.Empty;

    /// <summary>Whether building <paramref name="registration"/> builds a scoped service on the way.</summary>
    internal bool ReachesAScopedService(ServiceRegistration registration, Side side) =>
        ReachesAScopedService(registration, side == Side.Module ? module : null);

    // container = the module container the registration lives in (this module's, or another module's a
    // module-declared type is resolved from); null = the root.
    private bool ReachesAScopedService(ServiceRegistration registration, MeshServiceProvider? container)
    {
        var key = (registration.Identity, registration.ServiceType);
        if (verdicts.TryGetValue(key, out var known))
            return known;
        // Marked while in progress: a cycle reaches nothing new through itself.
        verdicts = verdicts.SetItem(key, false);
        var verdict = Dependencies(registration)
            .SelectMany(type => RegistrationsOf(type, container))
            .Any(dependency => dependency.Registration.IsScoped
                               || (!(dependency.Container is null && dependency.Registration.IsSingleton)
                                   && ReachesAScopedService(dependency.Registration, dependency.Container)));
        verdicts = verdicts.SetItem(key, verdict);
        return verdict;
    }

    // 🚨 What a MODULE container answers for `type`, exactly as it resolves it: the module's own
    // registrations; for a type a module DECLARES, the registrations in the other module containers that
    // hold it (read THEIR lifetimes, or another module's SCOPED type looks like no dependency at all); and a
    // root registration only where the module's fallback stands in for it. The fallback consults the root
    // only when the module registers no `T` of its own, so a module that overrides a root-SCOPED `T` with its
    // own singleton never reaches the root's `T` — and that unreachable registration must not mark a
    // root-reachable singleton as scoped. container = the module container asked; null = the root.
    private IEnumerable<(ServiceRegistration Registration, MeshServiceProvider? Container)> RegistrationsOf(
        Type type, MeshServiceProvider? container)
    {
        if (container is not null)
        {
            var any = false;
            foreach (var own in container.RegistrationsOf(type))
            {
                any = true;
                yield return own.StandsInFor is ServiceRegistration inRoot ? (inRoot, null) : (own, container);
            }
            if (MeshServiceProvider.NamesACollectibleAssembly(type) && elsewhere is not null)
                foreach (var (other, key) in elsewhere(type))
                    if (MeshServiceProvider.From(other) is { } otherScope && !ReferenceEquals(otherScope, container))
                        foreach (var there in otherScope.OwnRegistrationsOf(type, key))
                            yield return (there, otherScope);
            if (any)
                yield break;
        }
        // Never ASK the root about a type that names a collectible assembly — the module's fallback
        // refuses the same.
        if (root is not null && !MeshServiceProvider.NamesACollectibleAssembly(type))
            foreach (var inRoot in root.RegistrationsOf(type))
                yield return (inRoot, null);
    }

    private static IEnumerable<Type> Dependencies(ServiceRegistration registration) =>
        registration.ImplementationType is { } implementation && registration.Instance is null && !registration.IsFactory
            ? implementation
                .GetConstructors(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .SelectMany(c => c.GetParameters())
                .Select(p => BuiltWith(p.ParameterType))
                .OfType<Type>()
                .Distinct()
            : [];

    // The type a constructor parameter builds WITH its owner: a collection's element; nothing for a
    // deferred factory (Lazy<T>, Func<T>) or the container itself.
    private static Type? BuiltWith(Type parameter)
    {
        if (parameter == typeof(IServiceProvider) || parameter == typeof(IServiceScopeFactory)
            || parameter == typeof(MeshServiceProvider) || parameter == typeof(IKeyedServiceProvider))
            return null;
        if (parameter.IsArray)
            return parameter.GetElementType();
        if (!parameter.IsConstructedGenericType)
            return parameter;
        var definition = parameter.GetGenericTypeDefinition();
        if (definition == typeof(Lazy<>) || definition == typeof(Func<>))
            return null;
        if (definition == typeof(IEnumerable<>) || definition == typeof(IReadOnlyCollection<>)
            || definition == typeof(IReadOnlyList<>) || definition == typeof(ICollection<>) || definition == typeof(IList<>))
            return parameter.GetGenericArguments()[0];
        return parameter;
    }
}
