using MeshWeaver.ServiceProvider;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Messaging;

/// <summary>
/// An <see cref="IServiceProviderFactory{TContainerBuilder}"/> that builds the
/// MeshWeaver service provider and registers a hub factory so the root
/// <see cref="IMessageHub"/> can be resolved from the container.
/// </summary>
/// <param name="factory">Factory that creates the root hub from the built service provider.</param>
public class MessageHubServiceProviderFactory(
    Func<IServiceProvider, IMessageHub> factory
) : IServiceProviderFactory<IServiceCollection>
{
    /// <summary>
    /// Returns the service collection unchanged as the container builder.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection.</returns>
    public IServiceCollection CreateBuilder(IServiceCollection services) => services;

    /// <summary>
    /// Registers the hub factory as a singleton and builds the MeshWeaver
    /// service provider from the collection.
    /// </summary>
    /// <param name="services">The service collection to build from.</param>
    /// <returns>The constructed service provider.</returns>
    public IServiceProvider CreateServiceProvider(IServiceCollection services)
    {
        services.AddSingleton(factory);
        return services.CreateMeshWeaverServiceProvider();
    }
}
