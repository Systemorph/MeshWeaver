#nullable enable
using System.Collections.Frozen;
using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.ServiceProvider;

/// <summary>A service type and key — what a lookup asks for. A null key is the unkeyed service.</summary>
/// <param name="Type">The service type.</param>
/// <param name="Key">The service key, or null.</param>
internal readonly record struct ServiceIdentifier(Type Type, object? Key);

/// <summary>
/// ONE level's own registrations, frozen when the level is built: the descriptors it was given, in
/// order, and nothing copied from any other level. Read without locks by every resolve.
/// </summary>
internal sealed class ServiceRegistry
{
    private readonly FrozenDictionary<ServiceIdentifier, ImmutableArray<ServiceRegistration>> exact;
    private readonly FrozenDictionary<ServiceIdentifier, ImmutableArray<ServiceRegistration>> open;
    private readonly FrozenDictionary<Type, ImmutableArray<ServiceRegistration>> keyedByType;

    internal ServiceRegistry(
        IEnumerable<ServiceDescriptor> descriptors,
        ImmutableArray<IServiceRegistrationSource> sources,
        ImmutableArray<IActivationObserver> observers)
    {
        var all = ImmutableArray.CreateBuilder<ServiceRegistration>();
        var order = 0L;
        foreach (var descriptor in descriptors)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            var registration = ServiceRegistration.FromDescriptor(descriptor, order++);
            registration.Registry = this;
            all.Add(registration);
        }
        All = all.ToImmutable();
        exact = All.Where(r => !r.ServiceType.IsGenericTypeDefinition)
            .GroupBy(r => new ServiceIdentifier(r.ServiceType, r.ServiceKey))
            .ToFrozenDictionary(g => g.Key, g => g.ToImmutableArray());
        open = All.Where(r => r.ServiceType.IsGenericTypeDefinition && r.ImplementationType is not null)
            .GroupBy(r => new ServiceIdentifier(r.ServiceType, r.ServiceKey))
            .ToFrozenDictionary(g => g.Key, g => g.ToImmutableArray());
        keyedByType = All.Where(r => r.ServiceKey is not null)
            .GroupBy(r => r.ServiceType)
            .ToFrozenDictionary(g => g.Key, g => g.ToImmutableArray());
        Sources = sources;
        Observers = observers;
    }

    /// <summary>Every registration of the level, in registration order.</summary>
    internal ImmutableArray<ServiceRegistration> All { get; }

    /// <summary>The level's registration sources — asked last, never cached.</summary>
    internal ImmutableArray<IServiceRegistrationSource> Sources { get; }

    /// <summary>Who watches the activations of this level's own registrations.</summary>
    internal ImmutableArray<IActivationObserver> Observers { get; }

    /// <summary>How many registrations the level holds — what a hub's own DI cost is measured by.</summary>
    internal int Count => All.Length;

    /// <summary>The registration a single resolve of <paramref name="id"/> answers on this level, from the
    /// level's own registrations only (no source): the last exact one, else the last open generic that
    /// closes over it, else — for a keyed request — the last registration under
    /// <see cref="KeyedService.AnyKey"/>.</summary>
    internal ServiceRegistration? Default(ServiceIdentifier id)
    {
        if (exact.TryGetValue(id, out var exactMatches))
            return exactMatches[^1];
        if (LastOpen(id) is { } closed)
            return closed;
        if (id.Key is not null && !ReferenceEquals(id.Key, KeyedService.AnyKey))
        {
            var any = id with { Key = KeyedService.AnyKey };
            if (exact.TryGetValue(any, out var anyMatches))
                return anyMatches[^1];
            return LastOpen(any, id.Key);
        }
        return null;
    }

    private ServiceRegistration? LastOpen(ServiceIdentifier id, object? requestedKey = null)
    {
        if (!id.Type.IsConstructedGenericType
            || !open.TryGetValue(new ServiceIdentifier(id.Type.GetGenericTypeDefinition(), id.Key), out var candidates))
            return null;
        for (var i = candidates.Length - 1; i >= 0; i--)
            if (candidates[i].Close(id.Type, requestedKey ?? id.Key) is { } closed)
                return closed;
        return null;
    }

    /// <summary>Every registration of <paramref name="id"/> on this level, in registration order — exact
    /// and open-generic alike (no source).</summary>
    internal IReadOnlyList<ServiceRegistration> AllOf(ServiceIdentifier id)
    {
        if (id.Key is not null && ReferenceEquals(id.Key, KeyedService.AnyKey))
            return keyedByType.TryGetValue(id.Type, out var keyed)
                ? keyed.Where(r => !ReferenceEquals(r.ServiceKey, KeyedService.AnyKey)).ToArray()
                : [];
        var hasExact = exact.TryGetValue(id, out var exactMatches);
        if (!id.Type.IsConstructedGenericType
            || !open.TryGetValue(new ServiceIdentifier(id.Type.GetGenericTypeDefinition(), id.Key), out var candidates))
            return hasExact ? exactMatches : [];
        var closed = candidates.Select(c => c.Close(id.Type, id.Key)).OfType<ServiceRegistration>();
        return hasExact
            ? exactMatches.Concat(closed).OrderBy(r => r.Order).ToArray()
            : closed.ToArray();
    }

    /// <summary>Whether the level registers <paramref name="id"/> itself (no source).</summary>
    internal bool Has(ServiceIdentifier id) => Default(id) is not null;
}
