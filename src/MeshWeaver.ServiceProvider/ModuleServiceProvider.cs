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
    {
        var builder = new ContainerBuilder();
        builder.Populate(services);
        builder.RegisterSource(new ModuleFallbackSource(root, isModuleType, rootOpenGenerics));
        return new AutofacServiceProvider(builder.Build());
    }
}

/// <summary>
/// The module container's view of everything it does not register itself — see
/// <see cref="ModuleServiceProvider"/>.
/// </summary>
internal sealed class ModuleFallbackSource(
    IServiceProvider root,
    Func<Type, bool> isModuleType,
    IEnumerable<ServiceDescriptor> rootOpenGenerics) : IRegistrationSource
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
        if (registrationAccessor(service).Any())
            yield break; // the module registers it itself
        if (isModuleType(type))
        {
            // A closed generic over a module type: closed HERE, from the root's open generic.
            if (type.IsConstructedGenericType
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
        if (root.GetService<IServiceProviderIsService>() is { } isService && !isService.IsService(type))
            yield break;
        yield return RegistrationBuilder
            .ForDelegate(type, (_, _) => root.GetRequiredService(type))
            .ExternallyOwned()
            .InstancePerDependency()
            .CreateRegistration();
    }
}
