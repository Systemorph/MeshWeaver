#nullable enable
using Autofac;
using Autofac.Builder;
using Autofac.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.ServiceProvider;

/// <summary>
/// A factory registration whose instances the resolving scope does NOT own: the scope never disposes
/// what the factory returns. For a FORWARDER — a registration that hands out an instance some other
/// container owns and disposes (a module generation's singleton, the module scope a caller owns).
///
/// <para>🚨 <b>Why.</b> An ordinary factory registration is owned by the scope that resolves it, so
/// whatever the factory returns is disposed when that scope ends. A root forwarder that returned a
/// module's SINGLETON then had it disposed by the first Blazor circuit (or request, or hub scope) that
/// ended — under every other live user — and a module's SCOPED instance, already owned by the module
/// scope its caller owns, was disposed twice. Ownership stays with the container that built the
/// instance; the forwarder only forwards.</para>
///
/// <para>Honoured by every container MeshWeaver builds (<see cref="NotOwnedServiceDescriptorExtensions.PopulateMeshWeaver"/>);
/// a plain <c>Populate</c> or Microsoft's own provider would read it as an ordinary, owned factory.</para>
/// </summary>
public sealed class NotOwnedServiceDescriptor : ServiceDescriptor
{
    private NotOwnedServiceDescriptor(Type serviceType, Func<IServiceProvider, object> factory, ServiceLifetime lifetime)
        : base(serviceType, factory, lifetime)
    {
    }

    private NotOwnedServiceDescriptor(Type serviceType, object? serviceKey, Func<IServiceProvider, object?, object> factory, ServiceLifetime lifetime)
        : base(serviceType, serviceKey, factory, lifetime)
    {
    }

    /// <summary>An unkeyed forwarder of <paramref name="serviceType"/>.</summary>
    /// <param name="serviceType">The service type.</param>
    /// <param name="factory">Returns the instance some other container owns.</param>
    /// <param name="lifetime">The forwarder's own lifetime — how long the root caches what it returned.</param>
    public static ServiceDescriptor Forwarder(Type serviceType, Func<IServiceProvider, object> factory, ServiceLifetime lifetime) =>
        new NotOwnedServiceDescriptor(serviceType, factory, lifetime);

    /// <summary>A forwarder of <paramref name="serviceType"/> under <paramref name="serviceKey"/>.</summary>
    /// <param name="serviceType">The service type.</param>
    /// <param name="serviceKey">The service key.</param>
    /// <param name="factory">Returns the instance some other container owns.</param>
    /// <param name="lifetime">The forwarder's own lifetime.</param>
    public static ServiceDescriptor KeyedForwarder(Type serviceType, object? serviceKey,
        Func<IServiceProvider, object?, object> factory, ServiceLifetime lifetime) =>
        new NotOwnedServiceDescriptor(serviceType, serviceKey, factory, lifetime);

    /// <summary>Registers this forwarder on <paramref name="builder"/> as Autofac's <c>Populate</c> would,
    /// but externally owned.</summary>
    internal void Register(ContainerBuilder builder)
    {
        var serviceType = ServiceType;
        var registration = IsKeyedService
            ? RegistrationBuilder.ForDelegate(serviceType, (context, _) =>
                    KeyedImplementationFactory!(context.Resolve<IServiceProvider>(), ServiceKey))
                .Keyed(ServiceKey!, serviceType)
            : RegistrationBuilder.ForDelegate(serviceType, (context, _) =>
                    ImplementationFactory!(context.Resolve<IServiceProvider>()))
                .As(serviceType);
        registration.ExternallyOwned();
        _ = Lifetime switch
        {
            ServiceLifetime.Singleton => registration.SingleInstance(),
            ServiceLifetime.Scoped => registration.InstancePerLifetimeScope(),
            _ => registration.InstancePerDependency(),
        };
        builder.RegisterComponent(registration.CreateRegistration());
    }
}

/// <summary>Populating a container so <see cref="NotOwnedServiceDescriptor"/> is honoured.</summary>
public static class NotOwnedServiceDescriptorExtensions
{
    /// <summary>
    /// <c>Populate</c>, with every <see cref="NotOwnedServiceDescriptor"/> registered externally owned AT ITS
    /// OWN POSITION — so which registration of a service is the default, and the order of an
    /// <c>IEnumerable</c> of it, are exactly those of the collection.
    ///
    /// <para>The position is kept by registering the forwarder while <c>Populate</c> walks the collection:
    /// Autofac registers each descriptor as it reaches it, in order, so a forwarder registered as the walk
    /// passes it sits exactly where it stood. <c>ANotOwnedForwarderKeepsItsPositionTest</c> measures it.</para>
    /// </summary>
    /// <param name="builder">The container builder.</param>
    /// <param name="services">The registrations, in order.</param>
    public static void PopulateMeshWeaver(this ContainerBuilder builder, IEnumerable<ServiceDescriptor> services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var materialized = services as IReadOnlyCollection<ServiceDescriptor> ?? services.ToArray();
        PopulateMeshWeaverWithOwners(builder, materialized,
            materialized.Select(d => d.IsKeyedService ? null : d.ImplementationInstance).OfType<ModuleInstanceOwners>().LastOrDefault());
    }

    /// <summary>As <see cref="PopulateMeshWeaver(ContainerBuilder, IEnumerable{ServiceDescriptor})"/>, with the
    /// mesh's <see cref="ModuleInstanceOwners"/> — when there is one, a scope about to dispose a module-owned
    /// instance it does not own says so (MeshWeaver#6588, <see cref="ModuleScopeOwnership"/>).</summary>
    internal static void PopulateMeshWeaverWithOwners(this ContainerBuilder builder, IEnumerable<ServiceDescriptor> services,
        ModuleInstanceOwners? owners)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(services);
        if (owners is not null)
            ModuleScopeOwnership.WatchOwnershipTransfers(builder, owners);
        builder.Populate(InOrder(builder, services));
    }

    private static IEnumerable<ServiceDescriptor> InOrder(ContainerBuilder builder, IEnumerable<ServiceDescriptor> services)
    {
        foreach (var descriptor in services)
        {
            if (descriptor is NotOwnedServiceDescriptor notOwned)
                notOwned.Register(builder);
            else
                yield return descriptor;
        }
    }
}
