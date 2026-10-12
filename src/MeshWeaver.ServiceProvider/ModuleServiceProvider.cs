#nullable enable
using System.Collections.Immutable;
using Autofac;
using Autofac.Builder;
using Autofac.Core;
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
        => Create(services, root, isModuleType, rootOpenGenerics, _ => []);

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
        var builder = new ContainerBuilder();
        builder.Populate(services);
        if (root.GetService<ILifetimeScope>() is { } rootScope)
            // One root child scope per module scope, disposed with it — where every root registration
            // a module scope asks for is resolved (see RootResolutionScope).
            builder.Register(_ => new RootResolutionScope(rootScope.BeginLifetimeScope()))
                .AsSelf()
                .InstancePerLifetimeScope();
        builder.RegisterSource(new ModuleFallbackSource(root, isModuleType, rootOpenGenerics, registrationsElsewhere));
        return new AutofacServiceProvider(builder.Build());
    }

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
    {
        ArgumentNullException.ThrowIfNull(moduleContainer);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(rootReachable);
        if (moduleContainer.GetService<ILifetimeScope>() is not { } scope)
            return;
        var walk = new ScopedDependencyWalk(scope.ComponentRegistry, root.GetService<ILifetimeScope>()?.ComponentRegistry);
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
/// A root child scope owned by one module scope — the module container's own root scope or a scope
/// begun from it — and disposed with it. Every root registration that module scope asks for is
/// resolved here, so the root's lifetimes keep their meaning across the container boundary: a
/// singleton is the root's one instance, while a scoped service is never built — and shared, under
/// the root container's lock — in the root itself (MeshWeaver#6432).
/// </summary>
internal sealed class RootResolutionScope(ILifetimeScope scope) : IDisposable
{
    /// <summary>The root child scope.</summary>
    public ILifetimeScope Scope { get; } = scope;

    /// <inheritdoc />
    public void Dispose() => Scope.Dispose();
}

/// <summary>
/// The module container's view of everything it does not register itself — see
/// <see cref="ModuleServiceProvider"/>.
/// </summary>
internal sealed class ModuleFallbackSource(
    IServiceProvider root,
    Func<Type, bool> isModuleType,
    IEnumerable<ServiceDescriptor> rootOpenGenerics,
    Func<Type, IReadOnlyList<Func<object>>> registrationsElsewhere) : IRegistrationSource
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
            foreach (var standIn in ModuleServiceProvider.StandIns(type, elsewhere, ownRegistrations))
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
/// <see cref="ModuleServiceProvider.BuildRootReachableSingletons"/>). A reflection-activated
/// registration depends on every parameter type of its public constructors — a superset of the one
/// Autofac picks, and over-approximating only leaves a singleton to its first resolve; a delegate or a
/// provided instance names none. Followed through transients and the module's own singletons, and into
/// the root's registrations for every type that names no collectible assembly — which is what the
/// module's fallback resolves there. A ROOT singleton ends the walk: it is the root's one instance,
/// built in the root whoever asks. One walk per build, never shared.
/// </summary>
internal sealed class ScopedDependencyWalk(IComponentRegistry module, IComponentRegistry? root)
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
    internal bool ReachesAScopedService(IComponentRegistration registration, Side side)
    {
        if (verdicts.TryGetValue(registration, out var known))
            return known;
        // Marked while in progress: a cycle reaches nothing new through itself.
        verdicts = verdicts.SetItem(registration, false);
        var verdict = Dependencies(registration)
            .SelectMany(type => RegistrationsOf(type, side))
            .Any(dependency => IsScoped(dependency.Registration)
                               || (!(dependency.Side == Side.Root && IsSingleton(dependency.Registration))
                                   && ReachesAScopedService(dependency.Registration, dependency.Side)));
        verdicts = verdicts.SetItem(registration, verdict);
        return verdict;
    }

    /// <summary>The metadata key under which a module container's stand-in for a ROOT registration
    /// (<see cref="ModuleFallbackSource"/>) names the root registration it resolves.</summary>
    internal const string StandsInFor = "MeshWeaver.ModuleFallback.StandsInFor";

    // 🚨 What the MODULE container answers for `type`, exactly as it resolves it: the module's own
    // registrations, and a root registration only where the module's fallback stands in for it. The
    // fallback consults the root only when the module registers no `T` of its own, so a module that
    // overrides a root-SCOPED `T` with its own singleton never reaches the root's `T` — and that
    // unreachable registration must not mark a root-reachable singleton as scoped (it would then be left
    // to its first resolve, and the #6542 lock inversion with it).
    private IEnumerable<(IComponentRegistration Registration, Side Side)> RegistrationsOf(Type type, Side side)
    {
        var service = new TypedService(type);
        if (side == Side.Module)
        {
            var any = false;
            foreach (var own in module.ServiceRegistrationsFor(service))
            {
                any = true;
                yield return own.Registration.Metadata.TryGetValue(StandsInFor, out var stood)
                             && stood is IComponentRegistration inRoot
                    ? (inRoot, Side.Root)
                    : (own.Registration, Side.Module);
            }
            if (any)
                yield break;
        }
        // Never ASK the root about a type that names a collectible assembly: asking caches the type in
        // the root's registry, which pins its context (ModuleFallbackSource refuses the same).
        if (root is not null && !NamesACollectibleAssembly(type))
            foreach (var inRoot in root.ServiceRegistrationsFor(service))
                yield return (inRoot.Registration, Side.Root);
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
