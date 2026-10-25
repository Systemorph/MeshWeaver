#nullable enable
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
/// <para>Honoured by every container MeshWeaver builds (<see cref="MeshServiceProvider"/>); Microsoft's
/// own provider would read it as an ordinary, owned factory.</para>
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
}
