using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.BusinessRules;

public static class BusinessRulesRegistryExtensions
{

    public static IServiceCollection AddBusinessRules(this IServiceCollection services, params Assembly[] assemblies)
        =>
            services
                .AddScoped<IScopeRegistryFactory, ScopeRegistryFactory>()
                .AddScoped<IScopeFactory>(_ => new ScopeFactory(assemblies));

    public static ScopeRegistry<TState> CreateScopeRegistry<TState>(this IServiceProvider serviceProvider, TState state)
    {
        return serviceProvider.GetRequiredService<IScopeRegistryFactory>().Create(state);
    }
}
