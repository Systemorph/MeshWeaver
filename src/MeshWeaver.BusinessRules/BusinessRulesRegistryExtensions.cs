using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.BusinessRules;

/// <summary>
/// Extension methods for registering the business-rules scope infrastructure
/// (scope factories and registries) with a dependency-injection container.
/// </summary>
public static class BusinessRulesRegistryExtensions
{

    /// <summary>
    /// Registers the business-rules services (the scope registry factory and the scope factory)
    /// in the supplied service collection, scanning the given assemblies for scope implementations.
    /// </summary>
    /// <param name="services">The service collection to add the business-rules services to.</param>
    /// <param name="assemblies">The assemblies to scan for concrete scope implementations.</param>
    /// <returns>The same service collection, to allow chaining.</returns>
    public static IServiceCollection AddBusinessRules(this IServiceCollection services, params Assembly[] assemblies)
        =>
            services
                .AddScoped<IScopeRegistryFactory, ScopeRegistryFactory>()
                .AddScoped<IScopeFactory>(_ => new ScopeFactory(assemblies));

    /// <summary>
    /// Creates a new <c>ScopeRegistry</c> bound to the supplied state, using the
    /// <c>IScopeRegistryFactory</c> resolved from the service provider.
    /// </summary>
    /// <typeparam name="TState">The type of the shared state carried by the registry and its scopes.</typeparam>
    /// <param name="serviceProvider">The service provider used to resolve the registry factory.</param>
    /// <param name="state">The state instance shared across all scopes created from the registry.</param>
    /// <returns>A new <c>ScopeRegistry</c> for the given state.</returns>
    public static ScopeRegistry<TState> CreateScopeRegistry<TState>(this IServiceProvider serviceProvider, TState state)
    {
        return serviceProvider.GetRequiredService<IScopeRegistryFactory>().Create(state);
    }
}
