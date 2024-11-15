using System.Reflection;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.BusinessRules;

public static class BusinessRulesRegistryExtensions
{
    public static MessageHubConfiguration AddBusinessRules(this MessageHubConfiguration configuration, params Assembly[] assemblies)
        => configuration.WithServices(services =>
            services
                .AddScoped<IScopeRegistryFactory, ScopeRegistryFactory>()
                .AddScoped<IScopeFactory>(_ => new ScopeFactory(assemblies))
        );

    public static ScopeRegistry<TState> GetScopeRegistry<TState>(this IMessageHub hub, TState state)
    {
        return hub.ServiceProvider.GetRequiredService<IScopeRegistryFactory>().Create(state);
    }
}
