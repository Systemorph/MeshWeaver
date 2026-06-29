using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Messaging;

/// <summary>
/// Dependency-injection extensions for registering a message hub in a
/// <see cref="IServiceCollection"/>.
/// </summary>
public static class MessageHubDependencyInjectionExtensions
{
    /// <summary>
    /// Registers a singleton <see cref="IMessageHub"/> for the given address,
    /// constructed via <c>CreateMessageHub</c> with the supplied configuration.
    /// </summary>
    /// <param name="services">The service collection to add the hub to.</param>
    /// <param name="address">The address that identifies the hub.</param>
    /// <param name="configure">Transform applied to the hub's configuration at build time.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddMessageHubs(this IServiceCollection services, Address address,
        Func<MessageHubConfiguration, MessageHubConfiguration> configure)
    {
        return services.AddSingleton(sp => sp.CreateMessageHub(address, configure));
    }
}
