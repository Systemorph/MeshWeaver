#nullable enable
using System.Collections.Immutable;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.ServiceProvider;

/// <summary>
/// How a reflection-activated registration is built: its public constructors, longest first, each with
/// what every parameter is resolved as. Computed once per registration (never per process — a cache keyed
/// by type in a static would pin every collectible type it ever saw) and read without locks.
/// </summary>
internal sealed class ConstructorPlan
{
    private ConstructorPlan(ImmutableArray<ConstructorChoice> constructors) => Constructors = constructors;

    /// <summary>The public constructors, most parameters first.</summary>
    internal ImmutableArray<ConstructorChoice> Constructors { get; }

    internal static ConstructorPlan For(Type implementationType)
    {
        if (implementationType.IsAbstract || implementationType.IsInterface)
            throw new InvalidOperationException(
                $"Cannot build '{implementationType.FullName}': it is abstract or an interface, so it has no constructor to call.");
        var constructors = implementationType
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Select(c => new ConstructorChoice(c, c.GetParameters().Select(ParameterChoice.For).ToImmutableArray()))
            .OrderByDescending(c => c.Parameters.Length)
            .ToImmutableArray();
        if (constructors.IsEmpty)
            throw new InvalidOperationException(
                $"Cannot build '{implementationType.FullName}': it has no public constructor.");
        return new ConstructorPlan(constructors);
    }
}

/// <summary>One public constructor and its parameters.</summary>
/// <param name="Constructor">The constructor.</param>
/// <param name="Parameters">What each parameter is resolved as.</param>
internal sealed record ConstructorChoice(ConstructorInfo Constructor, ImmutableArray<ParameterChoice> Parameters);

/// <summary>What one constructor parameter is resolved as.</summary>
/// <param name="Parameter">The parameter.</param>
/// <param name="IsServiceKey">Whether it takes the key the service was asked for (<see cref="ServiceKeyAttribute"/>).</param>
/// <param name="KeyMode">How the key of the dependency is chosen (<see cref="FromKeyedServicesAttribute"/>); null for unkeyed.</param>
/// <param name="ExplicitKey">The explicit key, when <paramref name="KeyMode"/> says so.</param>
internal sealed record ParameterChoice(ParameterInfo Parameter, bool IsServiceKey, ServiceKeyLookupMode? KeyMode, object? ExplicitKey)
{
    internal static ParameterChoice For(ParameterInfo parameter)
    {
        if (parameter.GetCustomAttribute<ServiceKeyAttribute>() is not null)
            return new ParameterChoice(parameter, true, null, null);
        if (parameter.GetCustomAttribute<FromKeyedServicesAttribute>() is { } keyed)
            return new ParameterChoice(parameter, false, keyed.LookupMode, keyed.Key);
        return new ParameterChoice(parameter, false, null, null);
    }

    /// <summary>The key the dependency is looked up under, for a service asked for under <paramref name="requestedKey"/>.</summary>
    internal object? KeyFor(object? requestedKey) => KeyMode switch
    {
        null => null,
        ServiceKeyLookupMode.InheritKey => requestedKey,
        ServiceKeyLookupMode.NullKey => null,
        _ => ExplicitKey,
    };
}
