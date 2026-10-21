#nullable enable
using System.Collections.Immutable;
using System.Reflection;
using Autofac;
using Autofac.Builder;
using Autofac.Core;
using Autofac.Core.Lifetime;
using Autofac.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.ServiceProvider;

/// <summary>
/// A service provider for ONE generation of a module, in a container of its OWN — never a child scope
/// of the mesh's root container (policy <c>module-live-update-default</c>,
/// <c>Doc/Architecture/LiveModuleUpdate</c> → "Root services").
///
/// <para>🚨 <b>Why not a child lifetime scope.</b> Measured: an Autofac child scope — isolated or not,
/// load-context scope included — asks its PARENT's registry about every service it resolves, and the
/// parent's registered-services tracker caches each one by <see cref="Type"/> for the life of the
/// parent. A module service that took <c>IOptions&lt;ItsOwnOptions&gt;</c> left
/// <c>TypedService(IOptions&lt;ItsOwnOptions&gt;)</c> in the root registry, and the module's
/// collectible context could never be collected. A separate container cannot do that: the root is
/// reached only through <see cref="ModuleFallbackSource"/>, which refuses every type that names the
/// module.</para>
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

    private static IReadOnlyList<Func<ILifetimeScope?, object>> NoRegistrationsElsewhere(Type type) => [];

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
            type => registrationsElsewhere(type).Select(resolve => (Func<ILifetimeScope?, object>)(_ => resolve())).ToArray());
    }

    /// <summary>
    /// As <see cref="Create(IServiceCollection, IServiceProvider, Func{Type, bool}, IEnumerable{ServiceDescriptor}, Func{Type, IReadOnlyList{Func{object}}})"/>,
    /// with resolvers that are told the CALLER a module type is resolved for — the root-side scope (a
    /// Blazor circuit, a request, a hub scope) the asking module scope serves, or null for none — so a
    /// scoped registration in ANOTHER module's container is built in that module's scope owned by the
    /// same caller (MeshWeaver#6584, <see cref="ResolveAt(IServiceProvider, Type, int, ILifetimeScope?)"/>).
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
        Func<Type, IReadOnlyList<Func<ILifetimeScope?, object>>> registrationsElsewhere)
    {
        var builder = new ContainerBuilder();
        // MeshWeaver#6584/#6588: who owns each shared instance this container builds, and the warning a
        // scoped instance built for no caller — or served to a caller that does not own it — writes.
        var diagnostics = new ModuleScopeDiagnostics(
            ModuleScopeOwnership.LoggerFrom(root.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()),
            root.GetService<ModuleInstanceOwners>());
        diagnostics.Watch(builder);
        builder.RegisterInstance(diagnostics).AsSelf().ExternallyOwned();
        builder.PopulateMeshWeaverWithOwners(services, root.GetService<ModuleInstanceOwners>());
        var callers = new ModuleCallerScopes();
        builder.RegisterInstance(callers).AsSelf().ExternallyOwned();
        if (root.GetService<ILifetimeScope>() is { } rootScope)
            // Where every root registration a module scope asks for is resolved (see RootResolutionScope):
            // in a module scope a CALLER owns (MeshWeaver#6584), that caller's own scope — its circuit's
            // NavigationManager and IJSRuntime; in any other module scope, one root child scope of its
            // own, disposed with it.
            builder.Register(context => CallerOfModuleScope(context.Resolve<ILifetimeScope>()) is { } caller
                    ? new RootResolutionScope(caller, owned: false)
                    : new RootResolutionScope(rootScope.BeginLifetimeScope(), owned: true))
                .AsSelf()
                .InstancePerLifetimeScope();
        builder.RegisterSource(new ModuleFallbackSource(root, isModuleType, rootOpenGenerics, registrationsElsewhere));
        var container = builder.Build();
        callers.Attach(container);
        // The callers' module scopes end FIRST, before the generation's singletons they may use.
        container.CurrentScopeEnding += (_, _) => callers.Dispose();
        return new AutofacServiceProvider(container);
    }

    /// <summary>
    /// The ROOT-side scope a resolution on the mesh's root container is made for — a Blazor circuit's,
    /// a request's, a hub's — or null when it is made in the root container itself (a singleton, or a
    /// resolve off the root provider). What a root forwarder or a root stand-in for a module type passes
    /// as the caller (MeshWeaver#6584).
    /// </summary>
    /// <param name="scope">The scope the resolution is made in.</param>
    public static ILifetimeScope? CallerOfRootScope(ILifetimeScope? scope) =>
        scope is null || (scope is ISharingLifetimeScope sharing && ReferenceEquals(sharing.RootLifetimeScope, sharing))
            ? null
            : scope;

    /// <summary>As <see cref="CallerOfRootScope(ILifetimeScope?)"/>, for a root-side provider.</summary>
    /// <param name="provider">The provider the resolution is made from.</param>
    public static ILifetimeScope? CallerOfRootScope(IServiceProvider? provider) =>
        CallerOfRootScope(provider?.GetService<ILifetimeScope>());

    /// <summary>
    /// The caller a MODULE container's scope serves: the root-side scope that owns it (or owns a scope it
    /// was begun from) — or null for the module container's own root and every scope begun from it.
    /// </summary>
    /// <param name="scope">A scope of a module container.</param>
    public static ILifetimeScope? CallerOfModuleScope(ILifetimeScope? scope)
    {
        for (var current = scope as ISharingLifetimeScope; current is not null; current = current.ParentLifetimeScope)
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
    /// dependencies come from a root child scope no circuit initialised: Radzen's <c>ThemeService</c> was
    /// one instance for every user, and <c>DialogService</c> subscribed to a <c>NavigationManager</c> that
    /// throws <c>'RemoteNavigationManager' has not been initialized</c>. In the caller's own module scope
    /// it is that caller's instance, built against that caller's <c>NavigationManager</c> and
    /// <c>IJSRuntime</c>.</para>
    /// </summary>
    /// <param name="moduleContainer">A provider <see cref="CreateForCallers"/> built.</param>
    /// <param name="caller">The root-side scope resolving, or null.</param>
    public static IServiceProvider InCallerScope(IServiceProvider moduleContainer, ILifetimeScope? caller)
    {
        ArgumentNullException.ThrowIfNull(moduleContainer);
        if (caller is null || moduleContainer.GetService<ILifetimeScope>() is not { } scope)
            return moduleContainer;
        return InCallerScope(scope, caller).Resolve<IServiceProvider>();
    }

    private static ILifetimeScope InCallerScope(ILifetimeScope moduleScope, ILifetimeScope caller) =>
        moduleScope.ResolveOptional<ModuleCallerScopes>() is { } callers ? callers.For(caller) : moduleScope;

    /// <summary>
    /// 🚨 <b>Built before published</b> (MeshWeaver#6542, MeshWeaver#6556): builds every
    /// PROCESS-LIFETIME instance of <paramref name="moduleContainer"/> the mesh's root can reach — each
    /// SINGLETON exposing a service <paramref name="rootReachable"/> accepts — unless building it would
    /// build a SCOPED service. The caller does this BEFORE the container can be reached from the mesh's
    /// root container, so the root only ever READS those instances: Autofac answers an instance that
    /// exists without any lock, so a thread holding the root container's shared-instance lock never
    /// waits for a module container's.
    ///
    /// <para><b>Why.</b> Autofac holds a scope's shared-instance lock for the whole build of a shared
    /// instance, and that one lock covers every shared instance of the scope. A root singleton whose
    /// construction reached a module's service not built yet took the root's lock, then the module's; a
    /// module singleton whose construction needed a root singleton not built yet took them the other
    /// way round. Two first-time builds in those orders deadlocked. Taking the root's lock first on the
    /// module side was measured and rejected: a module constructor then ran under the root's lock, so
    /// one slow module constructor stalled every first-time root resolve in the process (#6432).</para>
    ///
    /// <para><b>What it does NOT build, and why (MeshWeaver#6556).</b> A SCOPED registration: its
    /// instance belongs to a scope — a Blazor circuit, a request — so the one a boot or a swap would
    /// build is an instance no scope asked for, made against scoped state nobody initialised. Radzen's
    /// <c>DialogService</c> subscribes to a <c>NavigationManager</c> in its constructor; the boot-built
    /// one threw <c>'RemoteNavigationManager' has not been initialized</c> and withdrew the whole
    /// module. Nor a singleton whose constructor graph — read STRUCTURALLY off the registrations
    /// (<see cref="ScopedDependencyWalk"/>) — reaches a scoped registration: it is not a sound
    /// process-lifetime instance, and building it at boot runs the same uninitialised scoped state.
    /// Neither is a path the root's lock order needs — a root singleton that takes a scoped service is a
    /// captive dependency, not a way the root reaches a module. A delegate-activated registration names
    /// no dependencies, so it is built.</para>
    ///
    /// <para><b>What it reaches.</b> Every such registration, in registration order, and — transitively —
    /// everything their constructors take: a closed generic over a module type
    /// (<c>IOptions&lt;ItsOptions&gt;</c>, <c>ILogger&lt;ItsType&gt;</c>) that a built instance takes is
    /// built with it, and so is the root child scope the module's fallback resolves in. It does NOT
    /// reach a shared instance that only a registration SOURCE makes on demand and that no constructor
    /// takes — a closed generic resolved later from inside a factory or a method. That is the residual
    /// (<c>Doc/Architecture/LiveModuleUpdate</c> → "Built before published").</para>
    ///
    /// <para>Throws what a constructor throws; the caller fails the module's load or swap with it, and
    /// disposes the container.</para>
    /// </summary>
    /// <param name="moduleContainer">A provider <see cref="Create(IServiceCollection, IServiceProvider, Func{Type, bool}, IEnumerable{ServiceDescriptor})"/>
    /// built, not yet reachable from the root.</param>
    /// <param name="root">The mesh's root provider — whose registrations the module's fallback resolves.</param>
    /// <param name="rootReachable">The services the root reaches the module by — its forwarders and its
    /// module-type stand-ins. A singleton exposing none of them is left to its first resolve.</param>
    public static void BuildRootReachableSingletons(
        IServiceProvider moduleContainer, IServiceProvider root, Func<Service, bool> rootReachable)
        => BuildRootReachableSingletons(moduleContainer, root, rootReachable, NoRegistriesElsewhere);

    /// <summary>
    /// As <see cref="BuildRootReachableSingletons(IServiceProvider, IServiceProvider, Func{Service, bool})"/>,
    /// with the registrations a type a module DECLARES has in OTHER module containers — so a singleton
    /// whose constructor takes another module's SCOPED type is recognised as reaching a scoped service
    /// and is left to its first resolve. Without it the walk sees only this container and the root, and
    /// the root is never asked about a module's type: the singleton was judged sound, built at boot, and
    /// built the other module's scoped service with no caller, against scoped state nobody initialised —
    /// which threw and withdrew the module.
    /// </summary>
    /// <param name="moduleContainer">A provider <see cref="Create(IServiceCollection, IServiceProvider, Func{Type, bool}, IEnumerable{ServiceDescriptor})"/>
    /// built, not yet reachable from the root.</param>
    /// <param name="root">The mesh's root provider — whose registrations the module's fallback resolves.</param>
    /// <param name="rootReachable">The services the root reaches the module by.</param>
    /// <param name="registrationsElsewhere">For a type a module declares, EVERY module container that
    /// registers it and the service it is held under there — what <c>ModuleFallbackSource</c> resolves it
    /// from. The walk skips the container it is currently in, so this must not pre-exclude the module the
    /// walk started in: a nested walk (A → B → A) reaches A's registrations from inside B.</param>
    public static void BuildRootReachableSingletons(
        IServiceProvider moduleContainer, IServiceProvider root, Func<Service, bool> rootReachable,
        Func<Type, IEnumerable<(IComponentRegistry Registry, Service Service)>> registrationsElsewhere)
    {
        ArgumentNullException.ThrowIfNull(moduleContainer);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(rootReachable);
        ArgumentNullException.ThrowIfNull(registrationsElsewhere);
        if (moduleContainer.GetService<ILifetimeScope>() is not { } scope)
            return;
        var walk = new ScopedDependencyWalk(scope.ComponentRegistry, root.GetService<ILifetimeScope>()?.ComponentRegistry,
            registrationsElsewhere);
        var registrations = scope.ComponentRegistry.Registrations
            .Where(r => ScopedDependencyWalk.IsSingleton(r)
                        && r.Services.Any(rootReachable)
                        && !walk.ReachesAScopedService(r, ScopedDependencyWalk.Side.Module))
            .ToArray();
        foreach (var registration in registrations)
        {
            if (registration.Services.FirstOrDefault(rootReachable) is not { } service)
                continue;
            var serviceRegistration = scope.ComponentRegistry.ServiceRegistrationsFor(service)
                .FirstOrDefault(r => ReferenceEquals(r.Registration, registration));
            if (serviceRegistration.Registration is null)
                continue;
            scope.ResolveComponent(new ResolveRequest(service, serviceRegistration, []));
        }
    }

    private static IEnumerable<(IComponentRegistry Registry, Service Service)> NoRegistriesElsewhere(Type type) => [];

    /// <summary>
    /// One stand-in registration per resolver in <paramref name="resolvers"/>, each answering its own
    /// instance, created in order. When <paramref name="explicitRegistrationsExist"/> they are yielded in
    /// order — Autofac keeps an explicit registration the default, so the list reads explicit-then-these;
    /// otherwise last-first, because the FIRST registration a source yields becomes the default and that
    /// must be the LAST of the list, as in a single collection.
    /// </summary>
    /// <param name="type">The service type the stand-ins expose.</param>
    /// <param name="resolvers">One resolver per registration, in order.</param>
    /// <param name="explicitRegistrationsExist">Whether the container registers the type itself.</param>
    public static IEnumerable<IComponentRegistration> StandIns(
        Type type, IReadOnlyList<Func<object>> resolvers, bool explicitRegistrationsExist)
    {
        var standIns = resolvers
            .Select(resolve => RegistrationBuilder
                .ForDelegate(type, (_, _) => resolve())
                .ExternallyOwned()
                .InstancePerDependency()
                .CreateRegistration())
            .ToArray();
        return explicitRegistrationsExist ? standIns : standIns.Reverse();
    }

    /// <summary>
    /// As <see cref="StandIns(Type, IReadOnlyList{Func{object}}, bool)"/>, each resolver told the caller
    /// <paramref name="callerOf"/> reads off the scope the stand-in is resolved in (MeshWeaver#6584).
    /// </summary>
    /// <param name="type">The service type the stand-ins expose.</param>
    /// <param name="resolvers">One resolver per registration, in order.</param>
    /// <param name="explicitRegistrationsExist">Whether the container registers the type itself.</param>
    /// <param name="callerOf">The caller a resolving scope serves — <see cref="CallerOfRootScope(ILifetimeScope?)"/>
    /// on the root, <see cref="CallerOfModuleScope"/> in a module container.</param>
    public static IEnumerable<IComponentRegistration> StandIns(
        Type type, IReadOnlyList<Func<ILifetimeScope?, object>> resolvers, bool explicitRegistrationsExist,
        Func<ILifetimeScope?, ILifetimeScope?> callerOf)
    {
        ArgumentNullException.ThrowIfNull(callerOf);
        var standIns = resolvers
            .Select(resolve => RegistrationBuilder
                .ForDelegate(type, (context, _) => resolve(callerOf(context.Resolve<ILifetimeScope>())))
                .ExternallyOwned()
                .InstancePerDependency()
                .CreateRegistration())
            .ToArray();
        return explicitRegistrationsExist ? standIns : standIns.Reverse();
    }

    /// <summary>
    /// As <see cref="ResolveAt(IServiceProvider, Type, int)"/>, for <paramref name="caller"/>: a registration
    /// that is not a singleton is resolved in the module scope the caller owns
    /// (<see cref="InCallerScope(IServiceProvider, ILifetimeScope?)"/>) — that caller's instance, built
    /// against that caller's scope — and a singleton in the module container, the generation's one
    /// instance, as before.
    /// </summary>
    /// <param name="provider">The provider holding the registrations — a module container.</param>
    /// <param name="serviceType">The (closed) service type.</param>
    /// <param name="position">Zero-based position among that type's registrations.</param>
    /// <param name="caller">The root-side scope resolving, or null.</param>
    public static object ResolveAt(IServiceProvider provider, Type serviceType, int position, ILifetimeScope? caller)
    {
        if (caller is null || provider.GetService<ILifetimeScope>() is not { } scope)
            return ResolveAt(provider, serviceType, position);
        var service = new TypedService(serviceType);
        var registration = scope.ComponentRegistry.ServiceRegistrationsFor(service)
            .OrderBy(r => r.GetRegistrationOrder())
            .ElementAtOrDefault(position);
        if (registration.Registration is null)
            throw new InvalidOperationException(
                $"{serviceType} has no registration at position {position} in this module container.");
        if (ScopedDependencyWalk.IsSingleton(registration.Registration))
            return scope.ResolveComponent(new ResolveRequest(service, registration, []));
        var instance = InCallerScope(scope, caller).ResolveComponent(new ResolveRequest(service, registration, []));
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
    public static void ReportIfServedAcrossCallers(IServiceProvider moduleContainer, object instance, ILifetimeScope? caller)
    {
        ArgumentNullException.ThrowIfNull(moduleContainer);
        ArgumentNullException.ThrowIfNull(instance);
        if (caller is not null
            && moduleContainer.GetService<ILifetimeScope>()?.ResolveOptional<ModuleScopeDiagnostics>() is { } diagnostics)
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
        if (provider.GetService<ILifetimeScope>() is not { } scope)
            return provider.GetServices(serviceType).ElementAt(position)
                   ?? throw new InvalidOperationException($"Registration {position} of {serviceType} resolved to null.");
        var service = new TypedService(serviceType);
        var registration = scope.ComponentRegistry.ServiceRegistrationsFor(service)
            .OrderBy(r => r.GetRegistrationOrder())
            .ElementAtOrDefault(position);
        if (registration.Registration is null)
            throw new InvalidOperationException(
                $"{serviceType} has no registration at position {position} in this module container.");
        return scope.ResolveComponent(new ResolveRequest(service, registration, []));
    }
}

/// <summary>
/// Where one module scope resolves every root registration it asks for, so the root's lifetimes keep
/// their meaning across the container boundary: a singleton is the root's one instance, while a scoped
/// service is never built — and shared, under the root container's lock — in the root itself
/// (MeshWeaver#6432). For a module scope a CALLER owns (MeshWeaver#6584) that is the caller's own scope —
/// a circuit's <c>NavigationManager</c> is that circuit's — and is not disposed here; for any other
/// module scope it is a root child scope of its own, disposed with it.
/// </summary>
internal sealed class RootResolutionScope(ILifetimeScope scope, bool owned) : IDisposable
{
    /// <summary>The scope root registrations are resolved in.</summary>
    public ILifetimeScope Scope { get; } = scope;

    /// <inheritdoc />
    public void Dispose()
    {
        if (owned)
            Scope.Dispose();
    }
}

/// <summary>The tag of a module scope a caller owns — names the caller.</summary>
internal sealed class CallerTag(ILifetimeScope caller)
{
    /// <summary>The root-side scope that owns the module scope.</summary>
    public ILifetimeScope Caller { get; } = caller;

    /// <inheritdoc />
    public override string ToString() => $"module scope owned by {Caller.Tag}";
}

/// <summary>
/// The module scopes CALLERS own in one module container (MeshWeaver#6584,
/// <see cref="ModuleServiceProvider.InCallerScope(IServiceProvider, ILifetimeScope?)"/>): one per caller,
/// begun on first use, disposed when the caller ends — it is handed to the caller's own disposer — or when
/// the generation ends, whichever is first. Held per container, never static.
/// </summary>
internal sealed class ModuleCallerScopes : IDisposable
{
    private readonly object gate = new();
    private ILifetimeScope? container;
    private ImmutableDictionary<ILifetimeScope, CallerOwnedModuleScope> scopes =
        ImmutableDictionary.Create<ILifetimeScope, CallerOwnedModuleScope>(ReferenceEqualityComparer.Instance);
    private bool disposed;

    /// <summary>Binds the container the scopes are begun from — once it is built.</summary>
    public void Attach(ILifetimeScope moduleContainer) => container = moduleContainer;

    /// <summary>The module scope <paramref name="caller"/> owns, begun on first use.</summary>
    public ILifetimeScope For(ILifetimeScope caller)
    {
        CallerOwnedModuleScope created;
        ILifetimeScope begun;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (scopes.TryGetValue(caller, out var existing) && existing.Scope is { } live)
                return live;
            begun = (container ?? throw new InvalidOperationException("The module container is not built yet."))
                .BeginLifetimeScope(new CallerTag(caller));
            // ONE lock for the caller and every module scope it owns, before anything can resolve in it.
            CallerSharedInstanceLock.Share(caller, begun);
            created = new CallerOwnedModuleScope(this, caller, begun);
            scopes = scopes.SetItem(caller, created);
        }
        try
        {
            // Owned by the caller: it ends when the caller ends, disposed the way the caller is (a Blazor
            // circuit's scope is disposed asynchronously).
            caller.Disposer.AddInstanceForAsyncDisposal(created);
        }
        catch (ObjectDisposedException)
        {
            created.Dispose();
            throw;
        }
        return begun;
    }

    /// <summary>Drops <paramref name="owned"/> once it has ended.</summary>
    public void Forget(ILifetimeScope caller, CallerOwnedModuleScope owned)
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
        ImmutableDictionary<ILifetimeScope, CallerOwnedModuleScope> ending;
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
/// 🚨 <b>One lock owner per caller</b> (MeshWeaver#6584). Autofac builds every shared instance of a scope
/// under that scope's one shared-instance lock, held for the whole build. A module scope a caller owns is
/// a scope of the MODULE container, so it had a lock of its own — and the two are taken in both orders: a
/// caller-scoped ROOT service whose construction reaches a module's scoped service holds the caller's lock
/// and waits for the module scope's, while that module service's construction, taking a caller-scoped
/// root service not built yet (its root resolutions are made in the caller's own scope), holds the module
/// scope's lock and waits for the caller's. Two first builds overlapping in those orders deadlocked.
///
/// <para>So a caller-owned module scope — and every scope begun from it — takes its CALLER's lock as its
/// own: one monitor for the caller and everything it owns, re-entered (never waited for) by the thread
/// already holding it, so no order between them exists to invert. Every road into the module scope is
/// covered, including a module service that keeps its <see cref="IServiceProvider"/> and resolves later.
/// <c>CallerAndModuleScopedServicesBuildInOneLockTest</c> forces the overlap; its negative control begins
/// the same scope without the shared lock and deadlocks.</para>
///
/// <para>Autofac exposes no way to hand a scope its lock, so the scope's lock field is set before the
/// scope is handed to anyone. Autofac is pinned (<c>Directory.Packages.props</c>); a version whose
/// <c>LifetimeScope</c> no longer carries that field fails here, loudly, at the first caller — never a
/// silent return of the deadlock — and <c>CallerAndModuleScopedServicesBuildInOneLockTest</c> fails with it.</para>
/// </summary>
internal static class CallerSharedInstanceLock
{
    private static readonly FieldInfo? SyncRoot =
        typeof(LifetimeScope).GetField("_synchRoot", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>Makes <paramref name="owned"/> — and every scope later begun from it — build its shared
    /// instances under <paramref name="caller"/>'s lock.</summary>
    /// <param name="caller">The root-side scope that owns <paramref name="owned"/>.</param>
    /// <param name="owned">A module scope just begun for <paramref name="caller"/>, not yet handed out.</param>
    public static void Share(ILifetimeScope caller, ILifetimeScope owned)
    {
        var callerLock = LockOf(caller);
        Adopt(owned, callerLock);
    }

    private static void Adopt(ILifetimeScope scope, object callerLock)
    {
        SyncRoot!.SetValue(AsLifetimeScope(scope), callerLock);
        scope.ChildLifetimeScopeBeginning += (_, args) => Adopt(args.LifetimeScope, callerLock);
    }

    private static object LockOf(ILifetimeScope scope) =>
        SyncRoot?.GetValue(AsLifetimeScope(scope))
        ?? throw new InvalidOperationException(
            "Autofac's LifetimeScope no longer carries the shared-instance lock field '_synchRoot': a module "
            + "scope a caller owns cannot share its caller's lock, and the two would deadlock (MeshWeaver#6584). "
            + "Re-establish the shared lock for this Autofac version before upgrading.");

    private static LifetimeScope AsLifetimeScope(ILifetimeScope scope) =>
        scope as LifetimeScope
        ?? throw new InvalidOperationException(
            $"A caller scope must be an Autofac LifetimeScope to share its lock; got {scope.GetType()} (MeshWeaver#6584).");
}

/// <summary>
/// One caller's module scope, as the caller's disposer holds it. Disposing it ends the scope and lets
/// go of it, so a caller that outlives the generation (a long-lived hub scope across a live swap) holds
/// nothing of that generation — the module's collectible context stays collectible.
/// </summary>
internal sealed class CallerOwnedModuleScope : IDisposable, IAsyncDisposable
{
    private ModuleCallerScopes? owner;
    private ILifetimeScope? caller;
    private ILifetimeScope? scope;

    public CallerOwnedModuleScope(ModuleCallerScopes owner, ILifetimeScope caller, ILifetimeScope scope)
    {
        this.owner = owner;
        this.caller = caller;
        this.scope = scope;
    }

    /// <summary>The module scope, or null once it ended.</summary>
    public ILifetimeScope? Scope => Volatile.Read(ref scope);

    /// <inheritdoc />
    public void Dispose() => Release()?.Dispose();

    /// <inheritdoc />
    public ValueTask DisposeAsync() => Release()?.DisposeAsync() ?? ValueTask.CompletedTask;

    private ILifetimeScope? Release()
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
    Func<Type, IReadOnlyList<Func<ILifetimeScope?, object>>> registrationsElsewhere) : IRegistrationSource
{
    private readonly IReadOnlyDictionary<Type, ServiceDescriptor> openGenerics = rootOpenGenerics
        .Where(d => d.ServiceType.IsGenericTypeDefinition && d.ImplementationType is not null && !d.IsKeyedService)
        .GroupBy(d => d.ServiceType)
        .ToDictionary(g => g.Key, g => g.Last());

    public bool IsAdapterForIndividualComponents => false;

    public IEnumerable<IComponentRegistration> RegistrationsFor(
        Service service, Func<Service, IEnumerable<ServiceRegistration>> registrationAccessor)
    {
        if (service is not TypedService typed)
            yield break;
        var type = typed.ServiceType;
        var ownRegistrations = registrationAccessor(service).Any();
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
            foreach (var standIn in ModuleServiceProvider.StandIns(type, elsewhere, ownRegistrations,
                         ModuleServiceProvider.CallerOfModuleScope))
                yield return standIn;
            yield break;
        }
        if (isModuleType(type))
        {
            // A closed generic over a module type nobody registered closed: closed HERE, from the
            // root's open generic.
            if (!ownRegistrations && type.IsConstructedGenericType
                && openGenerics.TryGetValue(type.GetGenericTypeDefinition(), out var open))
            {
                var registration = RegistrationBuilder
                    .ForType(open.ImplementationType!.MakeGenericType(type.GetGenericArguments()))
                    .As(type);
                yield return (open.Lifetime switch
                {
                    ServiceLifetime.Singleton => registration.SingleInstance(),
                    ServiceLifetime.Scoped => registration.InstancePerLifetimeScope(),
                    _ => registration.InstancePerDependency(),
                }).CreateRegistration();
            }
            yield break; // never ask the root about a module type — that is what pins it
        }
        if (ownRegistrations)
            yield break; // the module registers it itself
        if (root.GetService<IServiceProviderIsService>() is { } isService && !isService.IsService(type))
            yield break;
        // 🚨 One stand-in PER ROOT REGISTRATION, never one for the type. A single
        // `root.GetRequiredService(type)` stand-in made every IEnumerable<T> a module service took
        // contain exactly ONE element — the root's last — while the root itself held all of them.
        // Measured on the core 7585f2a6 portals: the MCP server (an AI-module service taking
        // IEnumerable<McpServerTool>) listed one tool of 37, `restore_from_point_in_time`, the last
        // one declared; every other tool answered "Unknown tool".
        if (root.GetService<ILifetimeScope>() is { } rootScope)
        {
            var rootRegistrations = rootScope.ComponentRegistry.ServiceRegistrationsFor(service)
                .OrderBy(r => r.GetRegistrationOrder())
                .ToArray();
            // CREATED in the root's order, YIELDED last-first. Autofac stamps a registration's order
            // when it is created, and its collection resolution sorts by that order, so
            // IEnumerable<T> enumerates in the root's order; and it makes the FIRST registration a
            // source yields the default (what a single-service resolve answers), so that is the
            // root's LAST, as in the root. Both halves are pinned by
            // ModuleServicesKeepEveryRegistrationTest (yielded first-first, the single resolve
            // answered the root's FIRST registration).
            // 🚨 Resolved in the requesting module scope's OWN root child scope, never in the root
            // itself (MeshWeaver#6432, #6391). A root registration's lifetime is relative to the scope
            // that resolves it: resolved in the root, a SCOPED service (PortalApplication) became a
            // root-shared instance, built under the root container's shared-instance lock — and its
            // constructor reads the mesh hub's scope. A grain activation building a mesh-hub-scope
            // service whose constructor needs a root singleton takes the two locks the other way
            // round, and the silo stood still for the rest of its life. In a child scope a singleton
            // is still the root's one instance, a scoped service is that scope's, and a transient's
            // disposal is tracked there and ends with the module scope — as when the module's
            // registrations lived in the root and it resolved them from root child scopes.
            var standIns = rootRegistrations
                .Select(rootRegistration => RegistrationBuilder
                    .ForDelegate(type, (context, _) => context.Resolve<RootResolutionScope>().Scope
                        .ResolveComponent(new ResolveRequest(service, rootRegistration, [])))
                    // Names the root registration it resolves, so the scoped-dependency walk follows
                    // exactly what this module's fallback reaches (ScopedDependencyWalk).
                    .WithMetadata(ScopedDependencyWalk.StandsInFor, rootRegistration.Registration)
                    .ExternallyOwned()
                    .InstancePerDependency()
                    .CreateRegistration())
                .ToArray();
            for (var i = standIns.Length - 1; i >= 0; i--)
                yield return standIns[i];
            yield break;
        }
        // A root that is not an Autofac container (the mesh's never is — SetupModules requires one)
        // can only be asked by type: each stand-in takes its element of the root's enumeration, in the
        // same created-in-order, yielded-last-first shape.
        var positions = Enumerable.Range(0, root.GetServices(type).Count())
            .Select(position => RegistrationBuilder
                .ForDelegate(type, (_, _) => root.GetServices(type).ElementAt(position)!)
                .ExternallyOwned()
                .InstancePerDependency()
                .CreateRegistration())
            .ToArray();
        for (var i = positions.Length - 1; i >= 0; i--)
            yield return positions[i];
    }
}

/// <summary>
/// Whether building a registration would build a SCOPED service, read STRUCTURALLY off the
/// registrations — never off type names (MeshWeaver#6556,
/// <see cref="ModuleServiceProvider.BuildRootReachableSingletons(IServiceProvider, IServiceProvider, Func{Service, bool})"/>). A reflection-activated
/// registration depends on every parameter type of its public constructors — a superset of the one
/// Autofac picks, and over-approximating only leaves a singleton to its first resolve; a delegate or a
/// provided instance names none. Followed through transients and the module's own singletons, and into
/// the root's registrations for every type that names no collectible assembly — which is what the
/// module's fallback resolves there. A ROOT singleton ends the walk: it is the root's one instance,
/// built in the root whoever asks. One walk per build, never shared.
/// </summary>
internal sealed class ScopedDependencyWalk(
    IComponentRegistry module,
    IComponentRegistry? root,
    Func<Type, IEnumerable<(IComponentRegistry Registry, Service Service)>>? elsewhere = null)
{
    /// <summary>Which container a registration lives in — what its dependencies are looked up in.</summary>
    internal enum Side
    {
        /// <summary>The module container.</summary>
        Module,

        /// <summary>The mesh's root container.</summary>
        Root,
    }

    private ImmutableDictionary<IComponentRegistration, bool> verdicts =
        ImmutableDictionary.Create<IComponentRegistration, bool>(ReferenceEqualityComparer.Instance);

    /// <summary>A process-lifetime instance: shared at its container's root.</summary>
    internal static bool IsSingleton(IComponentRegistration registration) =>
        registration.Sharing == InstanceSharing.Shared && registration.Lifetime is Autofac.Core.Lifetime.RootScopeLifetime;

    /// <summary>One instance per scope — a circuit's, a request's, a hub's.</summary>
    private static bool IsScoped(IComponentRegistration registration) =>
        registration.Sharing == InstanceSharing.Shared && registration.Lifetime is not Autofac.Core.Lifetime.RootScopeLifetime;

    /// <summary>Whether building <paramref name="registration"/> builds a scoped service on the way.</summary>
    internal bool ReachesAScopedService(IComponentRegistration registration, Side side) =>
        ReachesAScopedService(registration, side == Side.Module ? module : null);

    // registry = the module container the registration lives in (this module's, or another module's a
    // module-declared type is resolved from); null = the root.
    private bool ReachesAScopedService(IComponentRegistration registration, IComponentRegistry? registry)
    {
        if (verdicts.TryGetValue(registration, out var known))
            return known;
        // Marked while in progress: a cycle reaches nothing new through itself.
        verdicts = verdicts.SetItem(registration, false);
        var verdict = Dependencies(registration)
            .SelectMany(type => RegistrationsOf(type, registry))
            .Any(dependency => IsScoped(dependency.Registration)
                               || (!(dependency.Registry is null && IsSingleton(dependency.Registration))
                                   && ReachesAScopedService(dependency.Registration, dependency.Registry)));
        verdicts = verdicts.SetItem(registration, verdict);
        return verdict;
    }

    /// <summary>The metadata key under which a module container's stand-in for a ROOT registration
    /// (<see cref="ModuleFallbackSource"/>) names the root registration it resolves.</summary>
    internal const string StandsInFor = "MeshWeaver.ModuleFallback.StandsInFor";

    // 🚨 What a MODULE container answers for `type`, exactly as it resolves it: the module's own
    // registrations; for a type a module DECLARES, the registrations in the other module containers
    // that hold it (ModuleFallbackSource adds a stand-in for each, after the module's own — read THEIR
    // lifetimes, or another module's SCOPED type looks like no dependency at all); and a root
    // registration only where the module's fallback stands in for it. The fallback consults the root
    // only when the module registers no `T` of its own, so a module that overrides a root-SCOPED `T`
    // with its own singleton never reaches the root's `T` — and that unreachable registration must not
    // mark a root-reachable singleton as scoped (it would then be left to its first resolve, and the
    // #6542 lock inversion with it). registry = the module container asked; null = the root.
    private IEnumerable<(IComponentRegistration Registration, IComponentRegistry? Registry)> RegistrationsOf(
        Type type, IComponentRegistry? registry)
    {
        var service = new TypedService(type);
        if (registry is not null)
        {
            var any = false;
            foreach (var own in registry.ServiceRegistrationsFor(service))
            {
                any = true;
                yield return own.Registration.Metadata.TryGetValue(StandsInFor, out var stood)
                             && stood is IComponentRegistration inRoot
                    ? (inRoot, null)
                    : (own.Registration, registry);
            }
            if (NamesACollectibleAssembly(type) && elsewhere is not null)
                foreach (var (other, held) in elsewhere(type))
                    if (!ReferenceEquals(other, registry))
                        foreach (var there in other.ServiceRegistrationsFor(held))
                            yield return (there.Registration, other);
            if (any)
                yield break;
        }
        // Never ASK the root about a type that names a collectible assembly: asking caches the type in
        // the root's registry, which pins its context (ModuleFallbackSource refuses the same).
        if (root is not null && !NamesACollectibleAssembly(type))
            foreach (var inRoot in root.ServiceRegistrationsFor(service))
                yield return (inRoot.Registration, null);
    }

    private static IEnumerable<Type> Dependencies(IComponentRegistration registration) =>
        registration.Activator is Autofac.Core.Activators.Reflection.ReflectionActivator reflection
            ? reflection.LimitType
                .GetConstructors(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .SelectMany(c => c.GetParameters())
                .Select(p => BuiltWith(p.ParameterType))
                .OfType<Type>()
                .Distinct()
            : [];

    // The type a constructor parameter builds WITH its owner: a collection's element; nothing for a
    // deferred factory (Lazy<T>, Func<T>, Owned<T>) or the container itself.
    private static Type? BuiltWith(Type parameter)
    {
        if (parameter == typeof(IServiceProvider) || parameter == typeof(IServiceScopeFactory)
            || typeof(IComponentContext).IsAssignableFrom(parameter))
            return null;
        if (parameter.IsArray)
            return parameter.GetElementType();
        if (!parameter.IsConstructedGenericType)
            return parameter;
        var definition = parameter.GetGenericTypeDefinition();
        if (definition == typeof(Lazy<>) || definition == typeof(Func<>) || definition == typeof(Autofac.Features.OwnedInstances.Owned<>))
            return null;
        if (definition == typeof(IEnumerable<>) || definition == typeof(IReadOnlyCollection<>)
            || definition == typeof(IReadOnlyList<>) || definition == typeof(ICollection<>) || definition == typeof(IList<>))
            return parameter.GetGenericArguments()[0];
        return parameter;
    }

    private static bool NamesACollectibleAssembly(Type type) =>
        type.Assembly.IsCollectible
        || (type.IsGenericType && type.GetGenericArguments().Any(NamesACollectibleAssembly))
        || (type.HasElementType && NamesACollectibleAssembly(type.GetElementType()!));
}
