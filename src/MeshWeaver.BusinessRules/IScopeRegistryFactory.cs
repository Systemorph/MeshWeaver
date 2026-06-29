using System.Collections.Immutable;
using System.Reflection;

namespace MeshWeaver.BusinessRules;

/// <summary>
/// Factory that creates <c>ScopeRegistry</c> instances bound to a given state.
/// </summary>
public interface IScopeRegistryFactory
{
    /// <summary>
    /// Creates a new <c>ScopeRegistry</c> bound to the supplied state.
    /// </summary>
    /// <typeparam name="TState">The type of the shared state carried by the registry.</typeparam>
    /// <param name="state">The state instance shared across all scopes created from the registry.</param>
    /// <returns>A new registry for the given state.</returns>
    ScopeRegistry<TState> Create<TState>(TState state);
}

/// <summary>
/// Factory that instantiates concrete scope implementations for a requested scope type and identity.
/// </summary>
public interface IScopeFactory
{
    /// <summary>
    /// Creates the concrete scope implementation registered for <typeparamref name="TScope"/>.
    /// </summary>
    /// <typeparam name="TScope">The scope type (typically an interface) to instantiate.</typeparam>
    /// <typeparam name="TState">The type of the shared state carried by the owning registry.</typeparam>
    /// <param name="identity">The identity the scope is keyed by.</param>
    /// <param name="state">The registry that owns the scope and supplies its shared state.</param>
    /// <returns>The newly created scope instance, boxed as <see cref="object"/>.</returns>
    object Create<TScope, TState>(object identity, ScopeRegistry<TState> state);
}

internal class ScopeFactory(Assembly[] assemblies) : IScopeFactory
{
    private readonly IReadOnlyDictionary<Type, Type> scopeImplementations = assemblies
        .SelectMany(a => a.GetTypes()
            .Where(t => !t.IsAbstract && t.BaseType?.IsGenericType == true && t.BaseType.GetGenericTypeDefinition() == typeof(ScopeBase<,,>))
        )
        .ToDictionary(t => t.BaseType!.GetGenericArguments().First());
    public object Create<TScope, TState>(object identity, ScopeRegistry<TState> state)
    {
        if (!scopeImplementations.TryGetValue(typeof(TScope), out var type))
            throw new NotSupportedException($"No implementation found for type {typeof(TScope).Name}. Is the compiler installed?");
        return (TScope)Activator.CreateInstance(type, identity, state)!;
    }
}

internal class ScopeRegistryFactory(IScopeFactory scopeFactory) : IScopeRegistryFactory
{
    public ScopeRegistry<TState> Create<TState>(TState state)
    {
        return new(state, scopeFactory);
    }
}

/// <summary>
/// Holds the shared state and caches the scope instances created for it, ensuring a single
/// scope instance per (scope type, identity) pair.
/// </summary>
/// <typeparam name="TState">The type of the shared state carried by the registry and its scopes.</typeparam>
/// <param name="state">The state instance shared across all scopes created from this registry.</param>
/// <param name="scopeFactory">The factory used to instantiate concrete scope implementations on first access.</param>
public class ScopeRegistry<TState>(TState state, IScopeFactory scopeFactory)
{
    private ImmutableDictionary<(Type ScopeType, object Identity), object> scopes = ImmutableDictionary<(Type ScopeType, object Identity), object>.Empty;
    private readonly object locker = new();
    /// <summary>Gets the shared state carried by this registry.</summary>
    public TState State => state;
    /// <summary>
    /// Returns the cached scope of type <typeparamref name="TScope"/> for the given identity,
    /// creating and caching it on first access.
    /// </summary>
    /// <typeparam name="TScope">The scope type to resolve.</typeparam>
    /// <param name="identity">The identity the requested scope is keyed by.</param>
    /// <returns>The scope instance of type <typeparamref name="TScope"/> for the given identity.</returns>
    public TScope GetScope<TScope>(object identity) where TScope : IScope
    {
        var key = (typeof(TScope), identity);
        lock (locker)
        {
            if (scopes.TryGetValue(key, out var scope))
            {
                return (TScope)scope;
            }
            var newScope = scopeFactory.Create<TScope, TState>(identity, this);
            scopes = scopes.Add(key, newScope);
            return (TScope)newScope;

        }
    }
}
