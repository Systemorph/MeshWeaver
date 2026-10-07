#nullable enable
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
        builder.RegisterSource(new ModuleFallbackSource(root, isModuleType, rootOpenGenerics, registrationsElsewhere));
        return new AutofacServiceProvider(builder.Build());
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
            var standIns = rootRegistrations
                .Select(rootRegistration => RegistrationBuilder
                    .ForDelegate(type, (_, _) => rootScope.ResolveComponent(new ResolveRequest(service, rootRegistration, [])))
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
