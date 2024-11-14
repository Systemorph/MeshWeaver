using System.Collections.Immutable;
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
                .AddScoped<IScopeFactory>(sp => new ScopeFactory(assemblies))
    );
}

public interface IScopeRegistryFactory
{
    ScopeRegistry<TState> Create<TState>(TState state);
}

public interface IScopeFactory
{
    object Create<TScope, TState>(object identity, TState state);
}

public class ScopeFactory(Assembly[] assemblies) : IScopeFactory
{
    private readonly IReadOnlyDictionary<Type, Type> scopeImplementations = assemblies
        .SelectMany(a => a.GetTypes()
            .Where(t => !t.IsAbstract && t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ScopeBase<,,>))
        )
        .ToDictionary(t => t.GetGenericArguments().First());
    public object Create<TScope, TState>(object identity, TState state)
    {
        if (!scopeImplementations.TryGetValue(typeof(TScope), out var type))
            throw new NotSupportedException($"No implementation found for type {typeof(TScope).Name}. Is the compiler installed?");
        return Activator.CreateInstance(type, identity, state);
    }
}

public class ScopeRegistryFactory(IScopeFactory scopeFactory) : IScopeRegistryFactory
{
    public ScopeRegistry<TState> Create<TState>(TState state)
    {
        return new(state, scopeFactory);
    }
}

public partial class ScopeRegistry<TState>(TState state, IScopeFactory scopeFactory)
{
    private ImmutableDictionary<(Type ScopeType, object Identity), object> scopes = ImmutableDictionary<(Type ScopeType, object Identity), object>.Empty;
    private readonly object locker = new();
    public TState State => state;
    public TScope GetScope<TScope>(object identity) where TScope : IScope
    {
        var key = (typeof(TScope), identity);
        lock (locker)
        {
            if (scopes.TryGetValue(key, out var scope))
            {
                return (TScope)scope;
            }
            var newScope = scopeFactory.Create<TScope, TState>(identity, state);
            scopes = scopes.Add(key, newScope);
            return (TScope)newScope;

        }
    }
}
